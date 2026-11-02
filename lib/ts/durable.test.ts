import assert from "node:assert/strict";
import { test, mock } from "node:test";
import { mkdtempSync, readFileSync, appendFileSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawn } from "node:child_process";
import { once } from "node:events";
import {
  AgentChoices, DocumentDefinition, DurableHarness, DurableHarnessOptions, DurableStore,
  DurableStoreOptions, DurableStoreFormat, ExtensionRegistry, PermissionPolicy, DurableTask,
  TaskCommit, DurableSandboxPolicy,
  Extension, ToolWrapper, DurableTaskRunner, TaskStatus, DurableConversation,
  type SessionEvent,
} from "./durable.js";
import { CopilotClient } from "@github/copilot-sdk";
import fs from "node:fs";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { syncBuiltinESMExports } from "node:module";

function canonicalFixture(mode: string, file: string): { ownerId: string; forkIds: string[] } {
  return JSON.parse(execFileSync("dotnet", ["run", "--file",
    fileURLToPath(new URL("./canonical-fixture.cs", import.meta.url)), "--", mode, file],
    { encoding: "utf8" }).trim().split("\n").at(-1)!);
}

function barrier() {
  let release!: () => void;
  const promise = new Promise<void>(resolve => { release = resolve; });
  return { promise, resolve: release };
}

test("canonical repeated definitions retain every version across reopen", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-canonical-"));
  const file = join(dir, "store.jsonl");
  let store: DurableStore | undefined;
  try {
    const fixture = canonicalFixture("create", file);
    store = DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file }));
    const notes = store.document("interop.notes", fixture.ownerId)!;
    assert.deepEqual(notes.versions.map(v => v.value), [["first"], ["second"]]);
    assert.deepEqual(notes.asOfOrBefore(notes.versions[0]!.entryId, id => store!.sequenceOf(id)), ["first"]);
    assert.deepEqual(notes.latest, ["second"]);
    const client = new CopilotClient();
    client.stop = async () => [];
    client.resumeSession = async id => sdkSession(id).sdk;
    const harness = new DurableHarness(client, store, new ExtensionRegistry());
    try {
      const child = await harness.resumeConversationAsync(fixture.forkIds[0]!);
      assert.equal(child.sessionId, "canonical-child");
    } finally { await harness.disposeAsync(); }
    store.dispose();
    store = undefined;
    canonicalFixture("verify", file);
  } finally { store?.dispose(); rmSync(dir, { recursive: true }); }
});

test("task runner retains exclusive scheduler ownership until its active phase drains", async () => {
  const store = DurableStore.createEphemeral();
  const registry = new ExtensionRegistry();
  const entered = barrier(), release = barrier();
  const definition = DurableTask.define("exclusive", () => ({ phase: "run" }), async (_input, _checkpoint, runtime) => {
    entered.resolve();
    await release.promise;
    return runtime.commitAsync(() => TaskCommit.completed(null));
  });
  registry.install(new Extension("exclusive", [], [], [definition]));
  const runner = new DurableTaskRunner(store, registry);
  let running: Promise<unknown> | undefined, closing: Promise<unknown> | undefined;
  try {
    const task = await store.commitAsync(tx => tx.createTask(definition, null));
    running = runner.runTaskAsync(task.id);
    await entered.promise;
    closing = runner.disposeAsync();
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.throws(() => new DurableTaskRunner(store, registry), /runner|runtime/);
    release.resolve();
    await Promise.all([closing, running]);
    const replacement = new DurableTaskRunner(store, registry);
    await replacement.disposeAsync();
  } finally {
    release.resolve();
    await Promise.allSettled([closing, running]);
    await runner.disposeAsync();
    store.dispose();
  }
});

test("a failing recovered task cannot release SDK or scheduler before another run drains", async () => {
  const store = DurableStore.createEphemeral();
  const registry = new ExtensionRegistry();
  const badEntered = barrier(), goodEntered = barrier(), badRelease = barrier(), goodRelease = barrier();
  const definition = DurableTask.define("task-errors", () => ({ phase: "run" }), async (input, _checkpoint, runtime) => {
    const bad = input === "bad";
    (bad ? badEntered : goodEntered).resolve();
    await (bad ? badRelease : goodRelease).promise;
    return runtime.commitAsync(() => TaskCommit.completed(input));
  });
  registry.install(new Extension("task-errors", [], [], [definition]));
  const client = new CopilotClient();
  let stops = 0;
  client.stop = async () => { stops++; return []; };
  const harness = new DurableHarness(client, store, registry);
  const original = new Error("task checkpoint storage failed");
  let recovery: Promise<unknown> | undefined, closing: Promise<unknown> | undefined;
  try {
    const [bad, good] = await store.commitAsync(tx =>
      [tx.createTask(definition, "bad"), tx.createTask(definition, "good")]);
    const commit = store.commitTaskCheckpointAsync.bind(store);
    mock.method(store, "commitTaskCheckpointAsync", (...[id, body]: Parameters<DurableStore["commitTaskCheckpointAsync"]>) => {
      if (id === bad!.id) return Promise.reject(original);
      return commit(id, body);
    });
    recovery = assert.rejects(harness.resumeAsync(), error => error === original);
    await Promise.all([badEntered.promise, goodEntered.promise]);
    closing = assert.rejects(harness.disposeAsync(), error => error === original);
    badRelease.resolve();
    await recovery;
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.equal(stops, 0);
    assert.throws(() => new DurableTaskRunner(store, registry), /runner|runtime/);
    goodRelease.resolve();
    await closing;
    assert.equal(stops, 1);
    assert.equal(store.task(good!.id)!.status, TaskStatus.Terminal);
    assert.equal(store.task(bad!.id)!.status, TaskStatus.Pending);
  } finally {
    badRelease.resolve(); goodRelease.resolve();
    await Promise.allSettled([closing, recovery]);
    await harness.disposeAsync().catch(error => { assert.equal(error, original); });
    store.dispose();
  }
});

