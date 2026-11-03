"""The winning shell implementation is wrapped without capturing the base tool."""

import asyncio
import subprocess
import sys
import time
from dataclasses import replace
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, ToolWrapper,
)
from copilot import define_tool
from pydantic import BaseModel


class ShellParams(BaseModel):
    command: str


async def main() -> None:
    with TemporaryDirectory(prefix="durable-venv-") as project:
        activation = Path(project, ".venv", "bin", "activate")
        activation.parent.mkdir(parents=True)
        activation.write_text(f'export VIRTUAL_ENV="{project}/.venv"\n')

        async def run(command):
            result = await asyncio.to_thread(subprocess.run, ["/bin/sh", "-c", command],
                                            cwd=project, capture_output=True, text=True, check=True)
            return result.stdout.strip()

        @define_tool(name="run_shell", description="Run a shell command")
        async def plain_shell(params: ShellParams) -> str:
            return await run(params.command)

        @define_tool(name="run_shell", description="Run a shell command")
        async def venv_shell(params: ShellParams) -> str:
            # POSIX dot works with /bin/sh on both Linux and macOS.
            return await run(f". .venv/bin/activate && {params.command}")

        timings = []
        def timed(inner):
            if inner.handler is None:
                raise ValueError("the winning run_shell tool has no handler")
            async def invoke(invocation):
                started = time.monotonic()
                try:
                    return await inner.handler(invocation)
                finally:
                    timings.append(int((time.monotonic() - started) * 1000))
            return replace(inner, description="Run a shell command (timed)", handler=invoke)

        registry = ExtensionRegistry()
        registry.install(Extension("shell", tools=[plain_shell]))
        registry.install(Extension("timing", wraps=[ToolWrapper("run_shell", timed)]))
        registry.install(Extension("venv", tools=[venv_shell]))
        with DurableStore.create_ephemeral() as store:
            harness = await DurableHarness.open_async(store, registry)
            try:
                plain = await harness.root_async(AgentChoices(
                    extensions=["shell", "timing"], tools=["run_shell"],
                    permissions=["custom-tool:run_shell"],
                ))
                effective = await plain.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                python = await harness.create_conversation_async(AgentChoices(
                    extensions=["shell", "timing", "venv"], tools=["run_shell"],
                    permissions=["custom-tool:run_shell"],
                ))
                print(f"plain tools: {[tool.name for tool in plain.agent().tools]}")
                print(f"python tools: {[tool.name for tool in python.agent().tools]}")
                await plain.submit_async("Use the run_shell tool to run 'echo plain: $VIRTUAL_ENV', then tell me the output.")
                await python.submit_async("Use the run_shell tool to run 'echo venv: $VIRTUAL_ENV', then tell me the output.")
                plain_answer = plain.answers[-1] if plain.answers else ""
                venv_answer = python.answers[-1] if python.answers else ""
                print(f"plain answer: {plain_answer}")
                print(f"python answer: {venv_answer}")
                print(f"timed bash calls: {len(timings)}")
                Check.contains(venv_answer, str(Path(project, ".venv")), "the venv shell replaced the plain one")
                Check.that(str(Path(project, ".venv")) not in plain_answer, "the plain conversation kept the plain shell")
                Check.equal(2, len(timings), "the wrapper timed both shell calls")
            finally:
                await harness.close_async()


if __name__ == "__main__":
    asyncio.run(main())
