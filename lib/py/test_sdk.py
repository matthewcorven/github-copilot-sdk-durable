import asyncio
import os
import tempfile
import unittest
from unittest.mock import patch

from durable import (
    AgentChoices, DurableHarness, DurableStore, DurableStoreOptions, ExtensionRegistry,
    DurableSandboxPolicy, DurableSandboxFilesystem, DurableSandboxCredentials,
    DurableSandboxNetwork, DurableSandboxAuth, PermissionPolicy,
    DurableTask, TaskCommit, TaskStatus, Extension, ToolWrapper, DurableHarnessOptions,
    DurableClientBuilder,
)
from copilot.tools import Tool, ToolResult


class SdkTests(unittest.IsolatedAsyncioTestCase):
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
                self.assertEqual(chat.admission().state, "completed")
                self.assertEqual(chat.checkpoint().content, answer.data.content)
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
                self.assertEqual(chat.choices().extensions, [])
                self.assertEqual(chat.admission().state, "completed")
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
                self.assertEqual(chat.binding().session_id, kwargs["session_id"])
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
            "--fixture", os.path.abspath("../../tools/ReplayRuntime/fixtures/tool-round-trip.json")])
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
            "--fixture", os.path.abspath("../../tools/ReplayRuntime/fixtures/no-model.json")])
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
