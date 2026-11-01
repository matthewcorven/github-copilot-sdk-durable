// 03 — Owned conversations. Plain harnesses record but never run supervisors.
import { DocumentDefinition, DocumentHistory, DurableTask, ForkPolicy, TaskCommit, TaskStatus } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";

await runExample(async (harness, store) => {
  const supervisor = DurableTask.define(
    "example.supervisor", () => ({ phase: "ready" }),
    () => TaskCommit.completed("done"), () => TaskCommit.aborted(),
  );
  const agentRegistry = new DocumentDefinition<Record<string, Record<string, string>>>(
    "example.agent-registry", DocumentHistory.Latest, ForkPolicy.Initial, () => ({}),
  );
  const main = await harness.createConversationAsync();
  console.log(`effective model: ${(await main.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  const setup = await main.commitAsync(tx => {
    const task = tx.createTask(supervisor, {}, { ownerConversationId: main.id, background: true });
    const child = tx.createConversation({ ownerTaskId: task.id });
    const draft = tx.doc(agentRegistry, main.id);
    draft.value["researcher"] = { conversationId: child.id, requestId: `researcher:first-message:${task.id}` };
    draft.save(tx.appendEntry(main.id, "agent", "researcher registered"));
    return { supervisorId: task.id, child };
  });
  const registryValue = main.snapshot(agentRegistry)!;
  const task = Check.notNull(store.task(setup.supervisorId), "the supervisor task is in the store");
  Check.notNull(store.conversation(setup.child.id), "the child conversation is in the store");
  Check.equal(setup.supervisorId, setup.child.ownerTaskId, "the child records its owning task");
  Check.equal(null, setup.child.ownerConversationId, "the child has no owning conversation");
  Check.equal(setup.child.id, registryValue["researcher"]!["conversationId"], "the registry points at the child");
  Check.equal(`researcher:first-message:${setup.supervisorId}`, registryValue["researcher"]!["requestId"], "the registry holds a fixed request id");
  Check.that(task.background, "the supervisor task is background work");
  Check.equal(TaskStatus.Pending, task.status, "the supervisor task has not run");
  const conversationsBefore = store.conversations.length;
  const tasksBefore = store.tasks.length;
  try {
    await main.commitAsync(tx => {
      tx.createTask(supervisor, {}, { ownerConversationId: main.id });
      tx.createConversation({ ownerTaskId: "t-doomed" });
      const draft = tx.doc(agentRegistry, main.id);
      draft.value["doomed"] = { conversationId: "c-doomed" };
      draft.save(tx.appendEntry(main.id, "agent", "doomed"));
      throw new Error("boom");
    });
  } catch (error) {
    if (!(error instanceof Error) || error.message !== "boom") throw error;
  }
  Check.equal(conversationsBefore, store.conversations.length, "a failed commit creates no conversation");
  Check.equal(tasksBefore, store.tasks.length, "a failed commit creates no task");
  Check.that(!("doomed" in main.snapshot(agentRegistry)!), "a failed commit writes no registry entry");
});
