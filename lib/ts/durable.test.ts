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
  DurableJson, type SessionEvent, type SessionConfig,
} from "./durable.js";
import { CopilotClient, RuntimeConnection } from "@github/copilot-sdk";
import { createServer } from "node:net";
import { createMessageConnection, SocketMessageReader, SocketMessageWriter, ResponseError } from "vscode-jsonrpc/node.js";
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

function knownNativeSessions(client: CopilotClient): void {
  client.getSessionMetadata = async sessionId => ({
    sessionId, startTime: new Date(0), modifiedTime: new Date(0), isRemote: false,
  });
}

async function operationPeer() {
  const methods: string[] = [];
  let held = "", failure = "";
  let entered = barrier(), release = barrier();
  const server = createServer(socket => {
    const connection = createMessageConnection(new SocketMessageReader(socket), new SocketMessageWriter(socket));
    connection.onRequest(async (method, raw) => {
      methods.push(method);
      const params: Record<string, unknown> = { ...raw };
      if (method === held) {
        entered.resolve();
        await release.promise;
        if (failure) throw new ResponseError(-32000, failure);
      }
      if (method === "connect") return { protocolVersion: 3 };
      if (method === "session.create") return { sessionId: params["sessionId"], capabilities: {} };
      if (method === "session.getMessages") return { events: [] };
      if (method === "session.getMetadata") return {};
      if (method === "session.detach") return { success: true };
      if (method === "session.tools.set") return {};
      throw new ResponseError(-32601, `Unexpected operation RPC ${method}`);
    });
    connection.listen();
    socket.on("close", () => connection.dispose());
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert(address && typeof address !== "string");
  const client = new CopilotClient({ connection: RuntimeConnection.forUri(`127.0.0.1:${address.port}`) });
  await client.start();
  return {
    client, methods,
    hold(method: string, error = "") {
      held = method; failure = error; entered = barrier(); release = barrier();
      return entered.promise;
    },
    release() { release.resolve(); },
    async close() {
      release.resolve();
      await client.stop();
      const closed = once(server, "close"); server.close(); await closed;
    },
  };
}

test("round seven: configuration drains the store gate and received public tools RPC", { timeout: 10000 }, async t => {
  for (const stage of ["store", "rpc", "error"] as const) await t.test(stage, async () => {
    const directory = mkdtempSync(join(tmpdir(), "durable-ts-config-drain-"));
    const options = Object.assign(new DurableStoreOptions(), { path: join(directory, "store.jsonl") });
    const store = DurableStore.create(options), peer = await operationPeer();
    const harness = new DurableHarness(peer.client, store, new ExtensionRegistry());
    const chat = await harness.rootAsync();
    const received = peer.hold("session.tools.set", stage === "error" ? "late tools failure" : "");
    const release = barrier(), entered = barrier();
    let holding: Promise<unknown> | undefined, closing: Promise<void> | undefined;
    let stopped = false;
    const stop = peer.client.stop.bind(peer.client);
    peer.client.stop = async () => { stopped = true; return stop(); };
    if (stage === "store") {
      const held = barrier();
      holding = store.commitAsync(async () => { held.resolve(); await release.promise; });
      await held.promise;
      const commit = store.commitAsync.bind(store);
      store.commitAsync = body => { entered.resolve(); return commit(body); };
    }
    const pending = chat.configureAsync(new AgentChoices({ instructions: "accepted" }));
    const result = pending.then(() => null, error => error);
    try {
      await (stage === "store" ? entered.promise : received);
      closing = harness.disposeAsync();
      await new Promise<void>(resolve => setImmediate(resolve));
      assert.equal(stopped, false, "configuration must retain the SDK through persistence and ACK");
      assert.throws(() => DurableStore.create(options), /already locked/);
      const sequence = store.sequence;
      await assert.rejects(chat.configureAsync(new AgentChoices({ instructions: "rejected" })), /closing/);
      assert.equal(store.sequence, sequence, "rejected configuration cannot persist choices");
      release.resolve();
      await holding;
      await received;
      assert.equal(stopped, false, "actual received setTools RPC must settle before stop");
      peer.release();
      const error = await result;
      if (stage === "error") assert.match(String(error), /late tools failure/);
      else assert.equal(error, null);
      await closing;
      assert.equal(chat.choices().instructions, "accepted");
      assert.equal(peer.methods.filter(method => method === "session.tools.set").length, 1);
      const finalSequence = store.sequence, bytes = readFileSync(options.path!);
      await assert.rejects(chat.configureAsync(new AgentChoices({ instructions: "after-close" })), /closing/);
      await new Promise<void>(resolve => setImmediate(resolve));
      assert.equal(store.sequence, finalSequence);
      assert.deepEqual(readFileSync(options.path!), bytes, "no durable writes after disposal");
    } finally {
      release.resolve(); peer.release();
      await Promise.allSettled([pending, holding, closing]);
      await harness.disposeAsync(); store.dispose(); await peer.close();
      rmSync(directory, { recursive: true });
    }
  });
});

test("round seven: concurrent first roots share creation attachment choices and retry", { timeout: 10000 }, async t => {
  for (const failure of [false, true]) await t.test(failure ? "failure-retry" : "store-and-attachment", async () => {
    const store = DurableStore.createEphemeral(), peer = await operationPeer();
    const harness = new DurableHarness(peer.client, store, new ExtensionRegistry());
    const entered = barrier(), release = barrier();
    const holding = store.commitAsync(async () => { entered.resolve(); await release.promise; });
    await entered.promise;
    const received = peer.hold("session.create", failure ? "first root failure" : "");
    const first = harness.rootAsync(new AgentChoices({ instructions: "first" }));
    const second = harness.rootAsync(new AgentChoices({ instructions: "second" }));
    const results = Promise.allSettled([first, second]);
    let closing: Promise<void> | undefined;
    try {
      release.resolve(); await holding; await received;
      assert.equal(store.conversations.length, 1, "both first callers must share the root record");
      const third = harness.rootAsync(new AgentChoices({ instructions: "third" }));
      const thirdResult = third.then(value => ({ value, error: null }), error => ({ value: null, error }));
      await new Promise<void>(resolve => setImmediate(resolve));
      assert.equal(peer.methods.filter(method => method === "session.create").length, 1,
        "root attachment must also be shared while the first SDK RPC is pending");
      if (!failure) {
        closing = harness.disposeAsync();
        await assert.rejects(harness.rootAsync(), /closing/);
      }
      peer.release();
      const outcomes = await results, thirdOutcome = await thirdResult;
      if (failure) {
        assert(outcomes.every(outcome => outcome.status === "rejected" && /first root failure/.test(String(outcome.reason))));
        assert.match(String(thirdOutcome.error), /first root failure/);
        peer.hold("");
        const retry = await harness.rootAsync(new AgentChoices({ instructions: "replacement" }));
        assert.equal(retry.id, store.conversations[0]!.id);
        assert.equal(retry.choices().instructions, "first", "retry preserves the persisted first choice");
        assert.equal(await harness.rootAsync(), retry);
        assert.equal(peer.methods.filter(method => method === "session.create").length, 2);
      } else {
        assert.equal(outcomes[0]!.status, "fulfilled");
        assert.equal(outcomes[1]!.status, "fulfilled");
        if (outcomes[0]!.status !== "fulfilled" || outcomes[1]!.status !== "fulfilled") throw new Error("roots failed");
        assert.equal(outcomes[0]!.value, outcomes[1]!.value);
        assert.equal(thirdOutcome.value, outcomes[0]!.value);
        assert.equal(outcomes[0]!.value.choices().instructions, "first");
        await closing;
      }
      assert.equal(store.conversations.length, 1);
    } finally {
      release.resolve(); peer.release(); await results;
      await harness.disposeAsync(); store.dispose(); await peer.close();
    }
  });
});

test("round seven: admitted task can configure during shutdown without admitting external calls", { timeout: 5000 }, async () => {
  const store = DurableStore.createEphemeral(), peer = await operationPeer();
  const registry = new ExtensionRegistry(), harness = new DurableHarness(peer.client, store, registry);
  const chat = await harness.rootAsync(), entered = barrier(), release = barrier();
  const definition = DurableTask.define("internal-configuration", () => ({}), async (_input, _checkpoint, runtime) => {
    entered.resolve(); await release.promise;
    await chat.configureAsync(new AgentChoices({ instructions: "internal" }));
    return runtime.commitAsync(() => TaskCommit.completed(null));
  });
  registry.install(new Extension("internal-configuration", [], [], [definition]));
  const record = await store.commitAsync(tx => tx.createTask(definition, null));
  const running = harness.runTaskAsync(record.id);
  await entered.promise;
  const closing = harness.disposeAsync();
  try {
    await assert.rejects(chat.configureAsync(new AgentChoices({ instructions: "external" })), /closing/);
    release.resolve();
    await running; await closing;
    assert.equal(chat.choices().instructions, "internal");
    assert.equal(peer.methods.filter(method => method === "session.tools.set").length, 1);
    assert.equal(record.status, TaskStatus.Terminal);
  } finally {
    release.resolve(); await Promise.allSettled([running, closing]);
    store.dispose(); await peer.close();
  }
});

test("review: conversation lifecycle retains SDK and writer through persistence and attachment", { timeout: 30000 }, async t => {
  for (const operation of ["create", "root", "resume-unbound", "resume-existing", "resume-missing", "fork"] as const) {
    for (const stage of ["store", "sdk", "history"] as const) {
      if (stage === "store" && (operation === "resume-existing" || operation === "resume-missing")) continue;
      await t.test(`${operation}/${stage}`, async () => {
        const directory = mkdtempSync(join(tmpdir(), "durable-ts-lifecycle-"));
        const options = Object.assign(new DurableStoreOptions(), { path: join(directory, "store.jsonl") });
        const store = DurableStore.create(options);
        const harness = await DurableHarness.openAsync(store, new ExtensionRegistry());
        const client = harness.client, entered = barrier(), release = barrier();
        let stopped = false, releaseSequence = -1, releaseBytes: Buffer | undefined;
        const stop = client.stop.bind(client);
        client.stop = async () => {
          stopped = true;
          releaseSequence = store.sequence;
          releaseBytes = fs.existsSync(options.path!) ? readFileSync(options.path!) : Buffer.alloc(0);
          return stop();
        };
        let holding: Promise<unknown> | undefined, pending: Promise<DurableConversation> | undefined;
        let closing: Promise<void> | undefined;
        let result: DurableConversation | undefined, failure: unknown;
        try {
          let parent: DurableConversation | undefined, id = "";
          if (operation === "fork") {
            // ReplayRuntime has no native fork fixture. Only the fork RPC response is
            // adapted; attachment and history still use real public SDK sessions.
            const child = await client.createSession({});
            client.rpc.sessions.fork = async () => ({ sessionId: child.sessionId });
          }
          if (operation === "fork" || operation === "resume-existing" || operation === "resume-missing") {
            parent = await harness.createConversationAsync();
            id = parent.id;
            if (operation !== "fork") parent.detach();
            if (operation === "resume-missing") client.getSessionMetadata = async () => undefined;
          } else if (operation === "resume-unbound") {
            id = await store.commitAsync(tx => tx.createConversation().id);
          }
          if (stage === "store") {
            const held = barrier();
            holding = store.commitAsync(async () => { held.resolve(); await release.promise; });
            await held.promise;
            const commit = store.commitAsync.bind(store);
            store.commitAsync = body => { entered.resolve(); return commit(body); };
          }
          {
            const create = client.createSession.bind(client), resume = client.resumeSession.bind(client);
            const intercept = async (session: Awaited<ReturnType<typeof create>>) => {
              const history = session.getEvents.bind(session);
              session.getEvents = async () => {
                if (stage === "history") { entered.resolve(); await release.promise; }
                assert.equal(stopped, false, "session history must finish before SDK stop");
                return history();
              };
              return session;
            };
            client.createSession = async config => {
              if (stage === "sdk") { entered.resolve(); await release.promise; }
              assert.equal(stopped, false, "session create must not run after SDK stop");
              return intercept(await create(config));
            };
            client.resumeSession = async (sessionId, config) => {
              if (stage === "sdk") { entered.resolve(); await release.promise; }
              assert.equal(stopped, false, "session resume must not run after SDK stop");
              return intercept(await resume(sessionId, config));
            };
            if (operation === "fork" && stage === "sdk") {
              const fork = client.rpc.sessions.fork.bind(client.rpc.sessions);
              client.rpc.sessions.fork = async params => {
                entered.resolve(); await release.promise;
                assert.equal(stopped, false, "native fork must not run after SDK stop");
                return fork(params);
              };
            }
          }
          pending = operation === "create" ? harness.createConversationAsync() :
            operation === "root" ? harness.rootAsync() :
            operation === "fork" ? harness.forkAsync(parent!, store.entries(parent!.id).at(-1)!) :
            harness.resumeConversationAsync(id);
          const settled = pending.then(value => { result = value; }, error => { failure = error; });
          await entered.promise;
          closing = harness.disposeAsync();
          await new Promise<void>(resolve => setImmediate(resolve));
          assert.equal(stopped, false, "accepted lifecycle must retain SDK while held");
          assert.throws(() => DurableStore.create(options), /writer|already open|locked/i);
          await assert.rejects(harness.resumeConversationAsync(id), /closing/);
          await assert.rejects(harness.createConversationAsync(), /closing/);
          if (parent) await assert.rejects(harness.forkAsync(parent, store.entries(parent.id).at(-1)!), /closing/);
          release.resolve();
          await holding;
          await settled;
          assert.equal(failure, undefined, "accepted lifecycle must settle successfully, not reject after writing");
          assert.ok(result);
          assert.ok(result.snapshot(new DocumentDefinition<import("./durable.js").JsonObject>("durable.session-binding"))?.["sessionId"],
            "successful lifecycle must have a persisted SDK binding even after shutdown detaches handles");
          await closing;
          assert.equal(result.sessionId, null, "shutdown must detach every accepted session");
          assert.equal(store.sequence, releaseSequence, "all lifecycle writes must precede SDK release");
          assert.deepEqual(readFileSync(options.path!), releaseBytes, "no post-close durable bytes");
          assert.throws(() => DurableStore.create(options), /writer|already open|locked/i);
        } finally {
          release.resolve();
          await Promise.allSettled([holding, pending, closing].filter(p => p !== undefined));
          await harness.disposeAsync();
          t.diagnostic(JSON.stringify({ operation, stage, stopped, releaseSequence, sequence: store.sequence,
            failure: failure ? String(failure) : null }));
          store.dispose();
          rmSync(directory, { recursive: true });
        }
      });
    }
  }
});

test("review: closing rejects external attached conversation admission", async () => {
  const store = DurableStore.createEphemeral(), harness = await DurableHarness.openAsync(store, new ExtensionRegistry());
  const chat = await harness.rootAsync(), entered = barrier(), release = barrier();
  const create = harness.client.createSession.bind(harness.client);
  harness.client.createSession = async config => { entered.resolve(); await release.promise; return create(config); };
  const pending = harness.createConversationAsync();
  let closing: Promise<void> | undefined;
  try {
    await entered.promise;
    closing = harness.disposeAsync();
    const sequence = store.sequence;
    await assert.rejects(harness.resumeConversationAsync(chat.id), /closing/,
      "external resume must reject even when an SDK session is already attached");
    await assert.rejects(harness.rootAsync(), /closing/);
    assert.equal(store.sequence, sequence, "rejected admission must not write");
  } finally {
    release.resolve();
    await Promise.allSettled([pending, closing].filter(p => p !== undefined));
    await harness.disposeAsync();
    store.dispose();
  }
});

test("review: lifecycle rollback and SDK errors retain identity and drain cleanup", async () => {
  for (const operation of ["create", "resume", "fork"] as const) {
    for (const stage of ["store", "sdk"] as const) {
      const directory = mkdtempSync(join(tmpdir(), "durable-ts-lifecycle-error-"));
      const options = Object.assign(new DurableStoreOptions(), { path: join(directory, "store.jsonl") });
      const store = DurableStore.create(options), harness = await DurableHarness.openAsync(store, new ExtensionRegistry());
      const entered = barrier(), release = barrier(), cleanup = barrier();
      const original = new Error(`${operation}/${stage} failed`);
      let stopped = false, cleaned = false;
      const stop = harness.client.stop.bind(harness.client);
      harness.client.stop = async () => { stopped = true; return stop(); };
      let pending: Promise<unknown> | undefined, closing: Promise<void> | undefined;
      try {
        const parent = await harness.createConversationAsync();
        const id = await store.commitAsync(tx => tx.createConversation().id);
        const before = store.sequence, count = store.conversations.length;
        if (stage === "store") {
          const commit = store.commitAsync.bind(store);
          store.commitAsync = body => commit(async tx => {
            await body(tx);
            entered.resolve(); await release.promise;
            throw original;
          });
        } else {
          const fail = async () => {
            entered.resolve();
            try { await release.promise; throw original; }
            finally { await cleanup.promise; cleaned = true; }
          };
          harness.client.createSession = fail;
          harness.client.rpc.sessions.fork = fail;
        }
        pending = assert.rejects(operation === "create" ? harness.createConversationAsync() :
          operation === "resume" ? harness.resumeConversationAsync(id) :
          harness.forkAsync(parent, store.entries(parent.id).at(-1)!), error => error === original);
        await entered.promise;
        closing = harness.disposeAsync();
        assert.strictEqual(harness.disposeAsync(), closing, "concurrent closers must share lifecycle drain");
        await new Promise<void>(resolve => setImmediate(resolve));
        assert.equal(stopped, false);
        release.resolve();
        if (stage === "sdk") {
          await new Promise<void>(resolve => setImmediate(resolve));
          assert.equal(stopped, false, "operation exception cleanup remains shutdown-owned");
        }
        cleanup.resolve();
        await Promise.all([pending, closing]);
        if (stage === "store") {
          assert.equal(store.sequence, before, "failed lifecycle transaction must roll back sequence");
          assert.equal(store.conversations.length, count, "failed lifecycle must not leave a partial conversation");
        } else {
          assert.equal(cleaned, true);
          if (operation === "fork") {
            const child = store.conversations.at(-1)!;
            assert.equal(harness.findConversation(child.id)!.snapshot(
              new DocumentDefinition<import("./durable.js").JsonObject>("durable.fork"))?.["state"], 0,
            "failed SDK fork must remain explicitly recoverable, not falsely completed");
          }
        }
        const bytes = readFileSync(options.path!);
        await assert.rejects(harness.resumeConversationAsync(id), /closing/);
        assert.deepEqual(readFileSync(options.path!), bytes);
      } finally {
        release.resolve(); cleanup.resolve();
        await Promise.allSettled([pending, closing].filter(p => p !== undefined));
        await harness.disposeAsync();
        store.dispose(); rmSync(directory, { recursive: true });
      }
    }
  }
});

test("review: draining task may finish dependent conversation lifecycle without external admission", async () => {
  const store = DurableStore.createEphemeral(), registry = new ExtensionRegistry();
  const harness = await DurableHarness.openAsync(store, registry);
  const entered = barrier(), release = barrier();
  let stopped = false, completed = false;
  const stop = harness.client.stop.bind(harness.client);
  harness.client.stop = async () => {
    assert.equal(completed, true, "parent's dependent conversation must settle before SDK release");
    stopped = true;
    return stop();
  };
  const definition = DurableTask.define("conversation-parent", () => ({}), async (_input, _checkpoint, runtime) => {
    entered.resolve(); await release.promise;
    const child = await harness.createConversationAsync(new AgentChoices(), runtime.task.id);
    assert.ok(child.sessionId);
    assert.strictEqual(await harness.resumeConversationAsync(child.id), child);
    assert.equal(stopped, false);
    completed = true;
    return runtime.commitAsync(() => TaskCommit.completed(child.id));
  });
  registry.install(new Extension("conversation-parent", [], [], [definition]));
  let closing: Promise<void> | undefined;
  try {
    const task = await harness.createTaskAsync(definition, null);
    await entered.promise;
    closing = harness.disposeAsync();
    await assert.rejects(harness.createConversationAsync(), /closing/);
    release.resolve();
    await closing;
    assert.equal(store.task(task.id)?.status, TaskStatus.Terminal);
    assert.equal(store.conversations.length, 1);
    assert.equal(store.conversations[0]!.ownerTaskId, task.id);
  } finally { release.resolve(); await closing; await harness.disposeAsync(); store.dispose(); }
});

test("own reserved JSON keys survive cloning, document save and persistent recovery", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-own-json-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "store.jsonl") });
  let store = DurableStore.create(options);
  const definition = new DocumentDefinition<import("./durable.js").JsonObject>("reserved");
  const value = { ["__proto__"]: { conversationId: "child", reported: "0" }, constructor: "ordinary" };
  try {
    for (const clone of [DurableJson.toElement(value), DurableJson.fromElement<typeof value>(value), DurableJson.clone(value)]) {
      assert.deepEqual(clone, value);
      assert.equal(Object.getPrototypeOf(clone), Object.prototype);
      assert.equal(Object.hasOwn(clone!, "__proto__"), true);
    }
    const owner = await store.commitAsync(tx => {
      const owner = tx.createConversation();
      const draft = tx.doc(definition, owner.id);
      draft.value = value;
      draft.save(tx.appendEntry(owner.id, "reserved"));
      return owner.id;
    });
    assert.deepEqual(store.document(definition.kind, owner)!.latest, value);
    store.dispose();
    store = DurableStore.create(options);
    const recovered = DurableJson.fromElement<typeof value>(store.document(definition.kind, owner)!.latest);
    assert.deepEqual(recovered, value);
    assert.equal(Object.getPrototypeOf(recovered), Object.prototype);
    assert.equal(recovered!["__proto__"].conversationId, "child");
  } finally { store.dispose(); rmSync(dir, { recursive: true }); }
});

