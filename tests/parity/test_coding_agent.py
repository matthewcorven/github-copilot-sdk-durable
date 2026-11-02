"""Actual standalone source, SDK tool, real shell and alias TMPDIR; not LIVE evidence."""
import asyncio
import contextlib
from datetime import datetime, timezone
import io
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "lib/py"))
from durable import DurableHarness
from copilot.generated.session_events import (
    AssistantMessageData, SessionEvent, SessionIdleData,
)


class _Session:
    """Only the public SDK session resources used by this example."""

    def __init__(self, config):
        self.session_id = config["session_id"]
        self.config = config
        self.listeners = set()
        self.events = [{"type": "session.start", "data": {"selectedModel": "mock-reported-model"}}]
        self.disconnected = False
        self.disconnects = 0
        self._disconnect_lock = asyncio.Lock()

    def on(self, listener):
        if self.disconnected:
            raise RuntimeError("Session is disconnected")
        self.listeners.add(listener)
        return lambda: self.listeners.discard(listener)

    async def get_events(self):
        if self.disconnected:
            raise RuntimeError("Session is disconnected")
        return self.events

    async def disconnect(self):
        async with self._disconnect_lock:
            if self.disconnected:
                return
            self.listeners.clear()
            self.config.clear()
            self.disconnected = True
            self.disconnects += 1


class CodingAgentTests(unittest.IsolatedAsyncioTestCase):
    async def run_example(self, aliased, mutation=None):
        path = ROOT / "examples/py/25-coding-agent.py"
        source = path.read_text()
        if mutation:
            self.assertIn(mutation[0], source)
            source = source.replace(*mutation, 1)
        namespace = {"__name__": "coding_agent_regression", "__file__": str(path)}
        exec(compile(source, str(path), "exec"), namespace)
        calls = []
        sessions = []

        class Client:
            async def create_session(client, **config):
                session = _Session(config)
                sessions.append(session)

                async def send(prompt):
                    if session.disconnected:
                        raise RuntimeError("Session is disconnected")
                    tool = next(tool for tool in config["tools"] if tool.name == "run_shell")
                    result = await tool.handler(SimpleNamespace(
                        arguments={"command": "pwd"}, session_id=config["session_id"],
                        tool_name="run_shell", tool_call_id=f"call-{len(calls)}"))
                    self.assertEqual("success", result.result_type, result.error)
                    output = result.text_result_for_llm
                    calls.append((config["working_directory"], output))
                    for kind, data in [
                        ("assistant.message", AssistantMessageData(content=output, message_id=str(uuid4()))),
                        ("session.idle", SessionIdleData()),
                    ]:
                        event = SessionEvent(type=kind, data=data, id=uuid4(), timestamp=datetime.now(timezone.utc))
                        session.events.append(event)
                        for listener in list(session.listeners):
                            listener(event)

                async def set_tools(tools):
                    if session.disconnected:
                        raise RuntimeError("Session is disconnected")
                    config["tools"] = tools

                session.send = send
                session.set_tools = set_tools
                return session

            async def stop(client):
                for session in sessions:
                    await session.disconnect()

        async def open_harness(store, registry):
            return DurableHarness(Client(), store, registry)

        with tempfile.TemporaryDirectory(prefix="durable-agent-regression-") as directory:
            parent = Path(directory).resolve()
            physical = parent / "physical"
            physical.mkdir()
            alias = parent / "alias"
            alias.symlink_to(physical, target_is_directory=True)
            with patch.dict(os.environ, {"TMPDIR": str(alias if aliased else physical)}), \
                    patch.object(tempfile, "tempdir", None), \
                    patch.object(DurableHarness, "open_async", side_effect=open_harness), \
                    contextlib.redirect_stdout(io.StringIO()):
                await asyncio.wait_for(namespace["main"](), 5)
            self.assertEqual(2, len(calls), "both actual SDK-decorated shell invocations execute")
            self.assertTrue(calls[0][0].startswith(str(physical / "durable-agent-")),
                            "SDK cwd is physically canonical")
            self.assertEqual(calls[0][0], calls[0][1].split(" (exit")[0])
            self.assertEqual(str(Path(calls[0][0]) / "app"), calls[1][1].split(" (exit")[0])
            self.assertEqual(1, len(sessions))
            session = sessions[0]
            self.assertTrue(session.disconnected, "example shutdown disconnects the SDK session")
            self.assertEqual(1, session.disconnects)
            self.assertEqual(set(), session.listeners, "example shutdown releases subscriptions")
            self.assertEqual({}, session.config, "example shutdown releases tools and permission handlers")
            self.assertEqual(5, len(session.events), "disconnect preserves the actual example history")

    async def test_session_disconnect_releases_resources_but_preserves_history(self):
        config = {"session_id": "fixture", "tools": [object()],
                  "on_permission_request": lambda request: None}
        session = _Session(config)
        listener = lambda event: None
        unsubscribe = session.on(listener)
        history = list(await session.get_events())
        self.assertEqual({listener}, session.listeners)
        self.assertTrue(session.config["tools"])
        self.assertIsNotNone(session.config["on_permission_request"])
        await asyncio.gather(session.disconnect(), session.disconnect())
        self.assertTrue(session.disconnected)
        self.assertEqual(1, session.disconnects, "disconnect is idempotent")
        self.assertEqual(set(), session.listeners, "disconnect releases existing subscriptions")
        self.assertEqual({}, config, "disconnect releases tool and permission handlers")
        self.assertEqual(history, session.events, "disconnect is not history deletion")
        unsubscribe()
        unsubscribe()
        with self.assertRaisesRegex(RuntimeError, "Session is disconnected"):
            session.on(listener)
        with self.assertRaisesRegex(RuntimeError, "Session is disconnected"):
            await session.get_events()

    async def test_actual_standalone_and_real_shell_with_physical_and_alias_tmpdir(self):
        for aliased in (False, True):
            with self.subTest(aliased=aliased):
                await self.run_example(aliased)

    async def test_uncanonicalized_actual_workspace_mutant_fails_original_assertion(self):
        with self.assertRaisesRegex(AssertionError, "the tool ran in the conversation's directory"):
            await self.run_example(True, ("        workspace = str(Path(workspace).resolve())\n", ""))


if __name__ == "__main__":
    unittest.main()
