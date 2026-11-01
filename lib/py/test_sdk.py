import asyncio
import os
import tempfile
import unittest
from unittest.mock import patch

from durable import (
    AgentChoices, DurableHarness, DurableStore, DurableStoreOptions, ExtensionRegistry,
    DurableSandboxPolicy, DurableSandboxFilesystem, DurableSandboxCredentials,
    DurableSandboxNetwork, DurableSandboxAuth, PermissionPolicy,
)


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


if __name__ == "__main__":
    unittest.main()