test("Auto preserves malformed legacy and unrelated JSON instead of truncating them", () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-identify-"));
  const file = join(dir, "store.jsonl");
  try {
    for (const bytes of [
      Buffer.from('{"sequence":4,"conversations":[{"id":"important"}]'),
      Buffer.from('{"unknown":'), Buffer.from('{"unrelated":"valid"}'),
      Buffer.from("not a store"), Buffer.from('{"conversations":[]}', "utf16le"),
      Buffer.from('{"v":1,"seq":1,"type":"conversation","payload":{"id":"bad"}}\nnot json\n'),
      Buffer.concat([Buffer.from('{"v":1,"seq":1,"type":"conversation","payload":{"id":"'),
        Buffer.from([0xff]), Buffer.from('"}}\n')]),
    ]) {
      writeFileSync(file, bytes);
      assert.throws(() => DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file })));
      assert.deepEqual(readFileSync(file), bytes);
    }
    const tail = Buffer.concat([Buffer.from('{"v":1,"seq":2,"type":"conversation","payload":{"id":"'), Buffer.from([0xe9, 0x9b])]);
    writeFileSync(file, tail);
    const store = DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file }));
    try { assert.equal(readFileSync(file).length, 0); } finally { store.dispose(); }
  } finally { rmSync(dir, { recursive: true }); }
});

