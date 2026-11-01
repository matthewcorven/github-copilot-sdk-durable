import assert from "node:assert/strict";
import { test } from "node:test";
import { mkdtempSync, readFileSync, appendFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AgentChoices, DocumentDefinition, DurableHarness, DurableHarnessOptions, DurableStore,
  DurableStoreOptions, DurableStoreFormat, ExtensionRegistry, PermissionPolicy, DurableTask,
  TaskCommit, DurableSandboxPolicy,
} from "./durable.js";

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
  } finally { await harness.disposeAsync(); store.dispose(); }
});
