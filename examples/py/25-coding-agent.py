"""A coding tool follows the conversation's reconfigured working directory."""

import asyncio
import subprocess
import sys
import time
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DurableHarness, DurableStore, Extension,
    ExtensionRegistry, PromptSection,
)
from copilot import define_tool
from pydantic import BaseModel


class ShellParams(BaseModel):
    command: str


async def main() -> None:
    with TemporaryDirectory(prefix="durable-agent-") as workspace:
        app = str(Path(workspace) / "app")
        Path(app).mkdir()
        current_directory = workspace
        user_settings = {"parallel_tools": True, "max_retries": 3}

        @define_tool(name="run_shell", description="Run a shell command")
        async def run_shell(params: ShellParams) -> str:
            command = params.command
            started = time.monotonic()
            result = await asyncio.to_thread(
                subprocess.run, ["/bin/sh", "-c", command],
                cwd=current_directory, capture_output=True, text=True, check=True,
            )
            return f"{result.stdout.strip()} (exit {result.returncode}, {int((time.monotonic() - started) * 1000)} ms)"

        async def preamble(input):
            return "You are a coding agent. Use the tools to inspect the project."

        async def cwd(input):
            return input.working_directory

        registry = ExtensionRegistry()
        registry.install(Extension(
            "coding",
            tools=[run_shell],
            sections=[PromptSection("preamble", preamble, tag=False), PromptSection("cwd", cwd)],
        ))
        with DurableStore.create_ephemeral() as store:
            harness = await DurableHarness.open_async(store, registry)
            try:
                root = await harness.root_async(AgentChoices(
                    extensions=["coding"], working_directory=workspace,
                    permissions=["custom-tool:run_shell"],
                ))
                effective = await root.wait_for_effective_model_async()
                print(f"effective model: {effective.model or '(not reported)'}")
                current_directory = root.agent().working_directory or workspace
                first = await root.submit_async(
                    "Use the run_shell tool to run 'pwd', then tell me the directory."
                )
                first_directory = root.agent().working_directory
                print(f"answer: {first.data.content if first else ''}")
                await root.configure_async(AgentChoices(
                    extensions=["coding"], working_directory=app,
                    permissions=["custom-tool:run_shell"],
                ))
                current_directory = root.agent().working_directory or workspace
                user_settings["parallel_tools"] = False
                second = await root.submit_async(
                    "Use the run_shell tool to run 'pwd' again, then tell me the directory."
                )
                print(f"answer: {second.data.content if second else ''}")
                Check.equal(workspace, first_directory,
                            "the conversation's working directory is the one that was configured")
                Check.contains(first.data.content if first else "", workspace,
                               "the tool ran in the conversation's directory")
                Check.equal(app, root.agent().working_directory,
                            "the conversation's working directory changed")
                Check.contains(second.data.content if second else "", app,
                               "the tool followed the conversation's new directory")
                Check.contains(second.data.content if second else "", app,
                               "the tool ran in the new directory")
                print(f"live settings: maxRetries={user_settings['max_retries']} "
                      f"toolExecution={'parallel' if user_settings['parallel_tools'] else 'sequential'}")
            finally:
                await harness.close_async()


if __name__ == "__main__":
    asyncio.run(main())
