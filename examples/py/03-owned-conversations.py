"""03 — Owned conversations. Plain harnesses record but never run supervisors."""
from example import d, run_example
from check import Check


async def main(harness, store, registry):
    async def phase(input, checkpoint, runtime):
        return d.TaskCommit.completed("done")

    async def abort(input, checkpoint, runtime):
        return d.TaskCommit.aborted()

    supervisor = d.DurableTask.define("example.supervisor", lambda: {"phase": "ready"}, phase, abort)
    agent_registry = d.DocumentDefinition("example.agent-registry", d.DocumentHistory.LATEST, d.ForkPolicy.INITIAL, lambda: {})
    chat = await harness.create_conversation_async()
    print(f"effective model: {(await chat.wait_for_effective_model_async()).model or '(not reported)'}")

    async def setup(tx):
        task = tx.create_task(supervisor, {}, owner_conversation_id=chat.id, background=True)
        child = tx.create_conversation(owner_task_id=task.id)
        draft = tx.doc(agent_registry, chat.id)
        draft.value["researcher"] = {"conversationId": child.id, "requestId": f"researcher:first-message:{task.id}"}
        draft.save(tx.append_entry(chat.id, "agent", "researcher registered"))
        return task.id, child

    supervisor_id, child = await chat.commit_async(setup)
    registry_value = chat.snapshot(agent_registry)
    task = Check.not_none(store.task(supervisor_id), "the supervisor task is in the store")
    Check.not_none(store.conversation(child.id), "the child conversation is in the store")
    Check.equal(supervisor_id, child.owner_task_id, "the child records its owning task")
    Check.equal(None, child.owner_conversation_id, "the child has no owning conversation")
    Check.equal(child.id, registry_value["researcher"]["conversationId"], "the registry points at the child")
    Check.equal(f"researcher:first-message:{supervisor_id}", registry_value["researcher"]["requestId"], "the registry holds a fixed request id")
    Check.that(task.background, "the supervisor task is background work")
    Check.equal(d.TaskStatus.PENDING, task.status, "the supervisor task has not run")
    conversations_before, tasks_before = len(store.conversations), len(store.tasks)

    async def doomed(tx):
        tx.create_task(supervisor, {}, owner_conversation_id=chat.id)
        tx.create_conversation(owner_task_id="t-doomed")
        draft = tx.doc(agent_registry, chat.id)
        draft.value["doomed"] = {"conversationId": "c-doomed"}
        draft.save(tx.append_entry(chat.id, "agent", "doomed"))
        raise RuntimeError("boom")

    try:
        await chat.commit_async(doomed)
    except RuntimeError as error:
        if str(error) != "boom":
            raise
    Check.equal(conversations_before, len(store.conversations), "a failed commit creates no conversation")
    Check.equal(tasks_before, len(store.tasks), "a failed commit creates no task")
    Check.that("doomed" not in chat.snapshot(agent_registry), "a failed commit writes no registry entry")


if __name__ == "__main__":
    run_example(main)
