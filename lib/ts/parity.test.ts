import assert from "node:assert/strict";
import { test } from "node:test";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import { CopilotClient } from "@github/copilot-sdk";
import {
  AgentChoices, DocumentDefinition, DurableHarness, DurableConversation, DurableStore,
  DurableStoreOptions, DurableTask, DurableTaskRunner, Extension, ExtensionRegistry,
  TaskCommit, TaskOutcome, TaskStatus,
} from "./durable.js";

test("transaction rollback leaves no partial entry or document", async () => {
  const store = DurableStore.createEphemeral();
  const doc = new DocumentDefinition<string[]>("parity.notes", undefined, undefined, () => []);
  try {
    const conversation = await store.commitAsync(tx => tx.createConversation());
    const entry = await store.commitAsync(tx => {
      const draft = tx.doc(doc, conversation.id);
      draft.value = ["before"];
      const entry = tx.appendEntry(conversation.id, "note");
      draft.save(entry);
      return entry;
    });
    await assert.rejects(store.commitAsync(tx => {
      tx.createConversation();
      const draft = tx.doc(doc, conversation.id);
      draft.value = ["partial"];
      draft.save(tx.appendEntry(conversation.id, "partial"));
      const other = tx.doc(new DocumentDefinition("parity.partial"), conversation.id);
      other.save(tx.appendEntry(conversation.id, "other"));
      throw new Error("interrupted transaction");
    }), /interrupted transaction/);
    assert.deepEqual(store.entries(conversation.id), [entry], "rollback leaked a partial entry");
    assert.deepEqual(store.document(doc.kind, conversation.id)?.latest, ["before"]);
    assert.equal(store.documents.length, 1, "rollback leaked a new document");
    assert.equal(store.conversations.length, 1, "rollback leaked a conversation");
  } finally { store.dispose(); }
});

test("fork inherits boundary state, independent edit and exact filtered transcript", async () => {
  const store = DurableStore.createEphemeral();
  const harness = new DurableHarness(new CopilotClient(), store, new ExtensionRegistry());
  const doc = new DocumentDefinition<string[]>("parity.notes", undefined, undefined, () => []);
  try {
    const record = await store.commitAsync(tx => tx.createConversation());
    const parent = harness.findConversation(record.id)!;
    await parent.configureAsync(new AgentChoices({ permissions: ["read:/before"] }));
    const write = (chat: DurableConversation, value: string) => chat.commitAsync(tx => {
      const entry = tx.appendEntry(chat.id, "note");
      const draft = tx.doc(doc, chat.id);
      draft.value = [value]; draft.save(entry);
      return entry;
    });
    const first = await write(parent, "v1");
    const second = await write(parent, "v2");
    // Exercise the store's fork primitives without claiming a native session fork.
    const forkRecord = await store.commitAsync(tx => {
      const fork = tx.createConversation({ parentConversationId: parent.id, forkEntryId: first });
      store.storeProvider.copyEntries(parent.id, fork.id, first);
      for (const document of [...store.documents].filter(d => d.ownerId === parent.id))
        store.storeProvider.copyDocument(document, fork.id, first);
      return fork;
    });
    const fork = new DurableConversation(harness, forkRecord);
    assert.deepEqual(fork.snapshot(doc), ["v1"]);
    assert.deepEqual(fork.entries(100), [first], "inherited bookkeeping or post-boundary entry leaked");
    assert.equal(fork.entries(100).length, 1);
    const own = await write(fork, "v1-forked");
    await fork.configureAsync(new AgentChoices({ permissions: ["read:/fork"] }));
    assert.deepEqual(fork.entries(100), [own, first]);
    assert.equal(fork.entries(100).length, 2, "fork transcript must contain inherited + own only");
    assert(!fork.entries(100).includes(second));
    assert.deepEqual(parent.entries(100), [second, first]);
    assert.deepEqual(parent.snapshot(doc), ["v2"]);
    assert.deepEqual(fork.snapshot(doc), ["v1-forked"]);
    assert.deepEqual(parent.choices().permissions, ["read:/before"]);
    assert.deepEqual(fork.choices().permissions, ["read:/fork"]);
  } finally { await harness.disposeAsync(); store.dispose(); }
});

test("recovery twice executes a memoized step once across actual process crashes", async () => {
  const directory = mkdtempSync(join(tmpdir(), "parity-recovery-"));
  const file = join(directory, "store.jsonl");
  const effects = join(directory, "effects.txt");
  const helper = fileURLToPath(new URL("./parity-recovery.ts", import.meta.url));
  try {
    for (const [mode, status] of [["create", 73], ["interrupt", 73], ["complete", 0]] as const) {
      const result = spawnSync(process.execPath, ["--import", "tsx", helper, file, effects, mode],
        { encoding: "utf8", timeout: 30000, cwd: fileURLToPath(new URL(".", import.meta.url)) });
      assert.equal(result.error, undefined);
      assert.equal(result.status, status, `${mode}: ${result.stdout}\n${result.stderr}`);
      assert.equal(readFileSync(effects, "utf8"), "charged\n", `${mode} repeated the side effect`);
    }
    const store = DurableStore.create(Object.assign(new DurableStoreOptions(), { path: file }));
    try {
      assert.equal(store.tasks.length, 1);
      const task = store.tasks[0];
      assert(task);
      assert.equal(task.status, TaskStatus.Terminal);
      assert.equal(TaskOutcome.fromJson(task.outcome)?.status, "completed");
      assert.equal(TaskOutcome.fromJson(task.outcome)?.result, "receipt");
      assert.equal(await store.memoAsync(task.id, "charged"), true);
      assert.equal(task.restarts, 2);
    } finally { store.dispose(); }
  } finally { rmSync(directory, { recursive: true }); }
});

test("abort cascade persists exact terminal states for parent and two children", async () => {
  const directory = mkdtempSync(join(tmpdir(), "parity-abort-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(directory, "store.jsonl") });
  let store = DurableStore.create(options);
  const registry = new ExtensionRegistry();
  const aborted: string[] = [];
  const definition = DurableTask.define("parity.work", () => ({ phase: "run" }),
    (_input, _checkpoint, runtime) => runtime.commitAsync(() => TaskCommit.completed(null)),
    (_input, _checkpoint, runtime) => {
      aborted.push(runtime.task.id); return runtime.commitAsync(() => TaskCommit.aborted());
    });
  registry.install(new Extension("parity", [], [], [definition]));
  const runner = new DurableTaskRunner(store, registry);
  try {
    const [parent, child1, child2] = await store.commitAsync(tx => {
      const parent = tx.createTask(definition, null);
      return [parent, tx.createTask(definition, null, { ownerTaskId: parent.id }),
        tx.createTask(definition, null, { ownerTaskId: parent.id })];
    });
    await runner.abortTaskAsync(parent.id);
    await runner.abortTaskAsync(parent.id);
    assert.deepEqual(aborted, [child1.id, child2.id, parent.id], "abort must visit each owned task once");
    await runner.disposeAsync();
    store.dispose();
    store = DurableStore.create(options);
    assert.equal(store.tasks.length, 3);
    assert.equal(store.tasks.filter(t => t.ownerTaskId === parent.id).length, 2);
    for (const task of store.tasks) {
      assert.equal(task.status, TaskStatus.Terminal, `task ${task.id} is still live`);
      assert.equal(TaskOutcome.fromJson(task.outcome)?.status, "aborted", `task ${task.id} not aborted`);
    }
  } finally { await runner.disposeAsync(); store.dispose(); rmSync(directory, { recursive: true }); }
});
