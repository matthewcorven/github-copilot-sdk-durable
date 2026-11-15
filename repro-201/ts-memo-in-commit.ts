// Port-independent control for #201 in TypeScript.
//
// It knows nothing about the guard: it calls `memoAsync` from inside a commit and races the call
// against a 2s timer. Against `lib/ts/durable.ts` before the guard the race is won by the timer
// (the call never settles); after it, the call rejects immediately.
//
//   (cd lib/ts && npx tsx ../../repro-201/ts-memo-in-commit.ts "$PWD/durable.ts")

import { setTimeout as delay } from "node:timers/promises";

const modulePath = process.argv[2];
if (modulePath === undefined) {
  console.error("usage: ts-memo-in-commit.ts <path-to-durable.ts>");
  process.exit(2);
}

const durable = await import(modulePath);
const { DurableStore, DurableTask, TaskCommit, DurableJson } = durable;

const store = DurableStore.createEphemeral();
const definition = DurableTask.definePhases(
  "repro.memo-in-commit",
  () => ({ phase: "run" }),
  new Map([
    [
      "run",
      (_input: never, _checkpoint: never, runtime: { commitInTransactionAsync: (f: (tx: unknown) => unknown) => Promise<unknown>; memoAsync: (key: string, value: unknown) => Promise<unknown>; task: { id: string } }) =>
        runtime.commitInTransactionAsync(async (tx: unknown) => {
          console.log("  inside the transaction; calling memoAsync");
          const memo = runtime.memoAsync("k", "written");
          const outcome = await Promise.race([
            memo.then(() => "returned", (error: unknown) => `threw ${(error as Error).name}`),
            delay(2000).then(() => "HANG"),
          ]);

          console.log(`RESULT: memoAsync inside a commit -> ${outcome}`);
          if (outcome === "HANG") {
            process.exit(7);
          }

          (tx as { memo: (taskId: string, key: string, value: unknown) => unknown }).memo(
            runtime.task.id,
            "k",
            "written",
          );
          return TaskCommit.completed(DurableJson.toElement({ ok: "yes" }));
        }),
    ],
  ]),
);

const record = await store.commitAsync(async (tx: { createTask: (d: unknown) => { id: string } }) =>
  tx.createTask(definition),
);

const registry = new durable.ExtensionRegistry();
registry.install(new durable.Extension("repro.memo-in-commit", [], [], [definition]));
const runner = new durable.DurableTaskRunner(store, registry);
console.log(`  task ${record.id} created; running it`);
const outcome = await Promise.race([
  runner.runTaskAsync(record.id),
  delay(5000).then(() => null),
]);
console.log(outcome === null ? "RESULT: the run never settled" : `RESULT: outcome=${outcome.status}`);
await runner.disposeAsync();
store.dispose();