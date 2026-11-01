"""20 - Late join (LIVE). Requires deltas after joining an in-progress model run."""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))
from durable import AgentChoices, Check, DurableHarness, DurableStore, ExtensionRegistry


async def main():
    store = DurableStore.create_ephemeral()
    harness = await DurableHarness.open_async(store, ExtensionRegistry())
    unsubscribe = None
    run = None
    try:
        root = await harness.root_async(AgentChoices(streaming=True))
        report = await root.wait_for_effective_model_async()
        print(f"effective model: {report.model or '(not reported)'}")
        run = asyncio.create_task(root.submit_async("Count slowly from one to five, one number per sentence, then say DONE."))
        await asyncio.sleep(1.5)
        snapshot = await root.context_async()
        print(f"snapshot events: {len(snapshot)}")
        deltas = []

        def observe(event):
            if event.type.value == "assistant.message_delta" and event.data.delta_content:
                deltas.append(event.data.delta_content)
                print(f"event text delta: {event.data.delta_content}")
            else:
                print(f"event: {event.type.value}")

        unsubscribe = root.session.on(observe)
        answer = await run
        Check.that(len(snapshot) >= 1, "the late joiner received the current state")
        Check.that(any(e.type.value == "user.message" for e in snapshot), "the snapshot holds the user message")
        Check.that(len(deltas) >= 1, "the late joiner received live deltas")
        Check.that(answer is not None, "the run finished")
        Check.that(len(answer.data.content.strip()) > 0, "the answer is not empty")
        print(f"answer: {answer.data.content}; deltas received after joining: {len(deltas)}")
    finally:
        if unsubscribe is not None:
            unsubscribe()
        if run is not None and not run.done():
            run.cancel()
            await asyncio.gather(run, return_exceptions=True)
        await harness.close_async()
        store.close()


if __name__ == "__main__":
    asyncio.run(main())
