"""19 - The inbox (LIVE). Overlapping submissions retain independent answers."""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import Check, DurableHarness, DurableStore, ExtensionRegistry


async def main():
    store = DurableStore.create_ephemeral()
    harness = await DurableHarness.open_async(store, ExtensionRegistry())
    try:
        root = await harness.root_async()
        report = await root.wait_for_effective_model_async()
        print(f"effective model: {report.model or '(not reported)'}")
        first = asyncio.create_task(root.submit_async("First question: what is 2+2? Answer with just the number."))
        follow_up = asyncio.create_task(root.submit_async("A follow-up: what is 3+3? Answer with just the number."))
        first_answer, follow_up_answer = await asyncio.gather(first, follow_up)
        context = await root.context_async()
        messages = [e.data.content for e in context if e.type == "user.message"]
        Check.equal(2, len(messages), "both prompts are in the transcript")
        Check.that(any("2+2" in m for m in messages), "the first prompt is in the transcript")
        Check.that(any("3+3" in m for m in messages), "the queued prompt is in the transcript")
        Check.that(first_answer is not None, "the first prompt was answered")
        Check.that(follow_up_answer is not None, "the queued prompt was answered")
        Check.that(len(first_answer.data.content.strip()) > 0, "the first prompt was answered")
        Check.that(len(follow_up_answer.data.content.strip()) > 0, "the queued prompt was answered")
        Check.that(len(root.answers) == 2, "the conversation produced one answer per prompt")
        print(f"first: {first_answer.data.content}\nfollow-up: {follow_up_answer.data.content}")
    finally:
        await harness.close_async()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
