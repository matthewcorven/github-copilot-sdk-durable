import assert from "node:assert/strict";
import { test } from "node:test";
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
} from "./durable.js";
import { CopilotClient } from "@github/copilot-sdk";

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

test("JSONL retains exact CRLF/mixed UTF8 boundaries and discards all unterminated tails", async () => {
  for (const endings of [["\r\n", "\r\n"], ["\n", "\r\n"]]) {
    for (const tail of ["", '{"v":1', JSON.stringify({
      v: 1, seq: 3, type: "conversation", payload: { id: "torn", createdAt: "2026-11-01T00:00:00Z" },
    })]) {
      const dir = mkdtempSync(join(tmpdir(), "durable-ts-boundary-"));
      const file = join(dir, "store.jsonl");
      const options = Object.assign(new DurableStoreOptions(), { path: file, format: DurableStoreFormat.Jsonl });
      const ids = ["\u96ea\u00e9", "\u03bb"];
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