test("missing pinned native session is recreated under its original durable id after reopen", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-pinned-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "store.jsonl") });
  let store = DurableStore.create(options);
  const client = new CopilotClient();
  client.stop = async () => [];
  client.createSession = async () => { throw new Error("native create interrupted"); };
  let harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    await assert.rejects(harness.createConversationAsync(), /native create interrupted/);
    const id = store.conversations[0]!.id;
    const pinned = harness.findConversation(id)!.binding()!["sessionId"];
    assert.equal(typeof pinned, "string");
    await harness.disposeAsync();
    store.dispose();
    store = DurableStore.create(options);
    const probe = mock.method(client, "getSessionMetadata", async () => undefined);
    const create = mock.method(client, "createSession", async (config?: SessionConfig) => sdkSession(config!.sessionId!).sdk);
    const resume = mock.method(client, "resumeSession", async () => { throw new Error("must not resume missing"); });
    harness = new DurableHarness(client, store, new ExtensionRegistry());
    const chat = await harness.resumeConversationAsync(id);
    assert.equal(chat.sessionId, pinned);
    assert.deepEqual(probe.mock.calls[0]!.arguments, [pinned]);
    assert.equal(create.mock.calls[0]!.arguments[0]!.sessionId, pinned);
    assert.equal(resume.mock.callCount(), 0);
  } finally { await harness.disposeAsync(); store.dispose(); rmSync(dir, { recursive: true }); }
});

