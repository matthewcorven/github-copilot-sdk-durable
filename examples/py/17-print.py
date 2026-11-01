"""17 - Print mode (LIVE). Awaits this submission, not global idle."""
import asyncio
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import AgentChoices, Check, DurableHarness, DurableHarnessOptions, DurableStore, Extension, ExtensionRegistry, PromptSection


async def main():
    prompt = sys.argv[1] if len(sys.argv) > 1 else "What is in this directory?"
    store = DurableStore.create_ephemeral()
    registry = ExtensionRegistry()

    async def preamble(input):
        return "You are a concise coding assistant."

    registry.install(Extension("coding", sections=[PromptSection("preamble", preamble, tag=False)]))
    fixture = os.environ.get("DURABLE_EXAMPLE_FIXTURE")
    harness = await DurableHarness.open_async(store, registry, DurableHarnessOptions(
        runtime_arguments=["--fixture", fixture] if fixture else []))
    try:
        root = await harness.root_async(AgentChoices(extensions=["coding"], permissions=["read"]))
        report = await root.wait_for_effective_model_async()
        print(f"effective model: {report.model or '(not reported)'}", file=sys.stderr)
        answer = await root.submit_async(prompt)
        Check.that(answer is not None, "the model answered")
        Check.that(len(answer.data.content.strip()) > 0, "the answer is not empty")
        Check.that(len(root.answers) == 1, "the turn produced exactly one answer")
        print(answer.data.content)
    finally:
        await harness.close_async()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
