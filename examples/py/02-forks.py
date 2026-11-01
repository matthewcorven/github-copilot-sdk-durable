"""02 — Forks. Native SDK forking is required, not a replacement fresh session."""
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
    branch = await harness.fork_async(chat, first_entry)
    fork_notes_at_fork = branch.snapshot(notes)["text"]

    async def edit(tx):
        draft = tx.doc(notes, branch.id)
        draft.value["text"] = "changed only in the fork"
        draft.save(tx.append_entry(branch.id, "note", "edit"))

    await branch.commit_async(edit)
    fork_entries = branch.entries(10)
    Check.equal(2, len(fork_entries), "the fork holds the inherited entry and its own edit")
    Check.equal(first_entry, fork_entries[-1], "the inherited entry is the oldest one in the fork")
    Check.that(second_entry not in fork_entries, "the fork did not inherit entries written after the fork entry")
    Check.equal("after hello", fork_notes_at_fork, "the fork starts with the document value as of the fork entry")
    Check.equal("changed only in the fork", branch.snapshot(notes)["text"], "the fork holds its own edit")
    Check.equal("after goodbye", chat.snapshot(notes)["text"], "the parent is unchanged by the fork's edit")
    Check.equal(chat.id, branch.record.parent_conversation_id, "the fork records its parent")
    Check.equal(first_entry, branch.record.fork_entry_id, "the fork records the entry it was taken at")
    Check.that(branch.id != chat.id, "the fork is a different conversation")


if __name__ == "__main__":
    run_example(main)