test("official SDK transport probes existence and preserves recovery guards and probe errors", async () => {
  const calls: Array<{ method: string; params: Record<string, unknown> }> = [];
  let present = false, probeError = false, wrongId = false;
  const sessions = new Map<string, object[]>();
  const server = createServer(socket => {
    const connection = createMessageConnection(new SocketMessageReader(socket), new SocketMessageWriter(socket));
    connection.onRequest((method, raw) => {
      const params: Record<string, unknown> = { ...raw };
      calls.push({ method, params });
      if (method === "connect") return { protocolVersion: 3 };
      if (method === "session.getMetadata") {
        if (probeError) throw new ResponseError(-32000, "metadata unavailable");
        return { session: present ? {
          sessionId: params["sessionId"], startTime: "2026-01-01T00:00:00Z",
          modifiedTime: "2026-01-01T00:00:00Z", isRemote: false,
        } : null };
      }
      if (method === "session.create") {
        sessions.set(String(params["sessionId"]), []);
        return { sessionId: wrongId ? "unexpected-id" : params["sessionId"], capabilities: {} };
      }
      if (method === "session.resume") return { sessionId: params["sessionId"], capabilities: {} };
      if (method === "session.getMessages") return { events: sessions.get(String(params["sessionId"])) ?? [] };
      if (method === "session.detach") return { success: true };
      throw new ResponseError(-32601, `Unexpected test RPC ${method}`);
    });
    connection.listen();
    socket.on("close", () => connection.dispose());
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert(address && typeof address !== "string");
  const client = new CopilotClient({ connection: RuntimeConnection.forUri(`127.0.0.1:${address.port}`) });
  const store = DurableStore.createEphemeral();
  let harness: DurableHarness | undefined;
  try {
    await client.start();
    harness = new DurableHarness(client, store, new ExtensionRegistry());
    const binding = new DocumentDefinition<import("./durable.js").JsonObject>("durable.session-binding");
    const admission = new DocumentDefinition<import("./durable.js").JsonObject>("durable.admission");
    const fork = new DocumentDefinition<import("./durable.js").JsonObject>("durable.fork");
    for (const shape of ["pinned", "allocated", "fork", "unsettled", "probe-error", "wrong-id", "existing"] as const) {
      const id = await store.commitAsync(tx => {
        const record = tx.createConversation();
        const draft = tx.doc(binding, record.id);
        draft.value = { sessionId: `native-${shape}`, pinned: shape !== "allocated" };
        draft.save(tx.appendEntry(record.id, "session-binding"));
        if (shape === "fork") {
          const intent = tx.doc(fork, record.id);
          intent.value = { state: 1, sessionId: `native-${shape}` };
          intent.save(tx.appendEntry(record.id, "fork"));
        }
        if (shape === "unsettled") {
          const draft = tx.doc(admission, record.id);
          draft.value = { state: "uncertain" };
          draft.save(tx.appendEntry(record.id, "admission"));
        }
        return record.id;
      });
      present = shape === "existing";
      probeError = shape === "probe-error";
      wrongId = shape === "wrong-id";
      calls.length = 0;
      if (shape === "pinned" || shape === "existing") {
        const chat = await harness.resumeConversationAsync(id);
        assert.equal(chat.sessionId, `native-${shape}`);
        assert.deepEqual(calls.map(c => c.method), [
          "session.getMetadata", shape === "existing" ? "session.resume" : "session.create", "session.getMessages",
        ]);
        if (shape === "pinned") assert.equal(calls[1]!.params["sessionId"], "native-pinned");
      } else {
        await assert.rejects(harness.resumeConversationAsync(id),
          shape === "probe-error" ? /metadata unavailable/ :
            shape === "wrong-id" ? /session.create returned sessionId unexpected-id but the caller requested native-wrong-id/ :
              /recovery|unsafe/);
        if (shape !== "wrong-id") assert.equal(calls.some(c => c.method === "session.create"), false);
        if (shape === "unsettled") assert.deepEqual(calls, []);
      }
    }
  } finally {
    try { if (harness) await harness.disposeAsync(); else await client.stop(); }
    finally {
      store.dispose();
      const closed = once(server, "close");
      server.close();
      await closed;
    }
  }
});

test("active phases retain native sessions and final checkpoints until every closer drains", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-active-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "store.jsonl") });
  const store = DurableStore.create(options);
  const client = new CopilotClient();
  const session = sdkSession("active");
  client.createSession = async () => session.sdk;
  let stops = 0;
  const stopped = barrier();
  client.stop = async () => { stops++; stopped.resolve(); return []; };
  const registry = new ExtensionRegistry();
  const harness = new DurableHarness(client, store, registry);
  const entered = barrier(), release = barrier(), finalEntered = barrier(), finalRelease = barrier(), gateEntered = barrier();
  let running: Promise<unknown> | undefined, first: Promise<unknown> | undefined, second: Promise<unknown> | undefined, held: Promise<void> | undefined;
  try {
    const chat = await harness.createConversationAsync();
    session.sdk.send = async () => {
      session.emit(sdkEvent("tool.execution_complete", "phase-tool", {
        toolCallId: "phase", success: true, result: { content: "submitted" },
      }));
      session.emit(sdkEvent("session.idle", "idle", {}));
      return "sent";
    };
    const definition = DurableTask.define("active-session", () => ({ phase: "run" }), async (_input, _checkpoint, runtime) => {
      entered.resolve();
      await release.promise;
      await chat.contextAsync();
      await chat.submitAsync("during shutdown");
      const commit = await runtime.commitAsync(() => TaskCommit.completed("phase saved"));
      held = store.commitAsync(async () => { gateEntered.resolve(); await finalRelease.promise; });
      await gateEntered.promise;
      session.emit(sdkEvent("tool.execution_complete", "late-tool", {
        toolCallId: "late", success: true, result: { content: "final" },
      }));
      return commit;
    });
    registry.install(new Extension("active-session", [], [], [definition]));
    const task = await store.commitAsync(tx => tx.createTask(definition, null));
    const checkpoint = chat.checkpointBarrierAsync.bind(chat);
    chat.checkpointBarrierAsync = async () => {
      if (!chat.sessionId) {
        finalEntered.resolve();
      }
      await checkpoint();
    };
    running = harness.runTaskAsync(task.id);
    await entered.promise;
    first = harness.disposeAsync();
    second = harness.disposeAsync();
    release.resolve();
    const outcome = await running;
    assert.equal((outcome as { status: string }).status, "completed");
    assert.equal(await Promise.race([
      finalEntered.promise.then(() => "barrier"), stopped.promise.then(() => "stop"),
    ]), "barrier");
    assert.equal(chat.checkpoint()!["content"], "submitted");
    assert.equal(stops, 0);
    assert.throws(() => DurableStore.create(options), /lock/);
    assert.throws(() => new DurableTaskRunner(store, registry), /runner|runtime/);
    await assert.rejects(harness.runTaskAsync("new-task"), /closing/);
    await assert.rejects(harness.createConversationAsync(), /closing/);
    finalRelease.resolve();
    await Promise.all([first, second, held]);
    assert.equal(stops, 1);
    store.dispose();
    const reopened = DurableStore.create(options);
    try {
      const saved = reopened.document("durable.checkpoint", chat.id)!.latest;
      assert(saved !== null && typeof saved === "object" && !Array.isArray(saved));
      assert.equal(saved["content"], "final");
    }
    finally { reopened.dispose(); }
  } finally {
    release.resolve(); finalRelease.resolve();
    await Promise.allSettled([running, first, second, held]);
    await harness.disposeAsync(); store.dispose(); rmSync(dir, { recursive: true });
  }
});

