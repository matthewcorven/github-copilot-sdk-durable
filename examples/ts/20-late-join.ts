// 20 - Late join (LIVE). A real SDK tool holds generation until the client joins.
import { AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry } from "../../lib/ts/durable.ts";
import type { SessionEvent, Tool } from "../../lib/ts/durable.ts";

const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
const deadline = new AbortController();
const deadlineTimer = setTimeout(() => deadline.abort(), 90000);
let signalStarted!: () => void;
let releaseJoin!: () => void;
const started = new Promise<void>(resolve => { signalStarted = resolve; });
const release = new Promise<void>(resolve => { releaseJoin = resolve; });
const waitForJoin: Tool = {
  name: "wait_for_join",
  description: "Wait for the client to join before generating the counting answer. Call this first.",
  parameters: { type: "object", properties: {} },
  handler: async (_args, invocation) => {
    signalStarted();
    const signal = invocation.signal
      ? AbortSignal.any([invocation.signal, deadline.signal]) : deadline.signal;
    await until(release, signal);
    signal.throwIfAborted();
    return "The client has joined. Now count from one to five, then say DONE.";
  },
};
registry.install(new Extension("late-join", [waitForJoin]));
let harness: DurableHarness | undefined;
let unsubscribe: (() => void) | undefined;
try {
  // The facade does not expose bootstrap cancellation; retain ownership until open settles.
  harness = await DurableHarness.openAsync(store, registry);
  const root = await until(harness.rootAsync(new AgentChoices({
    streaming: true, extensions: ["late-join"], tools: ["wait_for_join"],
    permissions: ["custom-tool:wait_for_join"],
  })), deadline.signal);
  const report = await until(root.waitForEffectiveModelAsync(), deadline.signal);
  console.log(`effective model: ${report.model ?? "(not reported)"}`);

  let completed = false;
  let failed = false;
  const run = root.submitAsync(
    "First call wait_for_join exactly once, and wait for its result before writing any counting text. " +
    "Then count slowly from one to five, one number per sentence, then say DONE.",
  ).then(answer => { completed = true; return answer; },
    error => { completed = failed = true; throw error; });

  async function requirePendingRun(milestone: Promise<unknown>, signal: AbortSignal, phase: string): Promise<void> {
    let winner: string;
    try {
      winner = await until(Promise.race([
        run.then(() => "run"), milestone.then(() => "milestone"),
      ]), signal);
    } catch (error) {
      if (signal.aborted) {
        if (failed) await run;
        throw new Error(`Late join timed out while ${phase}.`);
      }
      throw error;
    }
    if (winner === "run" || completed) {
      await run;
      throw new Error(`The run ended before joining while ${phase}.`);
    }
  }

  await requirePendingRun(started, deadline.signal, "waiting for wait_for_join");
  console.log("synchronization tool started; joining while the run is pending");
  let snapshot: SessionEvent[];
  const snapshotDeadline = new AbortController();
  const snapshotTimer = setTimeout(() => snapshotDeadline.abort(), 15000);
  const snapshotSignal = AbortSignal.any([snapshotDeadline.signal, deadline.signal]);
  try {
    while (true) {
      const read = root.contextAsync();
      await requirePendingRun(read, snapshotSignal, "reading the SDK snapshot");
      snapshot = await read;
      if (snapshot.some(event => event.type === "user.message")) break;
      await requirePendingRun(new Promise<void>(resolve => setTimeout(resolve, 50)),
        snapshotSignal, "waiting for an actual UserMessageEvent in the SDK snapshot");
    }
  } finally {
    clearTimeout(snapshotTimer);
  }
  console.log(`snapshot events: ${snapshot.length}`);
  for (const event of snapshot) console.log(`  ${event.type}`);
  const deltas: string[] = [];
  unsubscribe = root.session.on(event => {
    if (event.type === "assistant.message_delta" && event.data.deltaContent) {
      deltas.push(event.data.deltaContent);
      console.log(`event text delta: ${event.data.deltaContent}`);
    } else if (event.type === "assistant.streaming_delta") {
      console.log(`event: streamed ${event.data.totalResponseSizeBytes} bytes`);
    } else if (event.type === "assistant.message") {
      console.log(`event: final message (${event.data.content.length} chars)`);
    } else {
      console.log(`event: ${event.type}`);
    }
  });
  if (completed) {
    await run;
    throw new Error("The run ended before the live subscription was installed.");
  }
  releaseJoin();
  console.log("live subscription installed; synchronization tool released");
  let answer: Awaited<typeof run>;
  try {
    answer = await until(run, deadline.signal);
  } catch (error) {
    if (deadline.signal.aborted) {
      if (failed) await run;
      throw new Error("Late join timed out while awaiting the completed counting answer.");
    }
    throw error;
  }
  console.log(`answer: ${answer?.data.content}`);
  console.log(`deltas received after joining: ${deltas.length}`);
  Check.that(snapshot.length >= 1, "the late joiner received the current state");
  Check.that(snapshot.some(e => e.type === "user.message"), "the snapshot holds the user message");
  Check.that(deltas.length >= 1, "the late joiner received live deltas");
  const finished = Check.notNull(answer, "the run finished");
  Check.that(finished.data.content.trim().length > 0, "the answer is not empty");
} finally {
  deadline.abort();
  releaseJoin();
  clearTimeout(deadlineTimer);
  unsubscribe?.();
  try {
    await harness?.disposeAsync();
  } finally {
    store.dispose();
  }
}

function until<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const cleanup = () => signal.removeEventListener("abort", onAbort);
    const onAbort = () => { cleanup(); reject(signal.reason); };
    operation.then(value => { cleanup(); resolve(value); }, error => { cleanup(); reject(error); });
    if (signal.aborted) onAbort();
    else signal.addEventListener("abort", onAbort, { once: true });
  });
}
