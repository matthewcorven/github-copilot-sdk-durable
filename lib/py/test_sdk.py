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
)
from copilot.tools import Tool, ToolResult

REPLAY_FIXTURES = Path(__file__).resolve().parents[2] / "tools" / "ReplayRuntime" / "fixtures"


class SdkTests(unittest.IsolatedAsyncioTestCase):
    async def test_host_hook_tolerance_is_opt_in_and_preserves_error_and_answer(self):
        from unittest.mock import Mock
        for tolerant, message, succeeds in [
            (False, "postToolUse hook: Operation not permitted", False),
            (True, "postToolUse hook: Operation not permitted", True),
            (True, "hook timed out", False),
            (True, "Operation not permitted", False),
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
                for event in [answer, {"type": "session.error", "data": {"message": message}}]:
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
                    self.assertIs(await chat.submit_async("hello", timeout=1), answer)
                else:
                    with self.assertRaisesRegex(RuntimeError, message):
                        await chat.submit_async("hello", timeout=1)
                self.assertIn("actual event", chat.answers)
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
