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

        class Client:
            async def create_session(client, **config):
                listeners = []
                events = [{"type": "session.start", "data": {"selectedModel": "mock-reported-model"}}]

                def subscribe(listener):
                    listeners.append(listener)
                    return lambda: listeners.remove(listener)

                async def send(prompt):
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
                        events.append(event)
                        for listener in list(listeners):
                            listener(event)

                async def get_events():
                    return events

                async def set_tools(tools):
                    config["tools"] = tools

                return SimpleNamespace(session_id=config["session_id"], on=subscribe,
                                       send=send, get_events=get_events, set_tools=set_tools)

            async def stop(client):
                pass

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

    async def test_actual_standalone_and_real_shell_with_physical_and_alias_tmpdir(self):
        for aliased in (False, True):
            with self.subTest(aliased=aliased):
                await self.run_example(aliased)

    async def test_uncanonicalized_actual_workspace_mutant_fails_original_assertion(self):
        with self.assertRaisesRegex(AssertionError, "the tool ran in the conversation's directory"):
            await self.run_example(True, ("        workspace = str(Path(workspace).resolve())\n", ""))


if __name__ == "__main__":
    unittest.main()
