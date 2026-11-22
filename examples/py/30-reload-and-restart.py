"""An in-flight call keeps v1, the next uses v2, and a second process rebinds v3.

The C# reference takes approximately 124 seconds in this environment. Allow a
300-second outer invocation budget; the 120-second tool-start gate is deliberate,
not a default-runner false failure.
"""

import asyncio
import os
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import (
    AgentChoices, Check, DurableHarness, DurableStore, DurableStoreOptions, Extension, ExtensionRegistry,
)
from copilot import define_tool


async def run(directory: str, phase: str) -> None:
    started = asyncio.Event()
    gate = asyncio.Event()
    hold_open = True

    def load(version):
        @define_tool(name="version", description="Report the tool's code version")
        async def version_tool() -> str:
            started.set()
            if hold_open:
                await gate.wait()
            return version
        return version_tool

    registry = ExtensionRegistry()
    choices = AgentChoices(extensions=["versioned"], tools=["version"],
                           permissions=["custom-tool:version"])
    with DurableStore.create(DurableStoreOptions(path=str(Path(directory) / "session.jsonl"))) as store:
        harness = await DurableHarness.open_async(store, registry)
        try:
            if phase == "second":
                root = await harness.root_async()
                print(f"after restart, before install: [{', '.join(tool.name for tool in root.agent().tools)}]")
                registry.install(Extension("versioned", tools=[load("v3")]))
                print(f"after install: [{', '.join(tool.name for tool in root.agent().tools)}]")
                await root.configure_async(choices)
                print(f"after install, configured: [{', '.join(tool.name for tool in root.agent().tools)}]")
                return
            registry.install(Extension("versioned", tools=[load("v1")]))
            root = await harness.root_async(choices)
            effective = await root.wait_for_effective_model_async()
            print(f"effective model: {effective.model or '(not reported)'}")
            submission = asyncio.create_task(root.submit_async("Call the version tool and report what it returns."))
            try:
                await asyncio.wait_for(started.wait(), 120)
                registry.install(Extension("versioned", tools=[load("v2")]))
                await root.configure_async(choices)
            finally:
                gate.set()
            await submission
            during_reload = root.answers[-1] if root.answers else ""
            print(f"call running during the reload: {during_reload}")
            hold_open = False
            await root.submit_async("Call the version tool again and report what it returns.")
            after_reload = root.answers[-1] if root.answers else ""
            print(f"next call: {after_reload}")
        finally:
            await harness.close_async()

    # The first writer is closed before the new process opens the same file.
    process = await asyncio.to_thread(
        subprocess.run, [sys.executable, str(Path(__file__).resolve()), "second"],
        env={**os.environ, "DURABLE_RELOAD_DIR": directory},
        capture_output=True, text=True, timeout=180,
    )
    print(process.stdout.strip())
    Check.equal(0, process.returncode, f"the second process succeeded: {process.stderr}")
    Check.contains(during_reload, "v1", "the call running during the reload finished on the old code")
    Check.contains(after_reload, "v2", "the next call used the reloaded code")
    Check.contains(process.stdout, "after restart, before install: []",
                   "the restarted process had not installed the extension yet")
    Check.contains(process.stdout, "after install: [version]",
                   "the restarted process bound the stored name to the new code")


async def main() -> None:
    phase = sys.argv[1] if len(sys.argv) > 1 else "first"
    if phase not in ("first", "second"):
        raise ValueError("phase must be first or second")
    directory = os.environ.get("DURABLE_RELOAD_DIR")
    if directory:
        await run(directory, phase)
    elif phase == "second":
        raise ValueError("the second process needs DURABLE_RELOAD_DIR")
    else:
        with TemporaryDirectory(prefix="durable-reload-") as directory:
            await run(directory, phase)


if __name__ == "__main__":
    asyncio.run(main())
