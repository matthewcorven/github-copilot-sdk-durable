"""01 — Documents. Immutable version history and failed-commit isolation."""
from example import d, run_example
from check import Check


async def main(harness, store, registry):
    notes = d.DocumentDefinition("example.notes", d.DocumentHistory.REWINDABLE, d.ForkPolicy.AS_OF, lambda: {"text": ""})
    chat = await harness.create_conversation_async()
    print(f"effective model: {(await chat.wait_for_effective_model_async()).model or '(not reported)'}")

    async def hello(tx):
        entry = tx.append_entry(chat.id, "note", "hello")
        draft = tx.doc(notes, chat.id)
        draft.value["text"] = "after hello"
        draft.save(entry)
        return entry

    async def goodbye(tx):
        entry = tx.append_entry(chat.id, "note", "goodbye")
        draft = tx.doc(notes, chat.id)
        draft.value["text"] = "after goodbye"
        draft.save(entry)
        return entry

    first_entry = await chat.commit_async(hello)
    second_entry = await chat.commit_async(goodbye)
    document = Check.not_none(store.document(notes.kind, chat.id), "the notes document exists")
    Check.equal("after goodbye", chat.snapshot(notes)["text"], "the latest value is the last one written")
    Check.equal("after hello", chat.snapshot_as_of(notes, first_entry)["text"], "the value as of the first entry is readable")
    Check.equal("after goodbye", chat.snapshot_as_of(notes, second_entry)["text"], "the value as of the second entry is readable")
    Check.equal(2, len(document.versions), "both committed versions are retained")
    Check.equal(first_entry, document.versions[0].entry_id, "the first version belongs to the first entry")
    Check.equal(second_entry, document.versions[1].entry_id, "the second version belongs to the second entry")
    untouched = d.DocumentDefinition("example.untouched", d.DocumentHistory.LATEST, d.ForkPolicy.AS_OF, lambda: {"text": ""})
    Check.equal(None, chat.snapshot(untouched), "a document that was never written has no value")
    before = chat.snapshot(notes)["text"]

    async def doomed(tx):
        draft = tx.doc(notes, chat.id)
        draft.value["text"] = "should not survive"
        draft.save(tx.append_entry(chat.id, "note", "doomed"))
        raise RuntimeError("boom")

    try:
        await chat.commit_async(doomed)
    except RuntimeError as error:
        if str(error) != "boom":
            raise
    Check.equal(before, chat.snapshot(notes)["text"], "a failed transaction does not change the document")
    Check.equal(2, len(store.document(notes.kind, chat.id).versions), "a failed transaction adds no version")


if __name__ == "__main__":
    run_example(main)
