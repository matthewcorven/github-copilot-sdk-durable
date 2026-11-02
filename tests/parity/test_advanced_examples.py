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

        class Client:
            async def create_session(client, **config):
                listeners = []
                events = [{"type": "session.start", "data": {"selectedModel": "mock-reported-model"}}]

                def subscribe(listener):
                    listeners.append(listener)
                    return lambda: listeners.remove(listener)

                async def send(prompt):
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
                        event = {"id": f"event-{len(events)}", "type": kind, "data": data}
                        events.append(event)
                        for listener in list(listeners):
                            listener(event)

                async def get_events():
                    return events

                return SimpleNamespace(session_id=config["session_id"], on=subscribe,
                                       send=send, get_events=get_events)

            async def stop(client):
                pass

        async def open_harness(store, registry):
            return DurableHarness(Client(), store, registry)

        with patch.dict(os.environ, {"VIRTUAL_ENV": "/ambient/recommended-setup/.venv"}), \
                patch.object(DurableHarness, "open_async", side_effect=open_harness), \
                contextlib.redirect_stdout(io.StringIO()):
            await asyncio.wait_for(namespace["main"](), 5)
        self.assertEqual(2, len(calls), "both actual SDK-decorated wrapped handlers executed")
        self.assertEqual("plain: /ambient/recommended-setup/.venv", calls[0])
        self.assertRegex(calls[1], r"^venv: .*/durable-venv-[^/]+/\.venv$")

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
