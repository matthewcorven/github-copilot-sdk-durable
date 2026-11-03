// 11 - Tasks. Run: node --experimental-transform-types examples/ts/11-tasks.ts
// Uses the native core runner; no SDK session or reported model is implied.
import { Check, DurableStore, DurableTask, DurableTaskRunner, Extension, ExtensionRegistry, TaskCommit, TaskStatus, type PhaseHandler } from "../../lib/ts/durable.ts";

type Input = { amount: number };
type Checkpoint = { phase: string; key?: string };
const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
const payments = new Map<string, number>();
const phases = new Map<string, PhaseHandler<Input, Checkpoint>>([
  ["prepare", (_input, _checkpoint, runtime) => runtime.commitAsync(() =>
    TaskCommit.running({ phase: "charge", key: `payment-${runtime.task.id}` }))],
  ["charge", (input, checkpoint, runtime) => {
    const key = Check.notNull(checkpoint.key, "the charge checkpoint holds its idempotency key");
    if (!payments.has(key)) payments.set(key, input.amount * 100);
    return runtime.commitAsync(() => TaskCommit.completed({ receipt: payments.get(key)! }));
  }],
]);
const payment = DurableTask.definePhases<Input, Checkpoint>(
  "example.payment", () => ({ phase: "prepare" }), phases,
  (_input, _checkpoint, runtime) => runtime.commitAsync(() => TaskCommit.aborted()),
);
registry.install(new Extension("payments", [], [], [payment]));
const runner = new DurableTaskRunner(store, registry);
try {
  const root = await store.commitAsync(tx => tx.createConversation());
  const task = await runner.createTaskAsync(payment, { amount: 5 }, { ownerConversationId: root.id });
  const paid = await runner.waitForTaskAsync(task.id);
  const result = paid.result;
  Check.that(result !== null && typeof result === "object" && !Array.isArray(result), "the receipt is an object");
  if (result === null || typeof result !== "object" || Array.isArray(result)) throw new Error("Invalid receipt");
  Check.equal("completed", paid.status, "the task completed");
  Check.equal(500, result["receipt"], "the receipt holds the amount that was charged, in cents");
  Check.equal(1, payments.size, "the payment effect happened exactly once");
  Check.equal(500, [...payments.values()][0], "the charged amount is the one that was asked for, in cents");
  const record = Check.notNull(store.task(task.id), "the saved task exists");
  Check.equal(TaskStatus.Terminal, record.status, "the saved record is terminal");
  const checkpoint = record.checkpoint;
  Check.that(checkpoint !== null && typeof checkpoint === "object" && !Array.isArray(checkpoint), "the checkpoint is an object");
  if (checkpoint === null || typeof checkpoint !== "object" || Array.isArray(checkpoint)) throw new Error("Invalid checkpoint");
  Check.equal("charge", checkpoint["phase"], "the saved checkpoint is the last phase");
  Check.that(record.outcome !== null, "the saved record holds the outcome");
  const rerun = await runner.waitForTaskAsync(task.id);
  Check.equal("completed", rerun.status, "waiting on a finished task returns its outcome");
  Check.equal(1, payments.size, "waiting on a finished task does not repeat the effect");
  console.log(`payment outcome: ${paid.status}`);
  console.log(`receipt: ${result["receipt"]}`);
  console.log("task status: Terminal");
  console.log(`task checkpoint: ${checkpoint["phase"]}`);
  console.log(`payments charged: ${payments.size}`);
} finally {
  await runner.disposeAsync();
  store.dispose();
}