test("canonical numeric fork states resume and TS forks persist numeric pending and completed states", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-fork-wire-"));
  const file = join(dir, "store.jsonl");
  let store: DurableStore | undefined;
  try {
    const fixture = canonicalFixture("create", file);
    store = DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file }));
    const client = new CopilotClient();
    client.stop = async () => [];
    client.resumeSession = async id => sdkSession(id).sdk;
    mock.getter(client, "rpc", () => ({ sessions: { fork: async () => ({ sessionId: "ts-child" }) } }));
    const harness = new DurableHarness(client, store, new ExtensionRegistry());
    try {
      const parent = await harness.resumeConversationAsync(fixture.forkIds[0]!);
      assert.equal(parent.sessionId, "canonical-child");
      const entry = await store.commitAsync(tx => tx.appendEntry(parent.id, "boundary"));
      const child = await parent.forkAsync(entry);
      const versions = store.document("durable.fork", child.id)!.versions;
      assert.deepEqual(versions.slice(-2).map(v => (v.value as { state: number }).state), [0, 1]);
    } finally { await harness.disposeAsync(); }
    store.dispose();
    store = undefined;
    assert.equal(canonicalFixture("verify", file).forkIds.length, 2);
  } finally { store?.dispose(); rmSync(dir, { recursive: true }); }
});

test("failed checkpoint and memo append/fsync restore memory, bytes and retry after reopen", async t => {
  for (const operation of ["terminal", "checkpoint", "memo"] as const) {
    for (const fault of ["writeFileSync", "fsyncSync"] as const) {
      await t.test(`${operation}/${fault}`, async () => {
        const dir = mkdtempSync(join(tmpdir(), "durable-ts-failure-"));
        const file = join(dir, "store.jsonl");
        const options = Object.assign(new DurableStoreOptions(), { path: file });
        let store = DurableStore.create(options);
        try {
          const definition = DurableTask.define("failure", () => ({ phase: "run" }),
            async () => TaskCommit.completed(null));
          const task = await store.commitAsync(tx => tx.createTask(definition, null));
          const before = readFileSync(file);
          const checkpoint = structuredClone(task.checkpoint);
          const original = new Error(`injected ${fault}`);
          const real = fs[fault];
          let fail = true;
          const injection = mock.method(fs, fault, (...args: unknown[]) => {
            if (fail) {
              fail = false;
              if (fault === "writeFileSync") Reflect.apply(real, fs, [args[0], '{"partial":']);
              throw original;
            }
            return Reflect.apply(real, fs, args);
          });
          syncBuiltinESMExports();
          const write = () => operation === "memo"
            ? store.memoAsync(task.id, "once", { saved: true })
            : store.commitTaskCheckpointAsync(task.id, () => operation === "terminal"
              ? TaskCommit.completed({ saved: true }) : TaskCommit.running({ phase: "next" }));
          try {
            await assert.rejects(write(), error => error === original);
            assert.equal(task.status, TaskStatus.Pending);
            assert.deepEqual(task.checkpoint, checkpoint);
            assert.equal(task.outcome, null);
            assert.deepEqual(task.memos, {});
            assert.deepEqual(readFileSync(file), before);
            // A second rollback must not resurrect the failed first update.
            fail = true;
            await assert.rejects(write(), error => error === original);
            assert.equal(task.status, TaskStatus.Pending);
            assert.deepEqual(task.memos, {});
            assert.deepEqual(readFileSync(file), before);
          } finally { injection.mock.restore(); syncBuiltinESMExports(); }
          store.dispose();
          store = DurableStore.create(options);
          assert.equal(store.task(task.id)!.status, TaskStatus.Pending);
          assert.deepEqual(store.task(task.id)!.memos, {});
          await write();
          store.dispose();
          store = DurableStore.create(options);
          if (operation === "memo") assert.deepEqual(await store.memoAsync(task.id, "once"), { saved: true });
          else if (operation === "terminal") {
            assert.equal(store.task(task.id)!.status, TaskStatus.Terminal);
            assert.notEqual(store.task(task.id)!.outcome, null);
          } else {
            assert.equal(store.task(task.id)!.status, TaskStatus.Running);
            assert.deepEqual(store.task(task.id)!.checkpoint, { phase: "next" });
          }
        } finally { store.dispose(); rmSync(dir, { recursive: true }); }
      });
    }
  }
});

test("recovery runs committed Pending work after actual close and reopen", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-pending-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "store.jsonl") });
  let store = DurableStore.create(options);
  let calls = 0;
  const definition = DurableTask.define("pending", () => ({ phase: "run" }),
    async (_input, _checkpoint, runtime) => {
      calls++;
      return runtime.commitAsync(() => TaskCommit.completed({ recovered: true }));
    });
  const registry = new ExtensionRegistry();
  registry.install(new Extension("recovery", [], [], [definition]));
  const client = new CopilotClient();
  client.stop = async () => [];
  let harness: DurableHarness | undefined;
  try {
    const record = await store.commitAsync(tx => tx.createTask(definition, null));
    assert.equal(record.status, TaskStatus.Pending);
    assert.equal(calls, 0);
    store.dispose();
    store = DurableStore.create(options);
    harness = new DurableHarness(client, store, registry);
    const outcomes = await harness.resumeAsync();
    assert.equal(outcomes.length, 1);
    assert.equal(calls, 1);
    assert.equal(store.task(record.id)!.status, TaskStatus.Terminal);
    store.dispose();
    store = DurableStore.create(options);
    assert.equal(store.task(record.id)!.status, TaskStatus.Terminal);
  } finally { await harness?.disposeAsync(); store.dispose(); rmSync(dir, { recursive: true }); }
});

