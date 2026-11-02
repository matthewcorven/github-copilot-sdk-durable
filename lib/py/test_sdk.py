import asyncio
import os
import tempfile
import unittest
import json
from unittest.mock import patch, AsyncMock
from types import SimpleNamespace
from pathlib import Path

from durable import (
    AgentChoices, DurableHarness, DurableStore, DurableStoreOptions, ExtensionRegistry,
    DurableSandboxPolicy, DurableSandboxFilesystem, DurableSandboxCredentials,
    DurableSandboxNetwork, DurableSandboxAuth, PermissionPolicy,
    DurableTask, TaskCommit, TaskStatus, Extension, ToolWrapper, DurableHarnessOptions,
    DurableClientBuilder,
    DurableConversation,
    DocumentDefinition, DocumentHistory, ForkPolicy, PromptSection, ForkIntent, ForkIntentState, FORK_DOCUMENT,
    DurableRecoveryRequiredException,
)
from copilot.tools import Tool, ToolResult

REPLAY_FIXTURES = Path(__file__).resolve().parents[2] / "tools" / "ReplayRuntime" / "fixtures"

def sdk_session(session_id="native", history=None):
    listeners = []
    def subscribe(handler):
        listeners.append(handler)
        return lambda: listeners.remove(handler)
    def emit(event):
        for listener in list(listeners):
            listener(event)
    return SimpleNamespace(
        session_id=session_id, on=subscribe, emit=emit, listeners=listeners,
        get_events=AsyncMock(return_value=history or []), send=AsyncMock(return_value="sent"),
        set_tools=AsyncMock(),
    )


def sdk_client(session):
    return SimpleNamespace(
        stop=AsyncMock(), create_session=AsyncMock(return_value=session),
        resume_session=AsyncMock(return_value=session),
        rpc=SimpleNamespace(sessions=SimpleNamespace(fork=AsyncMock(
            return_value=SimpleNamespace(session_id="child")))),
    )