test("concurrent direct task-runner disposal joins the active drain", async () => {
  const store = DurableStore.createEphemeral();
  const registry = new ExtensionRegistry();
  const entered = barrier(), release = barrier();
  const definition = DurableTask.define("join-close", () => ({ phase: "run" }), async (_input, _checkpoint, runtime) => {
    entered.resolve();
    await release.promise;
    return runtime.commitAsync(() => TaskCommit.completed(null));
  });
  registry.install(new Extension("join-close", [], [], [definition]));
  const runner = new DurableTaskRunner(store, registry);
  const task = await store.commitAsync(tx => tx.createTask(definition, null));
  const running = runner.runTaskAsync(task.id);
  await entered.promise;
  const first = runner.disposeAsync();
  let secondDone = false;
  const second = runner.disposeAsync().then(() => { secondDone = true; });
  try {
    await Promise.resolve();
    assert.equal(secondDone, false);
  } finally {
    release.resolve();
    await Promise.all([running, first, second]);
    store.dispose();
  }
});

test("repeated checkpoint drains preserve one original storage failure", async () => {
  const store = DurableStore.createEphemeral();
  const session = sdkSession("checkpoint-failure");
  const client = new CopilotClient();
  client.createSession = async () => session.sdk;
  client.stop = async () => [];
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  const original = new Error("checkpoint write failed");
  try {
    const chat = await harness.createConversationAsync();
    const injection = mock.method(store, "commitAsync", async () => { throw original; });
    session.emit(sdkEvent("tool.execution_complete", "failed-tool", { toolCallId: "failed", success: true }));
    try {
      await assert.rejects(harness.disposeAsync(), error => {
        assert(error instanceof Error && !(error instanceof AggregateError));
        assert.equal(error.message, `Conversation '${chat.id}' failed to persist a tool checkpoint.`);
        assert.equal(error.cause, original);
        return true;
      });
    } finally { injection.mock.restore(); }
  } finally { store.dispose(); }
});

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
    knownNativeSessions(client);
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
    knownNativeSessions(client);
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
      if (order.includes("barrier")) return;
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
    const failure = new Error("checkpoint failed");
    harness.findConversation(record.id)!.checkpointBarrierAsync = async () => { throw failure; };
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

test("review: invalid sandbox cannot poison root", async () => {
  const store = DurableStore.createEphemeral(), client = new CopilotClient();
  client.createSession = async config => sdkSession(config!.sessionId!).sdk;
  client.stop = async () => [];
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    await assert.rejects(harness.rootAsync(new AgentChoices({
      sandbox: new DurableSandboxPolicy({ enabled: false, filesystem: { deniedPaths: ["/private"] } }),
    })), /disabled sandbox/);
    assert.equal(store.conversations.length, 0, "rejected sandbox must leave no durable root");
    const root = await harness.rootAsync(new AgentChoices({ model: "corrected" }));
    assert.equal(root.choices().model, "corrected");
  } finally { await harness.disposeAsync(); store.dispose(); }
});

