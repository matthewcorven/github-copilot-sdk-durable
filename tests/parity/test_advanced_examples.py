"""Actual standalone source, real facade/SDK tools, mocked session transport; not LIVE evidence."""
import asyncio
import contextlib
import io
import os
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "lib/py"))
from durable import DurableHarness


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


class ToolOverrideTests(unittest.IsolatedAsyncioTestCase):
    async def run_example(self, mutation=None):
        path = ROOT / "examples/py/29-tool-override.py"
        source = path.read_text()
        if mutation:
            self.assertIn(mutation[0], source, "mutation targets the actual production source")
            source = source.replace(*mutation, 1)
        namespace = {"__name__": "tool_override_regression", "__file__": str(path)}
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
                    command = ("echo plain: $VIRTUAL_ENV" if "echo plain:" in prompt
                               else "echo venv: $VIRTUAL_ENV")
                    result = await tool.handler(SimpleNamespace(
                        arguments={"command": command}, session_id=config["session_id"],
                        tool_name="run_shell", tool_call_id=f"call-{len(calls)}"))
                    self.assertEqual("success", result.result_type, result.error)
                    output = result.text_result_for_llm
                    calls.append(output)
                    for kind, data in [("assistant.message", {"content": output}), ("session.idle", {})]:
                        event = {"id": f"event-{len(session.events)}", "type": kind, "data": data}
                        session.events.append(event)
                        for listener in list(session.listeners):
                            listener(event)

                session.send = send
                return session

            async def stop(client):
                for session in sessions:
                    await session.disconnect()

        async def open_harness(store, registry):
            return DurableHarness(Client(), store, registry)

        with patch.dict(os.environ, {"VIRTUAL_ENV": "/ambient/recommended-setup/.venv"}), \
                patch.object(DurableHarness, "open_async", side_effect=open_harness), \
                contextlib.redirect_stdout(io.StringIO()):
            await asyncio.wait_for(namespace["main"](), 5)
        self.assertEqual(2, len(calls), "both actual SDK-decorated wrapped handlers executed")
        self.assertEqual("plain: /ambient/recommended-setup/.venv", calls[0])
        self.assertRegex(calls[1], r"^venv: .*/durable-venv-[^/]+/\.venv$")
        self.assertEqual(2, len(sessions))
        for session in sessions:
            self.assertTrue(session.disconnected, "example shutdown disconnects each SDK session")
            self.assertEqual(1, session.disconnects)
            self.assertEqual(set(), session.listeners, "example shutdown releases subscriptions")
            self.assertEqual({}, session.config, "example shutdown releases tools and permission handlers")
            self.assertEqual(3, len(session.events), "disconnect preserves the actual example history")

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

    async def test_actual_tools_allow_ambient_venv_but_select_project_override(self):
        await self.run_example()

    async def test_actual_override_and_plain_selection_mutants_fail_at_assertions(self):
        for before, after, diagnostic in [
            ('return await run(f". .venv/bin/activate && {params.command}")',
             "return await run(params.command)", "the venv shell replaced"),
            ('extensions=["shell", "timing"], tools=',
             'extensions=["shell", "timing", "venv"], tools=', "the plain conversation kept"),
        ]:
            with self.subTest(mutation=before), self.assertRaisesRegex(AssertionError, diagnostic):
                await self.run_example((before, after))


if __name__ == "__main__":
    unittest.main()