class RecoveryTests(unittest.IsolatedAsyncioTestCase):
    async def test_jsonl_noop_commit_after_reopen_preserves_document_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "store.jsonl"
            options = DurableStoreOptions(path=str(path))
            store = DurableStore.create(options)
            try:
                notes: DocumentDefinition[dict[str, str]] = DocumentDefinition(
                    "notes", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF)
                async def create(tx):
                    conversation = tx.create_conversation()
                    draft = tx.doc(notes, conversation.id)
                    draft.value = {"text": "saved"}
                    draft.save(tx.append_entry(conversation.id, "note"))
                    return conversation.id
                identifier = await store.commit_async(create)
                store.close()
                store = DurableStore.create(options)
                saved = path.read_bytes()
                async def noop(_tx):
                    return None
                await store.commit_async(noop)
                self.assertEqual(path.read_bytes(), saved)
                document = store.document(notes.kind, identifier)
                assert document is not None
                self.assertEqual(document.latest, {"text": "saved"})
            finally:
                store.close()

    async def test_completion_timeout_blocks_next_prompt_from_consuming_delayed_native_answer(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        try:
            chat = await harness.root_async()
            first = asyncio.create_task(chat.submit_async("first", timeout=0.01))
            second = asyncio.create_task(chat.submit_async("second", timeout=0.1))
            with self.assertRaises(TimeoutError):
                await first
            async def late_events():
                await asyncio.sleep(0.02)
                session.emit({"type": "assistant.message", "id": "late-answer", "data": {"content": "first answer"}})
                session.emit({"type": "session.idle", "data": {}})
            late = asyncio.create_task(late_events())
            try:
                with self.assertRaises(DurableRecoveryRequiredException):
                    await second
            finally:
                await late
            admission = chat.admission()
            assert admission is not None
            self.assertEqual((admission.prompt, admission.state), ("first", "uncertain"))
            self.assertEqual(session.send.await_count, 1)
            self.assertIsNone(chat.checkpoint())
        finally:
            await harness.close_async()
            store.close()

    async def test_send_failure_preserves_original_exception_and_recovery_gate(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        original = RuntimeError("native send failed")
        session.send.side_effect = original
        try:
            chat = await harness.root_async()
            with self.assertRaises(RuntimeError) as raised:
                await chat.submit_async("first")
            self.assertIs(raised.exception, original)
            with self.assertRaises(DurableRecoveryRequiredException):
                await chat.submit_async("second")
            admission = chat.admission()
            assert admission is not None
            self.assertEqual((admission.prompt, admission.state), ("first", "uncertain"))
            self.assertEqual(session.send.await_count, 1)
            self.assertEqual(len(session.listeners), 1)
        finally:
            await harness.close_async()
            store.close()

    async def test_timeout_bounds_send_and_blocks_queued_and_late_submissions(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        entered, cancelled = asyncio.Event(), asyncio.Event()
        async def send(_prompt):
            entered.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        session.send = AsyncMock(side_effect=send)
        try:
            chat = await harness.root_async()
            start = asyncio.get_running_loop().time()
            first = asyncio.create_task(chat.submit_async("first", timeout=0.02))
            await entered.wait()
            second = asyncio.create_task(chat.submit_async("second", timeout=0.1))
            with self.assertRaises(TimeoutError):
                await asyncio.wait_for(first, 0.3)
            self.assertLess(asyncio.get_running_loop().time() - start, 0.2)
            self.assertTrue(cancelled.is_set())
            with self.assertRaises(DurableRecoveryRequiredException):
                await second
            admission = chat.admission()
            assert admission is not None
            self.assertEqual((admission.prompt, admission.state), ("first", "uncertain"))
            session.emit({"type": "assistant.message", "id": "old-answer", "data": {"content": "first answer"}})
            session.emit({"type": "session.idle", "data": {}})
            with self.assertRaises(DurableRecoveryRequiredException):
                await asyncio.wait_for(chat.submit_async("third"), 0.1)
            self.assertEqual(chat.admission(), admission)
            self.assertIsNone(chat.checkpoint())
            self.assertEqual(session.send.await_count, 1)
            self.assertEqual(len(session.listeners), 1)
            self.assertFalse(chat.busy)
        finally:
            await harness.close_async()
            store.close()

    async def test_cancellation_keeps_original_error_and_blocks_followup_without_orphan_send(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        harness = DurableHarness(sdk_client(session), store, ExtensionRegistry())
        entered, cancelled = asyncio.Event(), asyncio.Event()
        async def send(_prompt):
            entered.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        session.send = AsyncMock(side_effect=send)
        try:
            chat = await harness.root_async()
            task = asyncio.create_task(chat.submit_async("cancel me"))
            await entered.wait()
            task.cancel("caller cancellation")
            with self.assertRaisesRegex(asyncio.CancelledError, "caller cancellation"):
                await task
            self.assertTrue(cancelled.is_set())
            admission = chat.admission()
            assert admission is not None
            self.assertEqual(admission.state, "uncertain")
            with self.assertRaises(DurableRecoveryRequiredException):
                await asyncio.wait_for(chat.submit_async("must not send"), 0.1)
            self.assertEqual(session.send.await_count, 1)
            self.assertEqual(len(session.listeners), 1)
        finally:
            await harness.close_async()
            store.close()

    async def test_history_hydration_is_readonly_and_idempotent_after_actual_store_reopen(self):
        history = [
            {"type": "session.start", "id": "start", "data": {"selectedModel": "reported-model"}},
            {"type": "tool.execution_start", "id": "tool-start", "data": {
                "toolCallId": "call", "toolName": "read", "arguments": {"path": "notes"}}},
            {"type": "tool.execution_complete", "id": "tool-end", "data": {
                "toolCallId": "call", "success": True, "result": {"content": "notes"}}},
            {"type": "assistant.message", "id": "answer", "data": {"content": "final answer"}},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "store.jsonl"
            options = DurableStoreOptions(path=str(path))
            store = DurableStore.create(options)
            session = sdk_session()
            client = sdk_client(session)
            harness = DurableHarness(client, store, ExtensionRegistry())
            try:
                chat = await harness.root_async()
                async def send(_prompt):
                    for event in history:
                        session.emit(event)
                    session.emit({"type": "session.idle", "data": {}})
                    return "sent"
                session.send.side_effect = send
                await chat.submit_async("read")
                identifier, checkpoint, saved = chat.id, chat.checkpoint(), path.read_bytes()
                resumed = sdk_session(history=history)
                client.resume_session.return_value = resumed
                for _ in range(2):
                    await harness.close_async()
                    store.close()
                    store = DurableStore.create(options)
                    harness = DurableHarness(client, store, ExtensionRegistry())
                    chat = await harness.resume_conversation_async(identifier)
                    await chat.checkpoint_barrier_async()
                    self.assertEqual(chat.checkpoint(), checkpoint)
                    self.assertEqual(path.read_bytes(), saved)
                    self.assertEqual(chat.answers, ["final answer"])
                    self.assertEqual((await chat.wait_for_effective_model_async(0.01)).model, "reported-model")
                    chat.detach()
                    await harness.resume_conversation_async(identifier)
                    self.assertEqual(chat.answers, ["final answer"])
                    resumed.emit({**history[2], "id": "redelivered-tool"})
                    await chat.checkpoint_barrier_async()
                    self.assertEqual(path.read_bytes(), saved)
                client.create_session.return_value = sdk_session("created", history)
                created = await harness.create_conversation_async()
                await created.checkpoint_barrier_async()
                self.assertIsNone(created.checkpoint())
                self.assertEqual(created.answers, ["final answer"])
            finally:
                await harness.close_async()
                store.close()

    async def test_pending_abandoned_and_completed_unbound_native_forks_never_create_empty_sessions(self):
        for state in ForkIntentState:
            with self.subTest(state=state):
                store = DurableStore.create_ephemeral()
                session = sdk_session()
                client = sdk_client(session)
                harness = DurableHarness(client, store, ExtensionRegistry())
                try:
                    async def create(tx):
                        parent = tx.create_conversation()
                        boundary = tx.append_entry(parent.id, "boundary")
                        child = tx.create_conversation(parent_conversation_id=parent.id, fork_entry_id=boundary)
                        intent = tx.doc(FORK_DOCUMENT, child.id)
                        intent.value = ForkIntent(parent.id, boundary, None, state)
                        intent.save(tx.append_entry(child.id, "fork"))
                        return child
                    child = await store.commit_async(create)
                    with self.assertRaises(DurableRecoveryRequiredException):
                        await harness.resume_conversation_async(child.id)
                    client.create_session.assert_not_awaited()
                    client.resume_session.assert_not_awaited()
                    restored = harness.find_conversation(child.id)
                    assert restored is not None
                    self.assertIsNone(restored.binding())
                finally:
                    await harness.close_async()
                    store.close()

    async def _fork_at_admission(self, harness, session):
        parent = await harness.root_async()
        boundary = ""
        async def send(_prompt):
            nonlocal boundary
            boundary = await parent.append_entry_async("tool-created")
            session.emit({"type": "assistant.message", "id": "parent-answer", "data": {"content": "parent done"}})
            session.emit({"type": "session.idle", "data": {}})
            return "sent"
        session.send.side_effect = send
        await parent.submit_async("parent")
        return await parent.fork_async(boundary)

    async def test_fork_hydrates_inherited_model_and_answer_without_tool_checkpoint_writes(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        client = sdk_client(session)
        child_session = sdk_session("child", [
            {"type": "session.start", "id": "start", "data": {"selectedModel": "fork-model"}},
            {"type": "tool.execution_complete", "id": "tool", "data": {"toolCallId": "call", "success": True}},
            {"type": "assistant.message", "id": "answer", "data": {"content": "inherited answer"}},
        ])
        client.resume_session.return_value = child_session
        harness = DurableHarness(client, store, ExtensionRegistry())
        try:
            child = await self._fork_at_admission(harness, session)
            self.assertEqual(child.answers, ["inherited answer"])
            self.assertEqual((await child.wait_for_effective_model_async(0.01)).model, "fork-model")
            await child.checkpoint_barrier_async()
            self.assertIsNone(child.checkpoint())
        finally:
            await harness.close_async()
            store.close()

    async def test_inherited_admission_does_not_block_child_resume_but_own_uncertainty_does(self):
        store = DurableStore.create_ephemeral()
        session = sdk_session()
        client = sdk_client(session)
        child_session = sdk_session("child")
        client.resume_session.return_value = child_session
        harness = DurableHarness(client, store, ExtensionRegistry())
        try:
            child = await self._fork_at_admission(harness, session)
            self.assertEqual(child.admission().state, "admitted")
            child.detach()
            self.assertIs(await harness.resume_conversation_async(child.id), child)
            with self.assertRaises(TimeoutError):
                await child.submit_async("child work", timeout=0.01)
            with self.assertRaises(DurableRecoveryRequiredException):
                await asyncio.wait_for(child.submit_async("retry"), 0.1)
            child.detach()
            with self.assertRaises(DurableRecoveryRequiredException):
                await harness.resume_conversation_async(child.id)
        finally:
            await harness.close_async()
            store.close()

    async def test_prompt_sections_preserve_sync_and_async_hook_outputs(self):
        async def async_dict(_input, _invocation):
            return {"additionalContext": "hook context", "modifiedPrompt": "modified"}
        async def async_none(_input, _invocation):
            return None
        for hook in [lambda _i, _v: {"additionalContext": "hook context", "modifiedPrompt": "modified"},
                     lambda _i, _v: None, async_dict, async_none]:
            with self.subTest(hook=hook):
                store = DurableStore.create_ephemeral()
                registry = ExtensionRegistry()
                registry.install(Extension("sections", sections=[
                    PromptSection("notes", AsyncMock(return_value="section content"))]))
                harness = DurableHarness(sdk_client(sdk_session()), store, registry)
                try:
                    choices = AgentChoices(hooks={"on_user_prompt_submitted": hook})
                    chat = await harness.root_async(choices)
                    config = await harness.build_session_config(choices, chat)
                    output = await config["hooks"]["on_user_prompt_submitted"]({"prompt": "original"}, {})
                    expected = await hook({}, {}) if asyncio.iscoroutinefunction(hook) else hook({}, {})
                    self.assertEqual(output, {
                        **(expected or {}),
                        "additionalContext": ((expected or {}).get("additionalContext", "") + "\n\n"
                                              if expected else "") + "<notes>\nsection content\n</notes>",
                    })
                finally:
                    await harness.close_async()
                    store.close()


class SdkTests(unittest.IsolatedAsyncioTestCase):
    @unittest.skipUnless(os.getenv("DURABLE_RUNTIME_PATH"), "Requires actual recorded runtime")
    async def test_official_sdk_models_envelope_support_and_raw_shape_limit(self):
        # PR123 PRRT_kwDOVUQtAs6yggpP: intercept only the test RPC boundary, not SDK fields.
        from copilot._jsonrpc import JsonRpcClient
        model = {
            "id": "replay-model", "name": "Replay model",
            "capabilities": {"supports": {}, "limits": {"max_context_window_tokens": 8192}},
            "supportedReasoningEfforts": ["low"],
        }
        for shape in ["models", "list", "array"]:
            with self.subTest(shape=shape):
                store = DurableStore.create_ephemeral()
                harness = await DurableHarness.open_async(store, ExtensionRegistry())
                original = JsonRpcClient.request
                async def request(client, method, params=None, **kwargs):
                    if method == "models.list":
                        return [model] if shape == "array" else {shape: [model]}
                    return await original(client, method, params, **kwargs)
                try:
                    with patch.object(JsonRpcClient, "request", request):
                        choices = AgentChoices(model="replay-model", reasoning_effort="minimal")
                        if shape == "array":
                            with self.assertRaisesRegex(AttributeError, "get"):
                                await harness.build_session_config(choices)
                        else:
                            config = await harness.build_session_config(choices)
                            if shape == "models":
                                self.assertEqual(config["reasoning_effort"], "minimal")
                                models = await harness.client.list_models()
                                self.assertEqual(models[0].supported_reasoning_efforts, ["low"])
                            else:
                                self.assertNotIn("reasoning_effort", config)
                                self.assertEqual(await harness.client.list_models(), [])
                finally:
                    await harness.close_async()
                    store.close()

    async def test_host_hook_tolerance_is_opt_in_and_preserves_error_and_answer(self):
        from unittest.mock import Mock
        for tolerant, message, succeeds, emit_answer in [
            (False, "postToolUse hook: Operation not permitted", False, True),
            (True, "postToolUse hook: Operation not permitted", True, True),
            (True, "hook timed out", False, True),
            (True, "Operation not permitted", False, True),
            (True, "postToolUse hook: Operation not permitted", True, False),
        ]:
            store = DurableStore.create_ephemeral()
            client = SimpleNamespace(stop=AsyncMock())
            harness = DurableHarness(client, store, ExtensionRegistry(),
                                     DurableHarnessOptions(tolerate_host_hook_failures=tolerant))
            listeners = []
            def subscribe(handler):
                listeners.append(handler)
                return lambda: listeners.remove(handler)
            answer = {"type": "assistant.message", "id": "answer", "data": {"content": "actual event"}}
            async def send(_prompt):
                events = ([answer] if emit_answer else []) + [{"type": "session.error", "data": {"message": message}}]
                for event in events:
                    for listener in list(listeners):
                        listener(event)
                return "message"
            session = Mock(session_id="sdk-session")
            session.on = subscribe
            session.send = send
            try:
                async def create(tx):
                    return tx.create_conversation()
                record = await store.commit_async(create)
                chat = harness.find_conversation(record.id)
                assert chat is not None
                chat.attach(session)
                if succeeds:
                    self.assertIs(await chat.submit_async("hello", timeout=1), answer if emit_answer else None)
                else:
                    with self.assertRaisesRegex(RuntimeError, message):
                        await chat.submit_async("hello", timeout=1)
                self.assertEqual(chat.answers, ["actual event"] if emit_answer else [])
                self.assertEqual(DurableConversation.is_host_hook_failure(message),
                                 "hook" in message.lower() and "Operation not permitted" in message)
            finally:
                await harness.close_async()
                store.close()

    async def test_close_stops_sdk_after_checkpoint_failure(self):
        store = DurableStore.create_ephemeral()
        client = SimpleNamespace(stop=AsyncMock())
        harness = DurableHarness(client, store, ExtensionRegistry())
        try:
            async def create(tx):
                return tx.create_conversation()
            record = await store.commit_async(create)
            chat = harness.find_conversation(record.id)
            assert chat is not None
            chat.checkpoint_barrier_async = AsyncMock(side_effect=RuntimeError("checkpoint failed"))
            with self.assertRaisesRegex(RuntimeError, "checkpoint failed"):
                await harness.close_async()
            await harness.close_async()
            client.stop.assert_awaited_once()
        finally:
            store.close()

    async def test_jsonl_exact_crlf_mixed_utf8_and_unterminated_tail_boundaries(self):
        from durable import DurableStoreFormat
        for endings in [("\r\n", "\r\n"), ("\n", "\r\n")]:
            for tail in ["", '{"v":1', json.dumps({
                "v": 1, "seq": 3, "type": "conversation",
                "payload": {"id": "torn", "createdAt": "2026-11-01T00:00:00Z"},
            })]:
                with tempfile.TemporaryDirectory() as directory:
                    path = os.path.join(directory, "store.jsonl")
                    ids = ["\u96ea\u00e9", "\U0001f680"]
                    prefix = "".join(json.dumps({
                        "v": 1, "seq": i + 1, "type": "conversation",
                        "payload": {"id": identifier, "createdAt": "2026-11-01T00:00:00Z"},
                    }, ensure_ascii=False) + endings[i] for i, identifier in enumerate(ids)).encode("utf-8")
                    with open(path, "wb") as handle:
                        handle.write(prefix + tail.encode("utf-8"))
                    options = DurableStoreOptions(path=path, format=DurableStoreFormat.JSONL)
                    store = DurableStore.create(options)
                    try:
                        self.assertEqual([c.id for c in store.conversations], ids)
                        with open(path, "rb") as handle:
                            self.assertEqual(handle.read(), prefix)
                        async def create(tx):
                            return tx.create_conversation()
                        added = await store.commit_async(create)
                        store.close()
                        store = DurableStore.create(options)
                        self.assertEqual([c.id for c in store.conversations], ids + [added.id])
                    finally:
                        store.close()

    async def test_jsonl_all_tail_file_truncates_to_zero(self):
        from durable import DurableStoreFormat
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "store.jsonl")
            for tail in [b'{"v":1', b'{"v":1,"seq":1,"type":"conversation","payload":{"id":"tail"}}']:
                with open(path, "wb") as handle:
                    handle.write(tail)
                store = DurableStore.create(DurableStoreOptions(path=path, format=DurableStoreFormat.JSONL))
                try:
                    self.assertEqual(len(store.conversations), 0)
                    self.assertEqual(os.path.getsize(path), 0)
                finally:
                    store.close()
    def test_sandbox_wire_and_boundary_policy(self):
        config = DurableHarness.build_sandbox_config(DurableSandboxPolicy(
            enabled=True, allow_bypass=False, sandbox_mcp_servers=False,
            sandbox_lsp_servers=True, keychain_access=False,
            auth=DurableSandboxAuth(gh=True, git=False),
            filesystem=DurableSandboxFilesystem(denied_paths=["{workspace}/private"],
                readonly_paths=[], readwrite_paths=["{workspace}"]),
            credentials=DurableSandboxCredentials(env_vars={"TOKEN": ["api.example.com"]}),
            network=DurableSandboxNetwork(allow_outbound=False, allow_local_network=True,
                allowed_hosts=["example.com"], blocked_hosts=["bad.com"]),
        ), "/work").to_dict()
        self.assertEqual(config["userPolicy"]["filesystem"]["deniedPaths"], ["/work/private"])
        self.assertEqual(config["credentials"], {"envVars": {"TOKEN": {"injectHosts": ["api.example.com"]}}})
        self.assertEqual(config["userPolicy"]["seatbelt"], {"keychainAccess": False})
        self.assertNotIn("experimental", config["userPolicy"])
        self.assertIs(config["allowBypass"], False)
        self.assertIs(config["sandboxMcpServers"], False)
        self.assertIs(config["userPolicy"]["network"]["allowOutbound"], False)
        self.assertFalse(PermissionPolicy.is_granted(["read:/tmp/foo"], "read", "/tmp/foobar"))
        self.assertTrue(PermissionPolicy.is_granted(["read:/tmp/foo"], "read", "/tmp/foo/bar"))

    @unittest.skipUnless(os.getenv("DURABLE_RUNTIME_PATH"), "Requires actual recorded runtime")
    async def test_real_sdk_lifecycle_reopen_and_models(self):
        with tempfile.TemporaryDirectory() as directory:
            options = DurableStoreOptions(path=os.path.join(directory, "store.jsonl"))
            store = DurableStore.create(options)
            harness = await DurableHarness.open_async(store, ExtensionRegistry())
            try:
                chat = await harness.root_async(AgentChoices(extensions=[]))
                answer = await chat.submit_async("Hello")
                self.assertEqual(answer.data.content, "Hello from the recorded transcript.")
                self.assertEqual(chat.answers, ["Hello from the recorded transcript."])
                self.assertEqual(chat.effective_model.model, "replay-model")
                self.assertEqual(chat.effective_model.source, "session")
                self.assertIsNone(chat.turn_models[0].model)
                admission, checkpoint = chat.admission(), chat.checkpoint()
                assert admission is not None and checkpoint is not None
                self.assertEqual(admission.state, "completed")
                self.assertEqual(checkpoint.content, answer.data.content)
                self.assertEqual(chat.entries(), [])
                session_id = chat.session_id
            finally:
                await harness.close_async()
                store.close()
            store = DurableStore.create(options)
            harness = await DurableHarness.open_async(store, ExtensionRegistry())
            try:
                chat = await harness.root_async()
                self.assertEqual(chat.session_id, session_id)
                choices, admission = chat.choices(), chat.admission()
                assert choices is not None and admission is not None
                self.assertEqual(choices.extensions, [])
                self.assertEqual(admission.state, "completed")
            finally:
                await harness.close_async()
                store.close()

    async def test_default_binding_starts_sdk_and_uses_keyword_configuration(self):
        # Patch the SDK boundary, not a replacement durable engine.
        from unittest.mock import AsyncMock, Mock
        session = Mock()
        session.session_id = "sdk-session"
        session.on.return_value = lambda: None
        session.get_events = AsyncMock(return_value=[])
        with patch("durable.CopilotClient") as client_type:
            client = client_type.return_value
            client.start = AsyncMock()
            client.stop = AsyncMock()
            client.create_session = AsyncMock(return_value=session)
            store = DurableStore.create_ephemeral()
            harness = await DurableHarness.open_async(store, ExtensionRegistry())
            try:
                chat = await harness.root_async(AgentChoices(extensions=[], permissions=[]))
                client.start.assert_awaited_once()
                kwargs = client.create_session.call_args.kwargs
                self.assertEqual(kwargs["tools"], [])
                self.assertTrue(kwargs["session_id"].startswith("s"))
                binding = chat.binding()
                assert binding is not None
                self.assertEqual(binding.session_id, kwargs["session_id"])
                self.assertEqual(client.create_session.call_args.args, ())
            finally:
                await harness.close_async()
                store.close()
            client.stop.assert_awaited_once()

    async def test_abort_cascade_commits_default_abort_and_preserves_pending_records(self):
        store = DurableStore.create_ephemeral()
        registry = ExtensionRegistry()
        harness = DurableHarness(None, store, registry)
        definition = DurableTask.define("work", lambda: {"phase": "run"},
            lambda _input, _checkpoint, runtime: runtime.commit_async(lambda: TaskCommit.completed(None)))
        registry.install(Extension("work", tasks=[definition]))
        try:
            async def create(tx):
                parent = tx.create_task(definition)
                child = tx.create_task(definition, owner_task_id=parent.id)
                return parent, child
            parent, child = await store.commit_async(create)
            self.assertEqual(parent.status, TaskStatus.PENDING)
            await harness.abort_task_async(parent.id)
            self.assertEqual(parent.status, TaskStatus.TERMINAL)
            self.assertEqual(child.status, TaskStatus.TERMINAL)
            self.assertEqual(child.outcome.status, "aborted")
            self.assertEqual(harness.task_graph(), [(parent, None), (child, parent.id)])
        finally:
            harness.close()
            store.close()

    @unittest.skipUnless(os.getenv("DURABLE_RUNTIME_PATH"), "Requires actual recorded runtime")
    async def test_actual_sdk_tool_override_wrapper_and_checkpoint(self):
        calls = []
        async def read(invocation):
            self.assertEqual(invocation.arguments, {"path": "notes.txt"})
            calls.append(invocation.session_id)
            return ToolResult(text_result_for_llm="recorded contents")
        def wrap(tool):
            async def invoke(invocation):
                calls.append("wrapped")
                return await tool.handler(invocation)
            from dataclasses import replace
            return replace(tool, handler=invoke)
        registry = ExtensionRegistry()
        registry.install(Extension("files", tools=[Tool(
            name="read_file", description="read", handler=read,
            parameters={"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]},
            overrides_built_in_tool=True,
        )], wraps=[ToolWrapper("read_file", wrap)]))
        options = DurableHarnessOptions(runtime_arguments=[
            "--fixture", str(REPLAY_FIXTURES / "tool-round-trip.json")])
        store = DurableStore.create_ephemeral()
        harness = await DurableHarness.open_async(store, registry, options)
        try:
            chat = await harness.root_async(AgentChoices(permissions=["custom-tool:read_file"]))
            answer = await chat.submit_async("Read notes.txt", timeout=5)
            self.assertTrue(answer.data.content)
            self.assertEqual(calls, ["wrapped", chat.session_id])
            self.assertTrue(any(
                version.value.get("eventId") == "tool:call-0001"
                for doc in store.documents if doc.kind == "durable.checkpoint"
                for version in doc.versions
            ))
        finally:
            await harness.close_async()
            store.close()

    @unittest.skipUnless(os.getenv("DURABLE_RUNTIME_PATH"), "Requires actual recorded runtime")
    async def test_facade_shares_one_harness_and_no_model_stays_unreported(self):
        store = DurableStore.create_ephemeral()
        options = DurableHarnessOptions(runtime_arguments=[
            "--fixture", str(REPLAY_FIXTURES / "no-model.json")])
        client = DurableClientBuilder().use_store(store).use_harness_options(options).build()
        try:
            chat = await client.root_async(AgentChoices(extensions=[]))
            second = await client.create_conversation_async(AgentChoices(extensions=[]))
            self.assertIs(client.find_conversation(chat.id), chat)
            self.assertNotEqual(chat.id, second.id)
            await chat.submit_async("Hello")
            self.assertIsNone(chat.effective_model.model)
            self.assertFalse(chat.effective_model.has_model)
            with self.assertRaises(RuntimeError):
                _ = chat.effective_model.required_model
            with self.assertRaises(TimeoutError):
                await chat.wait_for_effective_model_async(timeout=0.01)
        finally:
            await client.close_async()
            store.close()


if __name__ == "__main__":
    unittest.main()