test("review: initial choices commit atomically before a later binding failure", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-initial-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "store.jsonl") });
  let store = DurableStore.create(options);
  const client = new CopilotClient();
  client.createSession = async config => sdkSession(config!.sessionId!).sdk;
  client.stop = async () => [];
  let harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    const choices = new AgentChoices({ model: "original", extensions: [], sandbox: new DurableSandboxPolicy({ enabled: true }) });
    const hook: NonNullable<SessionConfig["hooks"]> = { onUserPromptSubmitted: async () => ({ additionalContext: "local callback" }) };
    choices.hooks = hook;
    const original = fs.fsyncSync;
    let first = true;
    const firstFault = mock.method(fs, "fsyncSync", (fd: number) => {
      if (first) { first = false; throw new Error("creation commit failed"); }
      original(fd);
    });
    syncBuiltinESMExports();
    try { await assert.rejects(harness.rootAsync(choices), /creation commit failed/); }
    finally { firstFault.mock.restore(); syncBuiltinESMExports(); }
    assert.equal(store.conversations.length, 0, "failed initial commit must roll back conversation and choices together");
    assert.equal(store.documents.length, 0);
    let commits = 0;
    const fault = mock.method(fs, "fsyncSync", (fd: number) => {
      if (++commits === 2) throw new Error("binding commit failed");
      original(fd);
    });
    syncBuiltinESMExports();
    try { await assert.rejects(harness.rootAsync(choices), /binding commit failed/); }
    finally { fault.mock.restore(); syncBuiltinESMExports(); }
    const id = store.conversations[0]!.id;
    assert.equal(harness.findConversation(id)!.choices().model, "original", "first commit must contain original choices");
    assert.equal(harness.findConversation(id)!.choices().hooks, hook);
    assert.equal(readFileSync(options.path!, "utf8").includes("local callback"), false, "callbacks cannot be durable JSON");
    await harness.disposeAsync(); store.dispose();
    store = DurableStore.create(options);
    harness = new DurableHarness(client, store, new ExtensionRegistry());
    const root = await harness.rootAsync(new AgentChoices({ model: "replacement" }));
    assert.equal(root.id, id);
    assert.equal(root.choices().model, "original");
    assert.deepEqual(root.choices().extensions, []);
    assert.deepEqual(root.choices().sandbox, { enabled: true });
  } finally { await harness.disposeAsync(); store.dispose(); rmSync(dir, { recursive: true }); }
});

test("review: official public SDK send keeps idle and RPC success under one admission", { timeout: 5000 }, async () => {
  let release = barrier(), sent = barrier();
  let rejectSend = false;
  let notify: (sessionId: string, event: SessionEvent) => Promise<void> = async () => { throw new Error("not connected"); };
  const server = createServer(socket => {
    const connection = createMessageConnection(new SocketMessageReader(socket), new SocketMessageWriter(socket));
    notify = (sessionId, event) => connection.sendNotification("session.event", { sessionId, event });
    connection.onRequest(async (method, raw) => {
      const params: Record<string, unknown> = { ...raw };
      if (method === "connect") return { protocolVersion: 3 };
      if (method === "session.create") return { sessionId: params["sessionId"], capabilities: {} };
      if (method === "session.getMessages") return { events: [] };
      if (method === "session.detach") return { success: true };
      if (method === "session.send") {
        sent.resolve();
        await release.promise;
        if (rejectSend) throw new ResponseError(-32000, "late public RPC failure");
        return { messageId: "public-message" };
      }
      throw new ResponseError(-32601, `Unexpected review RPC ${method}`);
    });
    connection.listen();
    socket.on("close", () => connection.dispose());
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert(address && typeof address !== "string");
  const client = new CopilotClient({ connection: RuntimeConnection.forUri(`127.0.0.1:${address.port}`) });
  const store = DurableStore.createEphemeral();
  await client.start();
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  try {
    for (const mode of ["idle-first", "send-first", "late-error", "deadline"] as const) {
      release = barrier(); sent = barrier(); rejectSend = mode === "late-error";
      const chat = await harness.createConversationAsync();
      const idle = barrier();
      const stop = chat.session.on(event => { if (event.type === "session.idle") idle.resolve(); });
      const result = chat.submitAsync(mode, mode === "deadline" ? 100 : 1000)
        .then(answer => ({ answer, error: null }), error => ({ answer: undefined, error }));
      await sent.promise;
      if (mode === "send-first") release.resolve();
      await notify(chat.sessionId!, sdkEvent("assistant.message", `${mode}-answer`, { content: mode }));
      await notify(chat.sessionId!, sdkEvent("session.idle", `${mode}-idle`, {}));
      await idle.promise;
      if (mode !== "send-first") assert.equal(chat.admission()?.["state"], "admitted");
      if (mode !== "deadline") release.resolve();
      const outcome = await result;
      if (mode === "late-error") assert.match(String(outcome.error), /late public RPC failure/);
      else if (mode === "deadline") assert.match(String(outcome.error), /timed out/);
      else {
        assert.equal(outcome.error, null);
        assert.equal(outcome.answer?.data.content, mode);
      }
      assert.equal(chat.admission()?.["state"], ["late-error", "deadline"].includes(mode) ? "uncertain" : "completed");
      release.resolve(); stop();
    }
  } finally {
    release.resolve();
    await harness.disposeAsync(); store.dispose();
    const closed = once(server, "close"); server.close(); await closed;
  }
});

test("review: accepted task creation behind the store gate belongs to shutdown", { timeout: 5000 }, async t => {
  for (const direct of [true, false]) await t.test(direct ? "runner" : "harness", async () => {
    const dir = mkdtempSync(join(tmpdir(), "durable-ts-create-close-"));
    const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "store.jsonl") });
    const store = DurableStore.create(options), registry = new ExtensionRegistry();
    const client = new CopilotClient();
    const harness = direct ? undefined : new DurableHarness(client, store, registry);
    const runner = harness?.runner ?? new DurableTaskRunner(store, registry);
    const held = barrier(), release = barrier(), phaseEntered = barrier(), phaseRelease = barrier();
    let released = false, phaseStarted = false;
    const definition = DurableTask.define("accepted", () => ({}), async (_input, _checkpoint, runtime) => {
      phaseStarted = true;
      phaseEntered.resolve();
      await phaseRelease.promise;
      assert.equal(released, false, "accepted task checkpoint must precede resource release");
      return runtime.commitAsync(() => TaskCommit.completed("saved"));
    });
    registry.install(new Extension("accepted", [], [], [definition]));
    const holding = store.commitAsync(async () => { held.resolve(); await release.promise; });
    await held.promise;
    const creating = (harness ?? runner).createTaskAsync(definition, null);
    const creationResult = creating.then(record => ({ record }), error => ({ error }));
    const finalize = async () => {
      released = true;
      assert.equal(store.tasks.length, 1, "resource release must include the accepted creation");
      assert.equal(store.tasks[0]!.status, TaskStatus.Terminal, "resource release must follow the final checkpoint");
      assert.deepEqual(store.tasks[0]!.outcome, { status: "completed", result: "saved" });
    };
    client.stop = async () => { await finalize(); return []; };
    const closing = harness ? harness.disposeAsync() : runner.disposeAsync(finalize);
    const closeResult = closing.then(() => null, error => error);
    try {
      await new Promise<void>(resolve => setImmediate(resolve));
      assert.equal(released, false, "close must retain resources while accepted creation awaits the store gate");
      assert.equal(store.tasks.length, 0, "creation is held at the real transaction gate");
      assert.throws(() => DurableStore.create(options), /already locked/);
      assert.throws(() => new DurableTaskRunner(store, registry), /already driven/);
      await assert.rejects((harness ?? runner).createTaskAsync(definition, null), /closing/);
      release.resolve();
      await holding;
      const result = await creationResult;
      assert.ok("record" in result, "accepted creation must not fail scheduling after its commit");
      await Promise.race([phaseEntered.promise, closeResult]);
      assert.equal(phaseStarted, true, "accepted creation must be scheduled even after close starts");
      assert.equal(released, false, "close must also drain the newly scheduled phase");
      phaseRelease.resolve();
      const record = result.record;
      await closing;
      assert.equal(store.task(record.id)!.status, TaskStatus.Terminal);
      const bytes = readFileSync(options.path!);
      const sequence = store.sequence;
      await assert.rejects(runner.createTaskAsync(definition, null), /closing/);
      assert.deepEqual(readFileSync(options.path!), bytes, "rejected post-close creation must not write");
      assert.equal(store.sequence, sequence);
      assert.throws(() => DurableStore.create(options), /already locked/);
      const replacement = new DurableTaskRunner(store, registry);
      await replacement.disposeAsync();
    } finally {
      release.resolve(); phaseRelease.resolve();
      await holding;
      const result = await creationResult;
      await closeResult;
      t.diagnostic(JSON.stringify({ direct, released, creation: "record" in result ? "success" : String(result.error),
        tasks: store.tasks.map(task => ({ id: task.id, status: task.status, outcome: task.outcome })) }));
      store.dispose();
      rmSync(dir, { recursive: true });
    }
  });
});

