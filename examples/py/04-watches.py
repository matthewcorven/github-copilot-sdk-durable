"""04 — Watches. Consume committed versions from the native change feed."""
import asyncio
from example import d, run_example
from check import Check


async def main(harness, store, registry):
    notes = d.DocumentDefinition("example.notes", d.DocumentHistory.REWINDABLE, d.ForkPolicy.AS_OF, lambda: {"text": ""})
    chat = await harness.create_conversation_async()
    print(f"effective model: {(await chat.wait_for_effective_model_async()).model or '(not reported)'}")

    async def first(tx):
        draft = tx.doc(notes, chat.id)
        draft.value["text"] = "first"
        draft.save(tx.append_entry(chat.id, "note", "first"))

    await chat.commit_async(first)
    print(f"watch baseline: {chat.snapshot(notes)['text']}")
    feed = d.DurableWatch(store, chat.id).frames()
    baseline = await anext(feed)
    Check.equal("baseline", baseline.kind, "the watch starts from a stable acquisition revision")
    Check.equal("first", chat.snapshot(notes)["text"], "the watch acquisition baseline is stable")
    seen = []

    async def second(tx):
        draft = tx.doc(notes, chat.id)
        draft.value["text"] = "observed asynchronously"
        draft.save(tx.append_entry(chat.id, "note", "second"))

    try:
        await chat.commit_async(second)
        frame = await asyncio.wait_for(anext(feed), timeout=5)
        # Read each exact anchored version, not a later mutable snapshot.
        for kind, owner_id, entry_id in frame.documents:
            if kind == notes.kind and owner_id == chat.id:
                await asyncio.sleep(0.05)
                seen.append(store.document(kind, owner_id).as_of(entry_id)["text"])
                print(f"watch update: {seen[-1]}")
    finally:
        await feed.aclose()
    print(f"watch stopped at: {chat.snapshot(notes)['text']}")
    Check.equal("observed asynchronously", chat.snapshot(notes)["text"], "the committed value is what the watch saw")
    Check.that(len(seen) >= 1, "the watch delivered at least one update")
    Check.equal("observed asynchronously", seen[-1], "the last delivered frame is the committed value")
    Check.that(all(frame == "observed asynchronously" for frame in seen), "the watch only ever delivered committed values")


if __name__ == "__main__":
    run_example(main)