test("shutdown drains every checkpoint and recovered phase before stopping SDK and releasing writer", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-drain-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "store.jsonl") });
  const store = DurableStore.create(options);
  const client = new CopilotClient();
  const order: string[] = [];
  client.stop = async () => { order.push("stop"); return []; };
  const registry = new ExtensionRegistry();
  const entered = barrier(), releaseRun = barrier();
  const releaseBarrier = barrier(), barrierEntered = barrier();
  const definition = DurableTask.define("drain", () => ({ phase: "run" }), async (_input, _checkpoint, runtime) => {
    entered.resolve();
    await releaseRun.promise;
    const result = await runtime.commitAsync(() => TaskCommit.completed(null));
    order.push("run");
    return result;
  });
  registry.install(new Extension("drain", [], [], [definition]));
  const harness = new DurableHarness(client, store, registry);
  const original = new Error("first checkpoint");
  let recovery: Promise<unknown> | undefined, closing: Promise<unknown> | undefined;
  try {
    const records = await store.commitAsync(tx => {
      const task = tx.createTask(definition, null);
      task.status = TaskStatus.Running;
      return [tx.createConversation(), tx.createConversation()];
    });
    harness.findConversation(records[0]!.id)!.checkpointBarrierAsync = async () => { throw original; };
    harness.findConversation(records[1]!.id)!.checkpointBarrierAsync = async () => {
      barrierEntered.resolve();
      await releaseBarrier.promise;
      await store.commitAsync(tx => tx.appendEntry(records[1]!.id, "last-checkpoint"));
      order.push("barrier");
    };
    recovery = harness.resumeAsync();
    await entered.promise;
    closing = assert.rejects(harness.disposeAsync(), error => error === original);
    await barrierEntered.promise;
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.deepEqual(order, []);
    assert.throws(() => DurableStore.create(options), /locked/);
    releaseRun.resolve();
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.deepEqual(order, ["run"]);
    releaseBarrier.resolve();
    await Promise.all([closing, recovery]);
    assert.deepEqual(order, ["run", "barrier", "stop"]);
    store.dispose();
    const reopened = DurableStore.create(options);
    try {
      assert.equal(reopened.tasks[0]!.status, TaskStatus.Terminal);
      assert.equal(reopened.entries(records[1]!.id).length, 1);
    } finally { reopened.dispose(); }
  } finally {
    releaseBarrier.resolve(); releaseRun.resolve();
    await Promise.allSettled([closing, recovery]);
    await harness.disposeAsync().catch(error => { assert.equal(error, original); });
    store.dispose(); rmSync(dir, { recursive: true });
  }
});

test("harness disposal is idempotent and stops SDK after checkpoint failure", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient();
  let stops = 0;
  client.stop = async () => { stops++; return []; };
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    const record = await store.commitAsync(tx => tx.createConversation());
    harness.findConversation(record.id)!.checkpointBarrierAsync = async () => { throw new Error("checkpoint failed"); };
    await assert.rejects(harness.disposeAsync(), /checkpoint failed/);
    await assert.rejects(harness.disposeAsync(), /checkpoint failed/);
    assert.equal(stops, 1);
  } finally { store.dispose(); }
});

test("shutdown preserves checkpoint failures alongside later SDK stop failures", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient();
  const checkpoint = new Error("checkpoint"), stop = new Error("stop");
  client.stop = async () => [stop];
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    const record = await store.commitAsync(tx => tx.createConversation());
    harness.findConversation(record.id)!.checkpointBarrierAsync = async () => { throw checkpoint; };
    await assert.rejects(harness.disposeAsync(), error => {
      assert(error instanceof AggregateError);
      assert.equal(error.cause, checkpoint);
      assert.deepEqual(error.errors, [checkpoint, stop]);
      return true;
    });
  } finally { store.dispose(); }
});

test("host hook tolerance is opt-in and matches only the C# denial classifier", () => {
  assert.equal(new DurableHarnessOptions().tolerateHostHookFailures, false);
  assert.equal(new DurableHarnessOptions({ tolerateHostHookFailures: true }).tolerateHostHookFailures, true);
  for (const message of ["postToolUse hook: Operation not permitted", "HOOK denied by SANDBOX", "hook exited with code 126"])
    assert.equal(DurableConversation.isHostHookFailure(message), true);
  for (const message of [null, "", "Operation not permitted", "tool failed", "hook timed out"])
    assert.equal(DurableConversation.isHostHookFailure(message), false);
});

test("JSONL native lock refuses another process and releases after a crash", { timeout: 5000 }, async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-lock-"));
  const file = join(dir, "store.jsonl");
  const child = spawn(process.execPath, ["-e",
    'const fs=require("node:fs"), ext=require("fs-ext"); const fd=fs.openSync(process.argv[1],"a+"); ext.flockSync(fd,"exnb"); console.log("locked"); setInterval(()=>{},1000);',
    `${file}.lock`], { stdio: ["ignore", "pipe", "pipe"] });
  const exited = once(child, "exit");
  try {
    await Promise.race([
      once(child.stdout, "data"),
      exited.then(() => { throw new Error("Lock helper exited before acquiring its native lock."); }),
    ]);
    const options = Object.assign(new DurableStoreOptions(), { path: file, format: DurableStoreFormat.Jsonl });
    assert.throws(() => DurableStore.create(options), /lock|locked/i);
    child.kill("SIGKILL");
    await exited;
    const reopened = DurableStore.create(options);
    reopened.dispose();
  } finally {
    if (child.exitCode === null && child.signalCode === null) { child.kill("SIGKILL"); await exited; }
    rmSync(dir, { recursive: true });
  }
});

