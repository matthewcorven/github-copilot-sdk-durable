"""00 — Conversations. Mirrors every assertion in 00-conversation.cs."""
from example import d, run_example
from check import Check


async def main(harness, store, registry):
    async def create(tx):
        return tx.create_conversation()

    standalone = await store.commit_async(create)
    print(f"standalone conversation: {standalone.id}")
    print(f"  parent: {standalone.parent_conversation_id or '(none)'}")
    print(f"  owner: {standalone.owner_task_id or '(none)'}")
    conversation = await harness.create_conversation_async()
    print(f"copilot session: {conversation.session_id}")
    print(f"conversation count: {len(store.conversations)}")
    print(f"effective model: {(await conversation.wait_for_effective_model_async()).model or '(not reported)'}")
    Check.that(len(standalone.id) > 0, "a conversation has an id")
    Check.equal(None, standalone.parent_conversation_id, "a standalone conversation has no parent")
    Check.equal(None, standalone.owner_task_id, "a standalone conversation has no owner task")
    Check.that(any(c.id == standalone.id for c in store.conversations), "the conversation is in the store")
    Check.that(any(c.id == conversation.id for c in store.conversations), "the live conversation is in the store")
    Check.equal(2, len(store.conversations), "both conversations were created")
    Check.not_none(conversation.session_id, "the live conversation has a real SDK session")
    before = len(store.conversations)

    async def doomed(tx):
        tx.create_conversation()
        tx.create_conversation()
        raise RuntimeError("boom")

    try:
        await store.commit_async(doomed)
    except RuntimeError as error:
        if str(error) != "boom":
            raise
    Check.equal(before, len(store.conversations), "a failed transaction writes nothing")


if __name__ == "__main__":
    run_example(main)
