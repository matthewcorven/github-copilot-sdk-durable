// 12 - Recovery. Real JSONL storage; no SDK session or reported model is implied.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Check, DurableStore, DurableStoreOptions, DurableTask, DurableTaskRunner, Extension, ExtensionRegistry, TaskCommit, type PhaseHandler } from "../../lib/ts/durable.ts";

type Input = { to: number };
type Checkpoint = { phase: string; n: string };
const directory = mkdtempSync(join(tmpdir(), "durable-example-"));
const path = join(directory, "session.jsonl");
const printed: number[] = [];
const attempted: number[] = [];
let interrupt = true;
let reachedTickTwo!: () => void;
const tickTwo = new Promise<void>(resolve => { reachedTickTwo = resolve; });
let releaseTickTwo!: () => void;
const release = new Promise<void>(resolve => { releaseTickTwo = resolve; });
const ticker = DurableTask.definePhases<Input, Checkpoint>(
  "example.ticker", () => ({ phase: "tick", n: "1" }),
  new Map<string, PhaseHandler<Input, Checkpoint>>([
    ["tick", async (input, checkpoint, runtime) => {
      const n = Number(checkpoint.n);
      attempted.push(n);
      if (await runtime.memoAsync(`printed-${n}`) === null) {
        await runtime.memoAsync(`printed-${n}`, true);
        printed.push(n);
        console.log(`tick ${n}`);
      }
      if (n === 2 && interrupt) {
        reachedTickTwo();
        // Pause after the effect and memo, BEFORE saving the next checkpoint.
        await release;
        runtime.cancellationToken.throwIfAborted();
      }
      if (n === input.to) return runtime.commitAsync(() => TaskCommit.completed(`counted to ${n}`));
      return runtime.sleepUntilAsync(new Date(runtime.now.getTime() + 50),
        () => TaskCommit.running({ phase: "tick", n: String(n + 1) }));
    }],
  ]),
  (_input, _checkpoint, runtime) => runtime.commitAsync(() => TaskCommit.aborted()),
);
const registry = new ExtensionRegistry();
registry.install(new Extension("ticker", [], [], [ticker]));
const openStore = (): DurableStore => DurableStore.create(Object.assign(new DurableStoreOptions(), { path }));
try {
  const firstStore = openStore();
  const first = new DurableTaskRunner(firstStore, registry);
  const cancellation = new AbortController();
  let taskId: string;
  let timeout: ReturnType<typeof setTimeout> | undefined;
  try {
    const root = await firstStore.commitAsync(tx => tx.createConversation());
    const record = await firstStore.commitAsync(tx =>
      tx.createTask(ticker, { to: 5 }, { ownerConversationId: root.id }));
    taskId = record.id;
    const run = first.runTaskAsync(taskId, cancellation.signal);
    await Promise.race([
      tickTwo,
      run.then(() => { throw new Error("The first run ended before reaching tick 2"); }),
      new Promise<never>((_resolve, reject) => {
        timeout = setTimeout(() => reject(new Error("Tick 2 was not reached within five seconds")), 5000);
      }),
    ]);
    cancellation.abort();
    releaseTickTwo();
    await run;
    Check.equal(null, firstStore.task(taskId)?.outcome, "interruption saved no terminal outcome");
  } finally {
    clearTimeout(timeout);
    cancellation.abort();
    releaseTickTwo();
    await first.disposeAsync();
    firstStore.dispose();
  }
  const readerStore = openStore();
  let saved: { checkpoint: unknown; memos: Record<string, unknown> };
  try {
    const record = Check.notNull(readerStore.task(taskId), "the saved task exists");
    saved = { checkpoint: structuredClone(record.checkpoint), memos: structuredClone(record.memos) };
    Check.sequence([1, 2], printed, "opening the reader did not resume the task");
  } finally {
    readerStore.dispose();
  }
  interrupt = false;
  const secondStore = openStore();
  const second = new DurableTaskRunner(secondStore, registry);
  try {
    const counted = await second.waitForTaskAsync(taskId, AbortSignal.timeout(5000));
    Check.equal("completed", counted.status, "the reopened task finished");
    Check.equal("counted to 5", counted.result, "the reopened task produced its result");
    Check.sequence([1, 2, 3, 4, 5], printed, "no tick was printed twice, and none was skipped");
    const checkpoint = saved.checkpoint;
    Check.that(checkpoint !== null && typeof checkpoint === "object", "the saved checkpoint is an object");
    if (checkpoint === null || typeof checkpoint !== "object") throw new Error("Invalid checkpoint");
    Check.equal("tick", Reflect.get(checkpoint, "phase"), "the saved checkpoint is the interrupted phase");
    Check.equal("2", Reflect.get(checkpoint, "n"), "the saved checkpoint is the interrupted tick");
    Check.equal(2, Object.keys(saved.memos).length, "the memos for the two printed ticks were saved");
    Check.sequence([1, 2, 2, 3, 4, 5], attempted, "tick 2 really reran after reopening");
    Check.equal(true, saved.memos["printed-2"], "tick 2's memo survived the interrupted write");
    console.log(`after reopen: ${counted.status} ${counted.result}; attempts: ${JSON.stringify(attempted)}; printed: ${JSON.stringify(printed)}`);
  } finally {
    await second.disposeAsync();
    secondStore.dispose();
  }
} finally {
  rmSync(directory, { recursive: true });
}