test("JSONL locks a fresh nested path before replay and retains both writers' identities", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-fresh-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "nested", "store.jsonl") });
  let first: DurableStore | undefined;
  let competing: DurableStore | undefined;
  try {
    first = DurableStore.create(options);
    assert.throws(() => { competing = DurableStore.create(options); }, /locked/i);
    const one = await first.commitAsync(tx => tx.createConversation());
    first.dispose();
    competing = DurableStore.create(options);
    const two = await competing.commitAsync(tx => tx.createConversation());
    assert.notEqual(one.id, two.id);
    competing.dispose();
    first = DurableStore.create(options);
    assert.deepEqual(first.conversations.map(c => c.id), [one.id, two.id]);
  } finally { competing?.dispose(); first?.dispose(); rmSync(dir, { recursive: true }); }
});

test("Auto rejects compact and formatted legacy snapshots without changing any bytes", () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-legacy-"));
  const file = join(dir, "store.json");
  const snapshot = { sequence: 4, conversations: [{ id: "c1" }], documents: [], tasks: [], entries: {}, entryRecords: [] };
  try {
    for (const text of [JSON.stringify(snapshot), JSON.stringify(snapshot, null, 2), JSON.stringify(snapshot) + "\n"]) {
      writeFileSync(file, text);
      let unexpected: DurableStore | undefined;
      try {
        assert.throws(() => { unexpected = DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file })); },
          /legacy.*unsupported|unsupported.*legacy/i);
      } finally { unexpected?.dispose(); }
      assert.deepEqual(readFileSync(file), Buffer.from(text));
      // A failed open must release its lifetime lock as well as preserve the data.
      assert.throws(() => DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file })), /legacy/i);
    }
  } finally { rmSync(dir, { recursive: true }); }
});

test("MCP grants match server/tool scopes without cross-server collisions", async () => {
  const invoke = (grants: string[], serverName: string, toolName: string) => PermissionPolicy.handler(grants)({
    kind: "mcp", serverName, toolName, toolTitle: toolName, readOnly: true,
  }, { sessionId: "test" });
  assert.equal((await invoke(["mcp:trusted/read"], "trusted", "read")).kind, "approve-once");
  assert.equal((await invoke(["mcp:trusted/read"], "other", "read")).kind, "reject");
  assert.equal((await invoke(["mcp:trusted"], "other", "trusted")).kind, "reject");
  assert.equal((await invoke(["mcp:trusted"], "trusted", "write")).kind, "approve-once");
  assert.equal((await invoke(["mcp:*"], "other", "read")).kind, "approve-once");
});

function sdkSession(id: string, history: SessionEvent[] = []) {
  const session = new CopilotClient().createSession;
  type Session = Awaited<ReturnType<typeof session>>;
  const listeners = new Set<(event: SessionEvent) => void>();
  const stub: Partial<Session> = {
    sessionId: id,
    on(handler: (event: SessionEvent) => void) { listeners.add(handler); return () => { listeners.delete(handler); }; },
    getEvents: async () => history,
    send: async () => "sent",
    setTools: async () => {},
  };
  const sdk = stub as Session;
  return { sdk, listeners, emit(event: SessionEvent) { for (const listener of [...listeners]) listener(event); } };
}
function sdkEvent(type: string, id: string, data: object): SessionEvent {
  return { type, id, timestamp: new Date().toISOString(), parentId: null, data } as SessionEvent;
}

test("timed-out submissions reject queued and later sends without overwriting the admission", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient();
  const session = sdkSession("native");
  client.createSession = async () => session.sdk;
  client.stop = async () => [];
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    const chat = await harness.rootAsync();
    let sends = 0;
    session.sdk.send = async () => { sends++; return "sent"; };
    const first = assert.rejects(chat.submitAsync("first", 10), /timed out/);
    const next = chat.submitAsync("second", 100);
    const blocked = assert.rejects(next, /unsettled admission/);
    const lateEvents = new Promise<void>(resolve => setTimeout(() => {
      session.emit(sdkEvent("assistant.message", "old-answer", { content: "first answer" }));
      session.emit(sdkEvent("session.idle", "old-idle", {}));
      resolve();
    }, 30));
    await Promise.all([first, blocked]);
    await lateEvents;
    const admission = chat.admission();
    assert.equal(admission?.["prompt"], "first");
    assert.equal(admission?.["state"], "uncertain");
    assert.equal(sends, 1);
    await assert.rejects(chat.submitAsync("third"), /unsettled admission/);
    assert.deepEqual(chat.admission(), admission);
    assert.equal(chat.checkpoint(), null);
    assert.equal(session.listeners.size, 1);
  } finally { await harness.disposeAsync(); store.dispose(); }
});

