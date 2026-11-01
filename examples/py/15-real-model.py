"""15 - A real model (LIVE). Verifies streaming deltas against the final message."""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, PromptSection


async def main():
    store = DurableStore.create_ephemeral()
    registry = ExtensionRegistry()

    async def preamble(input):
        return "You are a concise assistant."

    registry.install(Extension("concise", sections=[PromptSection("preamble", preamble, tag=False)]))
    harness = await DurableHarness.open_async(store, registry)
    unsubscribe = None
    try:
        root = await harness.root_async(AgentChoices(extensions=["concise"], streaming=True))
        report = await root.wait_for_effective_model_async()
        print(f"effective model: {report.model or '(not reported)'}")
        printed = 0

        def observe(event):
            nonlocal printed
            if event.type.value == "assistant.message_delta" and event.data.delta_content:
                text = event.data.delta_content
                print(text, end="", flush=True)
                printed += len(text.encode("utf-16-le")) // 2

        unsubscribe = root.session.on(observe)
        print("answer: ", end="", flush=True)
        answer = await root.submit_async("In one sentence, what makes a durable agent different from a stateless one?")
        print()
        Check.that(answer is not None, "the model answered")
        Check.that(printed > 0, "the answer streamed as deltas")
        Check.equal(len(answer.data.content.encode("utf-16-le")) // 2, printed,
                    "the streamed deltas add up to the final message")
        Check.that(len(answer.data.content) > 0, "the final message is not empty")
        print(f"streamed {printed} chars; final message: {answer.data.content}")
    finally:
        if unsubscribe is not None:
            unsubscribe()
        await harness.close_async()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
