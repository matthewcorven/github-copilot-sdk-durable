"""Infinite-session context management; compaction is observed, not promised."""

import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "lib" / "py"))

from durable import AgentChoices, Check, DurableHarness, DurableStore, ExtensionRegistry


async def main() -> None:
    with DurableStore.create_ephemeral() as store:
        harness = await DurableHarness.open_async(store, ExtensionRegistry())
        try:
            root = await harness.root_async(AgentChoices(infinite_sessions={
                "enabled": True,
                "background_compaction_threshold": 0.80,
                "buffer_exhaustion_threshold": 0.95,
            }))
            effective = await root.wait_for_effective_model_async()
            print(f"effective model: {effective.model or '(not reported)'}")
            compactions = []

            def observe(event):
                if event.type.value == "session.compaction_start":
                    compactions.append("start")
                    print("compaction started")
                elif event.type.value == "session.compaction_complete":
                    compactions.append("complete")
                    print(f"compaction complete: {event.data}")

            unsubscribe = root.session.on(observe)
            try:
                answers = []
                for question in [
                    "Where should we stay in Lisbon?", "What should we eat?",
                    "Which day trips are worth it?", "Any museums?", "Nightlife?",
                ]:
                    answer = await root.submit_async(question)
                    answers.append(answer)
                    print(f"asked: {question}")
                    print(f"  answered: {len(answer.data.content) if answer else 0} chars")
                stored = root.entries(100)
                context = await root.context_async()
                print(f"stored entries: {len(stored)}")
                print(f"context events: {len(context)}")
                print(f"compactions observed: {len(compactions)}")
                for event in context:
                    if event.type.value == "session.usage_checkpoint":
                        print(f"usage checkpoint: {event.data}")
                Check.equal(5, len(answers), "every question was answered")
                Check.that(
                    all(answer is not None and answer.data.content.strip() for answer in answers),
                    "no answer was empty",
                )
                Check.equal(0, len(stored), "plain chat checkpoints are not public app entries")
                Check.that(len(context) > 0, "the model context is not empty")
            finally:
                unsubscribe()
        finally:
            await harness.close_async()


if __name__ == "__main__":
    asyncio.run(main())