test("send failure preserves its original error and leaves recovery blocking later sends", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient();
  const session = sdkSession("native");
  client.createSession = async () => session.sdk;
  client.stop = async () => [];
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    const chat = await harness.rootAsync();
    const original = new Error("send failed");
    const send = mock.method(session.sdk, "send", async () => { throw original; });
    await assert.rejects(chat.submitAsync("first"), error => error === original);
    await assert.rejects(chat.submitAsync("next"), /unsettled admission/);
    assert.equal(send.mock.callCount(), 1);
    assert.equal(chat.admission()?.["state"], "uncertain");
    assert.equal(chat.admission()?.["prompt"], "first");
    assert.equal(session.listeners.size, 1);
  } finally { await harness.disposeAsync(); store.dispose(); }
});

test("pending, abandoned, unbound and mismatched native forks never get an empty replacement", async () => {
  for (const [state, bound, intentSession] of [
    ["pending", false, null], ["pending", true, "native"], ["abandoned", true, "native"],
    ["completed", false, "native"], ["completed", true, "wrong-native"],
    [0, false, null], [0, true, "native"], [2, true, "native"],
    [1, false, "native"], [1, true, "wrong-native"],
  ] as const) {
    const store = DurableStore.createEphemeral();
    const client = new CopilotClient();
    client.stop = async () => [];
    const create = mock.method(client, "createSession", async () => sdkSession("empty").sdk);
    const resume = mock.method(client, "resumeSession", async () => sdkSession("bound").sdk);
    const harness = new DurableHarness(client, store, new ExtensionRegistry());
    try {
      const child = await store.commitAsync(tx => {
        const parent = tx.createConversation();
        const boundary = tx.appendEntry(parent.id, "boundary");
        const fork = tx.createConversation({ parentConversationId: parent.id, forkEntryId: boundary });
        const intent = tx.doc(new DocumentDefinition("durable.fork"), fork.id);
        intent.value = { state, sessionId: intentSession };
        intent.save(tx.appendEntry(fork.id, "fork"));
        if (bound) {
          const binding = tx.doc(new DocumentDefinition("durable.session-binding"), fork.id);
          binding.value = { sessionId: "native" };
          binding.save(tx.appendEntry(fork.id, "session-binding"));
        }
        return fork;
      });
      await assert.rejects(harness.resumeConversationAsync(child.id), /fork.*recovery/i);
      assert.equal(create.mock.callCount(), 0);
      assert.equal(resume.mock.callCount(), 0);
    } finally { await harness.disposeAsync(); store.dispose(); }
  }
});

test("completed legacy string fork intents remain resumable with an exact native binding", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient();
  client.stop = async () => [];
  client.resumeSession = async id => sdkSession(id).sdk;
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    const child = await store.commitAsync(tx => {
      const parent = tx.createConversation();
      const fork = tx.createConversation({ parentConversationId: parent.id, forkEntryId: "boundary" });
      for (const [kind, value] of [
        ["durable.fork", { state: "completed", sessionId: "legacy" }],
        ["durable.session-binding", { sessionId: "legacy" }],
      ] as const) {
        const draft = tx.doc(new DocumentDefinition(kind), fork.id);
        draft.value = value;
        draft.save(tx.appendEntry(fork.id, "binding"));
      }
      return fork;
    });
    assert.equal((await harness.resumeConversationAsync(child.id)).sessionId, "legacy");
  } finally { await harness.disposeAsync(); store.dispose(); }
});

test("history hydration is read-only and deduplicated across create, resume and real store reopen", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-hydration-"));
  const file = join(dir, "store.jsonl");
  const options = Object.assign(new DurableStoreOptions(), { path: file });
  const history = [
    sdkEvent("session.start", "start", { selectedModel: "reported-model" }),
    sdkEvent("tool.execution_start", "tool-start", { toolCallId: "call", toolName: "read", arguments: { path: "notes" } }),
    sdkEvent("tool.execution_complete", "tool-end", { toolCallId: "call", success: true, result: { content: "notes" } }),
    sdkEvent("assistant.message", "answer", { content: "final answer" }),
  ];
  let store = DurableStore.create(options);
  const client = new CopilotClient();
  const session = sdkSession("native");
  client.createSession = async () => session.sdk;
  client.resumeSession = async () => session.sdk;
  client.stop = async () => [];
  let harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    let chat = await harness.rootAsync();
    session.sdk.send = async () => {
      for (const event of history) session.emit(event);
      session.emit(sdkEvent("session.idle", "idle", {}));
      return "sent";
    };
    await chat.submitAsync("read");
    const id = chat.id;
    const checkpoint = chat.checkpoint();
    const bytes = readFileSync(file);
    const resumed = sdkSession("native", history);
    client.resumeSession = async () => resumed.sdk;
    for (let i = 0; i < 2; i++) {
      await harness.disposeAsync(); store.dispose();
      store = DurableStore.create(options);
      harness = new DurableHarness(client, store, new ExtensionRegistry());
      chat = await harness.resumeConversationAsync(id);
      await chat.checkpointBarrierAsync();
      assert.deepEqual(chat.checkpoint(), checkpoint);
      assert.deepEqual(readFileSync(file), bytes);
      assert.deepEqual(chat.answers, ["final answer"]);
      assert.equal((await chat.waitForEffectiveModelAsync(10)).model, "reported-model");
      chat.detach();
      await harness.resumeConversationAsync(id);
      assert.deepEqual(chat.answers, ["final answer"]);
      // Re-delivery of an earlier completion cannot regress the final checkpoint.
      resumed.emit({ ...history[2]!, id: "redelivered-tool" });
      await chat.checkpointBarrierAsync();
      assert.deepEqual(readFileSync(file), bytes);
    }
    const creating = sdkSession("created-with-history", history);
    client.createSession = async () => creating.sdk;
    const created = await harness.createConversationAsync();
    await created.checkpointBarrierAsync();
    assert.equal(created.checkpoint(), null);
    assert.deepEqual(created.answers, ["final answer"]);
  } finally { await harness.disposeAsync(); store.dispose(); rmSync(dir, { recursive: true }); }
});

