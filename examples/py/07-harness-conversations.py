"""07 — Harness conversations. LIVE: models.list reasoning-effort resolution."""
from example import d, run_example
from check import Check


async def main(harness, store, registry):
    root = await harness.root_async(d.AgentChoices(model="gpt-6-luna", reasoning_effort="high"))

    async def greet(tx):
        return tx.append_entry(root.id, "message", "hello")

    hello = await root.commit_async(greet)
    print(f"effective model: {(await root.wait_for_effective_model_async()).model or '(not reported)'}")
    helper = await harness.create_conversation_async(d.AgentChoices(model="mai-code-1.1-flash", reasoning_effort="minimal"))
    retry = await harness.fork_async(root, hello)
    Check.equal("mai-code-1.1-flash", helper.agent().model, "the created conversation uses its own model")
    Check.equal("minimal", helper.agent().reasoning_effort, "the created conversation uses its own reasoning effort")
    Check.equal(root.agent().model, retry.agent().model, "the fork inherits the parent's model")
    Check.equal(root.agent().reasoning_effort, retry.agent().reasoning_effort, "the fork inherits the parent's reasoning effort")
    Check.not_none(store.conversation(retry.id), "the fork is in the store")
    Check.that(retry.id != root.id, "the fork is a different conversation")
    Check.equal(root.id, retry.record.parent_conversation_id, "the fork records its parent")
    Check.not_none(root.session_id, "the root has a native session")
    Check.not_none(helper.session_id, "the helper has a native session")
    Check.not_none(retry.session_id, "the fork has a native session")
    Check.that(root.session_id != helper.session_id, "each conversation has its own session")
    Check.that(root.session_id != retry.session_id, "the fork has its own session")
    Check.that(hello in retry.entries(10), "the fork inherited the fork entry")
    Check.equal(len(root.entries(10)), len(retry.entries(10)), "the fork inherited the parent's entries")


if __name__ == "__main__":
    run_example(main)
