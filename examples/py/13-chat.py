"""13 - Chat (LIVE). Requires the native SDK runtime and model access."""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, PromptSection


async def main():
    store = DurableStore.create_ephemeral()
    registry = ExtensionRegistry()

    async def preamble(input):
        return "You answer in one word."

    registry.install(Extension("terse", sections=[PromptSection("preamble", preamble, tag=False)]))
    harness = await DurableHarness.open_async(store, registry)
    try:
        root = await harness.root_async(AgentChoices(extensions=["terse"]))
        report = await root.wait_for_effective_model_async()
        print(f"effective model: {report.model or '(not reported)'}")
        answer = await root.submit_async("Capital of France? Answer in one word.")
        Check.equal("Paris", answer.data.content.strip() if answer else None, "the model answered the question")
        Check.equal(0, len(root.entries(10)), "a plain chat turn has no app-written entries")
        events = await root.context_async()
        Check.that(any(e.type.value == "user.message" for e in events), "the transcript holds the user message")
        Check.that(any(e.type.value == "assistant.message" for e in events), "the transcript holds the assistant message")
        Check.that(any(e.type.value == "system.message" for e in events), "the transcript holds the system message")
        print(f"answer: {answer.data.content}")
        for event in events:
            if event.type.value in ("user.message", "assistant.message", "system.message"):
                print(f"  {event.type.value}: {event.data.content}")
    finally:
        await harness.close_async()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