test("review: failed accepted creation releases its shutdown drain without durable state", async () => {
  const store = DurableStore.createEphemeral(), runner = new DurableTaskRunner(store, new ExtensionRegistry());
  const held = barrier(), release = barrier();
  const original = new Error("initial checkpoint failed");
  const definition = DurableTask.define("fails-before-task", () => { throw original; }, async () => TaskCommit.completed(null));
  const holding = store.commitAsync(async () => { held.resolve(); await release.promise; });
  await held.promise;
  const creating = assert.rejects(runner.createTaskAsync(definition, null), error => error === original);
  let released = false;
  const closing = runner.disposeAsync(async () => { released = true; });
  try {
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.equal(released, false, "close must enroll failed creations too");
    release.resolve();
    await Promise.all([holding, creating, closing]);
    assert.deepEqual(store.tasks, [], "failed creation must not persist a task");
    assert.equal(store.sequence, 0, "creation rollback must preserve the write sequence");
    assert.deepEqual(store.documents, []);
  } finally { release.resolve(); await Promise.allSettled([holding, creating, closing]); store.dispose(); }
});

test("review: draining parent's public creation retains ownership without admitting external owners", { timeout: 2000 }, async () => {
  const store = DurableStore.createEphemeral(), registry = new ExtensionRegistry();
  const runner = new DurableTaskRunner(store, registry), entered = barrier(), release = barrier();
  const child = DurableTask.define("public-child", () => ({}), async (_input, _checkpoint, runtime) =>
    runtime.commitAsync(() => TaskCommit.completed("child")));
  const parent = DurableTask.define("public-parent", () => ({}), async (_input, _checkpoint, runtime) => {
    entered.resolve();
    await release.promise;
    const record = await runner.createTaskAsync(child, null, { ownerTaskId: runtime.task.id });
    assert.equal((await runner.waitForTaskAsync(record.id)).result, "child");
    return runtime.commitAsync(() => TaskCommit.completed("parent"));
  });
  registry.install(new Extension("public", [], [], [parent, child]));
  const record = await runner.createTaskAsync(parent, null);
  await entered.promise;
  const closing = runner.disposeAsync();
  try {
    await assert.rejects(runner.createTaskAsync(child, null, { ownerTaskId: record.id }), /closing/);
    release.resolve();
    await closing;
    assert.equal(store.tasks.length, 2);
    assert.ok(store.tasks.every(task => task.status === TaskStatus.Terminal));
    assert.deepEqual(store.tasks.map(task => task.outcome), [
      { status: "completed", result: "parent" }, { status: "completed", result: "child" },
    ]);
  } finally { release.resolve(); await closing; store.dispose(); }
});

test("review: draining parent can commit and schedule a dependent child but external work is rejected", { timeout: 2000 }, async () => {
  const store = DurableStore.createEphemeral();
  const registry = new ExtensionRegistry();
  const runner = new DurableTaskRunner(store, registry);
  const entered = barrier(), release = barrier();
  const child = DurableTask.define("drain-child", () => ({}), async (_input, _checkpoint, runtime) =>
    runtime.commitAsync(() => TaskCommit.completed("child saved")));
  const parent = DurableTask.definePhases("drain-parent", () => ({ phase: "run" }), new Map<string,
    import("./durable.js").PhaseHandler<import("./durable.js").JsonValue, { phase: string }>>([
    ["run", async (_input, _checkpoint, runtime) => {
    entered.resolve(); await release.promise;
    let id = "";
    const commit = await runtime.commitInTransactionAsync(tx => {
      id = runtime.createChildTask(tx, child, null).id;
      return TaskCommit.waiting({ phase: "done" }, [id]);
    });
    runtime.scheduleTask(id);
    return commit;
    }],
    ["done", async (_input, _checkpoint, runtime) =>
      runtime.commitAsync(() => TaskCommit.completed("parent saved"))],
  ]));
  registry.install(new Extension("drain", [], [], [parent, child]));
  const record = await runner.createTaskAsync(parent, null);
  await entered.promise;
  const closing = runner.disposeAsync();
  const external = await store.commitAsync(tx => tx.createTask(child, null));
  await assert.rejects(runner.runTaskAsync(external.id), /closing/);
  release.resolve();
  try {
    await closing;
    assert.deepEqual(store.task(record.id)!.outcome, { status: "completed", result: "parent saved" },
      "drain must not falsely fail the committed parent");
    assert.equal(store.tasks.find(t => t.ownerTaskId === record.id)!.status, TaskStatus.Terminal);
  } finally { release.resolve(); store.dispose(); }
});

test("review: waitForTask propagates scheduling rejection instead of an unresolved completion", { timeout: 1000 }, async () => {
  const store = DurableStore.createEphemeral();
  const runner = new DurableTaskRunner(store, new ExtensionRegistry());
  const definition = DurableTask.define("not-started", () => ({}), async () => TaskCommit.completed(null));
  const record = await store.commitAsync(tx => tx.createTask(definition, null));
  await runner.disposeAsync();
  try { await assert.rejects(runner.waitForTaskAsync(record.id), /closing/); }
  finally { store.dispose(); }
});