test("forks inherit historical admissions, but child-owned uncertainty and incomplete native forks require recovery", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient();
  const parentSession = sdkSession("parent");
  const inherited = [sdkEvent("session.start", "start", { selectedModel: "fork-model" }),
    sdkEvent("assistant.message", "parent-answer", { content: "inherited answer" })];
  const childSession = sdkSession("child", inherited);
  client.createSession = async () => parentSession.sdk;
  client.resumeSession = async () => childSession.sdk;
  let failFork = false;
  mock.getter(client, "rpc", () => ({ sessions: { fork: async () => {
    if (failFork) throw new Error("fork failed");
    return { sessionId: "child" };
  } } }));
  client.stop = async () => [];
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    const parent = await harness.rootAsync();
    let boundary = "";
    parentSession.sdk.send = async () => {
      boundary = await parent.appendEntryAsync("tool-created");
      parentSession.emit(sdkEvent("assistant.message", "done", { content: "parent done" }));
      parentSession.emit(sdkEvent("session.idle", "idle", {}));
      return "sent";
    };
    await parent.submitAsync("first");
    const child = await parent.forkAsync(boundary);
    assert.equal(child.admission()?.["state"], "admitted");
    assert.deepEqual(child.answers, ["inherited answer"]);
    assert.equal((await child.waitForEffectiveModelAsync(10)).model, "fork-model");
    child.detach();
    assert.equal(await harness.resumeConversationAsync(child.id), child);
    await assert.rejects(child.submitAsync("child work", 5), /timed out/);
    await assert.rejects(child.submitAsync("child retry"), /unsettled admission/);
    child.detach();
    await assert.rejects(harness.resumeConversationAsync(child.id), /unsettled admission/);
    failFork = true;
    await assert.rejects(parent.forkAsync(boundary), /fork failed/);
    const pending = store.conversations.at(-1)!;
    const creates = mock.method(client, "createSession");
    await assert.rejects(harness.resumeConversationAsync(pending.id), /fork.*recovery/i);
    assert.equal(creates.mock.callCount(), 0);
    const intent = new DocumentDefinition<Record<string, string>>("durable.fork");
    for (const state of ["abandoned", "completed"]) {
      await store.commitAsync(tx => {
        const draft = tx.doc(intent, pending.id);
        draft.value = { state };
        draft.save(tx.appendEntry(pending.id, "fork"));
      });
      await assert.rejects(harness.resumeConversationAsync(pending.id), /fork.*recovery/i);
      assert.equal(creates.mock.callCount(), 0);
    }
  } finally { await harness.disposeAsync(); store.dispose(); }
});

test("JSONL retains exact CRLF/mixed UTF8 boundaries and discards all unterminated tails", async () => {
  for (const endings of [["\r\n", "\r\n"], ["\n", "\r\n"]]) {
    for (const tail of ["", '{"v":1', JSON.stringify({
      v: 1, seq: 3, type: "conversation", payload: { id: "torn", createdAt: "2026-11-01T00:00:00Z" },
    })]) {
      const dir = mkdtempSync(join(tmpdir(), "durable-ts-boundary-"));
      const file = join(dir, "store.jsonl");
      const options = Object.assign(new DurableStoreOptions(), { path: file, format: DurableStoreFormat.Jsonl });
      const ids = ["\u96ea\u00e9", "\ud83d\ude80"];
      const prefix = ids.map((id, i) => JSON.stringify({
        v: 1, seq: i + 1, type: "conversation", payload: { id, createdAt: "2026-11-01T00:00:00Z" },
      }) + endings[i]).join("");
      writeFileSync(file, prefix + tail);
      let store = DurableStore.create(options);
      try {
        assert.deepEqual(store.conversations.map(c => c.id), ids);
        assert.equal(readFileSync(file).byteLength, Buffer.byteLength(prefix));
        assert.equal(readFileSync(file, "utf8"), prefix);
        assert.throws(() => DurableStore.create(options), /lock|locked/i);
        const added = await store.commitAsync(tx => tx.createConversation());
        store.dispose();
        store = DurableStore.create(options);
        assert.deepEqual(store.conversations.map(c => c.id), [...ids, added.id]);
        assert(readFileSync(file, "utf8").endsWith("\n"));
      } finally { store.dispose(); rmSync(dir, { recursive: true }); }
    }
  }
});

test("JSONL truncates a file containing only an unterminated record to zero", () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-empty-tail-"));
  const file = join(dir, "store.jsonl");
  try {
    for (const tail of ['{"v":1', '{"v":1,"seq":1,"type":"conversation","payload":{"id":"tail"}}']) {
      writeFileSync(file, tail);
      const store = DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file, format: DurableStoreFormat.Jsonl }));
      try {
        assert.equal(store.conversations.length, 0);
        assert.equal(readFileSync(file).byteLength, 0);
      } finally { store.dispose(); }
    }
  } finally { rmSync(dir, { recursive: true }); }
});

test("JSONL commits, rollback, task memos and torn-tail reopen", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-"));
  const file = join(dir, "store.jsonl");
  const options = Object.assign(new DurableStoreOptions(), { path: file, format: DurableStoreFormat.Jsonl });
  const doc = new DocumentDefinition<string[]>("notes", undefined, undefined, () => []);
  const definition = DurableTask.define("job", () => ({ phase: "run" }), async (_input, _checkpoint, runtime) =>
    runtime.commitAsync(() => TaskCommit.completed(null)));
  let store = DurableStore.create(options);
  try {
    const record = await store.commitAsync(tx => {
      const conversation = tx.createConversation();
      const entry = tx.appendEntry(conversation.id, "note");
      const draft = tx.doc(doc, conversation.id); draft.value = ["saved"]; draft.save(entry);
      return { conversation, task: tx.createTask(definition, null), entry };
    });
    await store.memoAsync(record.task.id, "once", true);
    await assert.rejects(store.commitAsync(tx => {
      const draft = tx.doc(doc, record.conversation.id);
      draft.value = ["bad"]; draft.save(tx.appendEntry(record.conversation.id, "bad"));
      throw new Error("rollback");
    }), /rollback/);
    const lines = readFileSync(file, "utf8").trim().split("\n").map(text => JSON.parse(text));
    assert(lines.every(r => r.v === 1 && Number.isSafeInteger(r.seq)));
    assert(lines.some(r => r.type === "task" && r.payload.memos.once === true));
    store.dispose();
    appendFileSync(file, '{"v":1');
    store = DurableStore.create(options);
    assert.deepEqual(store.document(doc.kind, record.conversation.id)?.latest, ["saved"]);
    assert.equal(await store.memoAsync(record.task.id, "once"), true);
    assert.deepEqual(store.entries(record.conversation.id), [record.entry]);
    assert(!readFileSync(file, "utf8").endsWith('{"v":1'));
  } finally { store.dispose(); rmSync(dir, { recursive: true }); }
});

test("sandbox mapping preserves exact wire fields, permissions respect boundaries", () => {
  const config = DurableHarness.buildSandboxConfig(new DurableSandboxPolicy({
    enabled: true, allowBypass: false, sandboxMcpServers: false, sandboxLspServers: true,
    keychainAccess: false, auth: { gh: true, git: false },
    filesystem: { deniedPaths: ["{workspace}/private"], readonlyPaths: [], readwritePaths: ["{workspace}"] },
    credentials: { envVars: { TOKEN: ["api.example.com"] } },
    network: { allowOutbound: false, allowLocalNetwork: true, allowedHosts: ["example.com"], blockedHosts: ["bad.com"] },
  }), "/work");
  assert.deepEqual(config.userPolicy?.filesystem?.deniedPaths, ["/work/private"]);
  assert.deepEqual(config.credentials, { envVars: { TOKEN: { injectHosts: ["api.example.com"] } } });
  assert.deepEqual(config.userPolicy?.seatbelt, { keychainAccess: false });
  assert.equal(config.userPolicy?.experimental, undefined);
  assert.equal(config.allowBypass, false);
  assert.equal(config.sandboxMcpServers, false);
  assert.equal(config.userPolicy?.network?.allowOutbound, false);
  assert.equal(PermissionPolicy.isGranted(["read:/tmp/foo"], "read", "/tmp/foobar"), false);
  assert.equal(PermissionPolicy.isGranted(["read:/tmp/foo"], "read", "/tmp/foo/bar"), true);
});

test("real SDK recorded-runtime lifecycle and honest model report", { skip: !process.env.DURABLE_RUNTIME_PATH }, async () => {
  const store = DurableStore.createEphemeral();
  const harness = await DurableHarness.openAsync(store, new ExtensionRegistry(), new DurableHarnessOptions());
  try {
    const chat = await harness.rootAsync(new AgentChoices({ extensions: [] }));
    const answer = await chat.submitAsync("Hello");
    assert.equal(typeof answer?.data.content, "string");
    assert(chat.answers.length > 0);
    assert((await chat.contextAsync()).some(event => event.type === "assistant.message"));
    assert.equal(chat.snapshot(harness.agentDocument)?.["extensions"] instanceof Array, true);
    if (chat.effectiveModel.hasModel) assert.notEqual(chat.effectiveModel.source, null);
    else assert.equal(chat.effectiveModel.model, null);
    assert.equal(chat.entries().length, 0);
    const defaultConfig = await harness.buildSessionConfig(new AgentChoices({ reasoningEffort: "low" }), chat);
    assert.equal(defaultConfig.reasoningEffort, undefined);
    const explicitConfig = await harness.buildSessionConfig(new AgentChoices({ model: "replay-model", reasoningEffort: "high" }), chat);
    assert.equal(explicitConfig.reasoningEffort, undefined);
    const listModels = harness.client.rpc.models.list.bind(harness.client.rpc.models);
    harness.client.rpc.models.list = async params => {
      const models = await listModels(params);
      const wire: unknown = models;
      assert(wire !== null && typeof wire === "object" && "list" in wire && Array.isArray(wire.list));
      for (const model of wire.list) model.supportedReasoningEfforts = ["low"];
      return models;
    };
    const rawConfig = await harness.buildSessionConfig(new AgentChoices({
      model: "replay-model", reasoningEffort: "minimal",
    }), chat);
    assert.equal(rawConfig.reasoningEffort, "minimal");
    assert(answer);
    for (const [tolerant, errorMessage, succeeds, emitAnswer] of [
      [false, "postToolUse hook: Operation not permitted", false, true],
      [true, "postToolUse hook: Operation not permitted", true, true],
      [true, "hook timed out", false, true],
      [true, "postToolUse hook: Operation not permitted", true, false],
    ] as const) {
      const errorChat = await harness.createConversationAsync();
      harness.options.tolerateHostHookFailures = tolerant;
      let observer: ((event: SessionEvent) => void) | undefined;
      const subscription = mock.method(errorChat.session, "on", (handler: (event: SessionEvent) => void) => {
        observer = handler;
        return () => { observer = undefined; };
      });
      const send = mock.method(errorChat.session, "send", async () => {
        if (emitAnswer) observer?.(answer);
        observer?.({
          type: "session.error", id: "host-error", timestamp: new Date().toISOString(), parentId: null,
          data: { errorType: "hook", message: errorMessage },
        });
        return "native-message";
      });
      try {
        if (succeeds) assert.equal(await errorChat.submitAsync("denial"), emitAnswer ? answer : undefined);
        else await assert.rejects(errorChat.submitAsync("denial"), new RegExp(errorMessage));
      } finally { send.mock.restore(); subscription.mock.restore(); }
    }
    assert.equal(chat.effectiveModel.model, "replay-model");
    assert.equal(chat.turnModels[0]?.model, null);
  } finally { await harness.disposeAsync(); store.dispose(); }
});