test("review: close drains direct and queued submissions before detaching and forbids external admission", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient(), session = sdkSession("native");
  client.createSession = async () => session.sdk;
  const stopped = barrier(), first = barrier(), settling = barrier(), release = barrier(), held = barrier();
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  const chat = await harness.rootAsync();
  const send = mock.method(session.sdk, "send", async () => { first.resolve(); return "sent"; });
  const a = assert.rejects(chat.submitAsync("first"), /closing/);
  const b = assert.rejects(chat.submitAsync("second"), /closing/);
  await first.promise;
  const holding = store.commitAsync(async () => { held.resolve(); await release.promise; });
  await held.promise;
  const commit = chat.commitAsync.bind(chat);
  const settlement = mock.method(chat, "commitAsync", (body: Parameters<typeof chat.commitAsync>[0]) => {
    settling.resolve(); return commit(body);
  });
  client.stop = async () => {
    assert.equal(chat.admission()?.["state"], "uncertain", "SDK stop must follow durable admission settlement");
    assert.equal(chat.busy, false);
    assert.equal(session.listeners.size, 0);
    stopped.resolve(); return [];
  };
  const close = harness.disposeAsync();
  void close.catch(() => {});
  try {
    await assert.rejects(chat.submitAsync("external"), /closing/);
    await settling.promise;
    assert.equal(chat.admission()?.["state"], "admitted", "uncertain settlement is held behind the actual store gate");
    assert.throws(() => new DurableTaskRunner(store, new ExtensionRegistry()), /already driven/);
    release.resolve();
    await Promise.all([a, b, close, stopped.promise, holding]);
    assert.equal(chat.admission()?.["prompt"], "first", "cancelled queued input must never create an admission");
    assert.equal(chat.admission()?.["state"], "uncertain");
    assert.equal(send.mock.callCount(), 1);
    const sequence = store.storeProvider.sequence;
    session.emit(sdkEvent("session.idle", "late-idle", {}));
    assert.equal(store.storeProvider.sequence, sequence, "no late settlement writes");
  } finally { release.resolve(); settlement.mock.restore(); await holding; await close; store.dispose(); }
});

test("review: close cancels pending public send wait and queue before releasing observers and deadline", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient(), session = sdkSession("native");
  client.createSession = async () => session.sdk; client.stop = async () => [];
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  const chat = await harness.rootAsync(), sent = barrier(), release = barrier(), rpcFinished = barrier();
  session.sdk.send = async () => {
    sent.resolve();
    await release.promise;
    rpcFinished.resolve();
    throw new Error("late RPC rejection after close");
  };
  const first = assert.rejects(chat.submitAsync("deadline"), /closing/);
  const second = assert.rejects(chat.submitAsync("queued"), /closing/);
  await sent.promise;
  const closing = harness.disposeAsync();
  await Promise.all([closing, first, second]);
  assert.equal(chat.admission()?.["state"], "uncertain");
  assert.equal(chat.admission()?.["prompt"], "deadline");
  assert.equal(session.listeners.size, 0);
  assert.equal(chat.busy, false);
  await assert.rejects(chat.submitAsync("closed"), /closing/);
  const sequence = store.sequence;
  release.resolve(); await rpcFinished.promise;
  assert.equal(store.sequence, sequence, "late public RPC failure cannot write after shutdown");
  store.dispose();
});

test("review: idle before send RPC does not complete admission or conceal late rejection", async () => {
  for (const fail of [false, true]) {
    const store = DurableStore.createEphemeral();
    const client = new CopilotClient(), session = sdkSession("native");
    client.createSession = async () => session.sdk; client.stop = async () => [];
    const harness = new DurableHarness(client, store, new ExtensionRegistry());
    const chat = await harness.rootAsync();
    const entered = barrier(), release = barrier();
    const original = new Error("late public send rejection");
    session.sdk.send = async () => {
      session.emit(sdkEvent("assistant.message", "answer", { content: "received" }));
      session.emit(sdkEvent("session.idle", "idle", {}));
      entered.resolve(); await release.promise;
      if (fail) throw original;
      return "sent";
    };
    const run = chat.submitAsync("prompt");
    const result = run.then(value => ({ value, error: null }), error => ({ value: undefined, error }));
    await entered.promise;
    await chat.checkpointBarrierAsync();
    assert.equal(chat.admission()?.["state"], "admitted", "idle alone must not settle the admission");
    release.resolve();
    const settled = await result;
    assert.equal(settled.error, fail ? original : null);
    assert.equal(chat.admission()?.["state"], fail ? "uncertain" : "completed");
    await harness.disposeAsync(); store.dispose();
  }
});

test("review: idle cannot clear the deadline while send RPC is pending", async () => {
  const store = DurableStore.createEphemeral();
  const client = new CopilotClient(), session = sdkSession("native");
  client.createSession = async () => session.sdk; client.stop = async () => [];
  const harness = new DurableHarness(client, store, new ExtensionRegistry());
  const chat = await harness.rootAsync(), release = barrier();
  session.sdk.send = async () => {
    session.emit(sdkEvent("session.idle", "idle", {}));
    await release.promise; return "sent";
  };
  try {
    await assert.rejects(chat.submitAsync("prompt", 10), /timed out/);
    assert.equal(chat.admission()?.["state"], "uncertain");
    assert.equal(session.listeners.size, 1, "deadline removes the submission observer");
  } finally { release.resolve(); await harness.disposeAsync(); store.dispose(); }
});

test("review: JSONL emits bounded immutable history and only dirty task records per commit", async () => {
  const dir = mkdtempSync(join(tmpdir(), "durable-ts-linear-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(dir, "store.jsonl") });
  let store = DurableStore.create(options);
  const document = new DocumentDefinition<import("./durable.js").JsonObject>("bounded");
  let immutable = 0, taskSerializations = 0;
  const stringify = JSON.stringify;
  const spy = mock.method(JSON, "stringify", (value: unknown, ...args: unknown[]) => {
    if (value && typeof value === "object") {
      const item = value as Record<string, unknown>;
      if (item["type"] === "document-version") immutable++;
      if (item["type"] === "task") taskSerializations++;
    }
    return Reflect.apply(stringify, JSON, [value, ...args]);
  });
  try {
    const task = DurableTask.define("bounded", () => ({}), async (_input, _checkpoint, runtime) =>
      runtime.commitAsync(() => TaskCommit.completed(null)));
    const id = await store.commitAsync(tx => {
      tx.createTask(task, null);
      return tx.createConversation().id;
    });
    const initialTasks = taskSerializations;
    for (let i = 0; i < 32; i++) {
      const before = immutable;
      await store.commitAsync(tx => {
        const draft = tx.doc(document, id); draft.value = { value: i };
        draft.save(tx.appendEntry(id, "bounded"));
      });
      assert.equal(immutable - before, 1, "each append must serialize exactly one new immutable version");
    }
    assert.equal(taskSerializations, initialTasks, "unchanged mutable tasks must not be reserialized");
    const before = immutable;
    await assert.rejects(store.commitAsync(tx => {
      const draft = tx.doc(document, id); draft.value = { value: -1 };
      draft.save(tx.appendEntry(id, "rollback")); throw new Error("rollback");
    }), /rollback/);
    await store.commitAsync(() => {});
    assert.equal(immutable, before);
    store.dispose(); store = DurableStore.create(options);
    await store.commitAsync(() => {});
    assert.equal(immutable, before, "reopen must remember committed history without serializing it");
    assert.equal(store.document(document.kind, id)!.versions.length, 32);
  } finally { spy.mock.restore(); store.dispose(); rmSync(dir, { recursive: true }); }
});

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
  knownNativeSessions(client);
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
  knownNativeSessions(client);
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
    const resumed = sdkSession(String(chat.binding()!["sessionId"]), history);
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
  knownNativeSessions(client);
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