test("fork bookkeeping excludes inherited internal anchors, retains boundary and user entries", async () => {
  const store = DurableStore.createEphemeral();
  const harness = new DurableHarness(new CopilotClient(), store, new ExtensionRegistry());
  try {
    const record = await store.commitAsync(tx => tx.createConversation());
    const parent = harness.findConversation(record.id)!;
    await parent.configureAsync(new AgentChoices({ permissions: ["custom-tool:count"] }));
    const entry = await parent.appendEntryAsync("message");
    const fork = await store.commitAsync(tx => {
      const child = tx.createConversation({ parentConversationId: parent.id, forkEntryId: entry });
      store.storeProvider.copyEntries(parent.id, child.id, entry);
      for (const doc of [...store.documents].filter(d => d.ownerId === parent.id))
        store.storeProvider.copyDocument(doc, child.id, entry);
      return child;
    });
    const child = new DurableConversation(harness, fork);
    const own = await child.appendEntryAsync("fork");
    assert.deepEqual(child.entries(), [own, entry]);
    assert.deepEqual(child.choices().permissions, ["custom-tool:count"]);
    await parent.configureAsync(new AgentChoices({ permissions: ["*"] }));
    assert.deepEqual(child.choices().permissions, ["custom-tool:count"]);
  } finally { await harness.disposeAsync(); store.dispose(); }
});

test("task scheduler and abort cascade commit terminal outcomes child-first", async () => {
  const store = DurableStore.createEphemeral();
  const registry = new ExtensionRegistry();
  const runner = new DurableTaskRunner(store, registry);
  const aborted: string[] = [];
  const definition = DurableTask.define("work", () => ({ phase: "run" }),
    async (_input, _checkpoint, runtime) => runtime.commitAsync(() => TaskCommit.completed(null)),
    async (_input, _checkpoint, runtime) => {
      aborted.push(runtime.task.id);
      return TaskCommit.aborted();
    });
  registry.install(new Extension("work", [], [], [definition]));
  try {
    const [parent, child] = await store.commitAsync(tx => {
      const parent = tx.createTask(definition, null);
      const child = tx.createTask(definition, null, { ownerTaskId: parent.id });
      return [parent, child] as const;
    });
    assert.equal(parent.status, TaskStatus.Pending);
    await runner.abortTaskAsync(parent.id);
    assert.deepEqual(aborted, [child.id, parent.id]);
    assert.equal(parent.status, TaskStatus.Terminal);
    assert.equal(child.status, TaskStatus.Terminal);
    assert.equal((await runner.waitForTaskAsync(child.id)).status, "aborted");
  } finally { await runner.disposeAsync(); store.dispose(); }
});

test("actual SDK tool wire invokes winning override and wrapper", { skip: !process.env.DURABLE_RUNTIME_PATH }, async () => {
  const registry = new ExtensionRegistry();
  const calls: string[] = [];
  registry.install(new Extension("files", [{
    name: "read_file", description: "read", overridesBuiltInTool: true,
    parameters: { type: "object", properties: { path: { type: "string" } }, required: ["path"] },
    handler: (_args, invocation) => { calls.push(`old:${invocation.sessionId}`); return "old"; },
  }]));
  registry.install(new Extension("replacement", [{
    name: "read_file", description: "new", overridesBuiltInTool: true,
    parameters: { type: "object", properties: { path: { type: "string" } }, required: ["path"] },
    handler: (args, invocation) => {
      assert.deepEqual(args, { path: "notes.txt" });
      calls.push(`new:${invocation.sessionId}`);
      return "recorded contents";
    },
  }], [], [], [new ToolWrapper("read_file", tool => ({
    ...tool, handler: async (args, invocation) => {
      calls.push("wrapped"); return await tool.handler!(args, invocation);
    },
  }))]));
  const store = DurableStore.createEphemeral();
  const harness = await DurableHarness.openAsync(store, registry, new DurableHarnessOptions({
    runtimeArguments: ["--fixture", join(process.cwd(), "../../tools/ReplayRuntime/fixtures/tool-round-trip.json")],
  }));
  try {
    const chat = await harness.rootAsync(new AgentChoices({ permissions: ["custom-tool:read_file"] }));
    const reply = await chat.submitAsync("Read notes.txt", 5000);
    assert(reply?.data.content);
    assert.deepEqual(calls, ["wrapped", `new:${chat.sessionId}`]);
    assert(store.documents.some(d => d.kind === "durable.checkpoint" &&
      d.versions.some(v => typeof v.value === "object" && v.value !== null && !Array.isArray(v.value) &&
        v.value["eventId"] === "tool:call-0001")));
  } finally { await harness.disposeAsync(); store.dispose(); }
});
