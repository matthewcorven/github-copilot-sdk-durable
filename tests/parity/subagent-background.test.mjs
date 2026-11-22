import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { stripTypeScriptTypes } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { runInNewContext } from "node:vm";
import {
  AgentChoices, DocumentDefinition, DocumentHistory, DurableHarness, DurableStore,
  DurableStoreOptions, Extension, ExtensionRegistry, ForkPolicy,
} from "../../lib/ts/durable.ts";

const source = readFileSync(new URL("../../examples/ts/22-subagent-background.ts", import.meta.url), "utf8");
const names = ["constructor", "toString", "hasOwnProperty", "__proto__", "reader"];

function actualTool(text) {
  const start = text.indexOf("  const subagents =", text.indexOf("async function main()"));
  const end = text.indexOf("  const registry =", start);
  assert.ok(start >= 0 && end > start, "load the actual example's document and complete handler");
  return runInNewContext(`${stripTypeScriptTypes(text.slice(start, end))}
    ({ tool, subagents, bind(harness, conversation) { host = harness; chat = conversation; } });`,
  { AgentChoices, DocumentDefinition, DocumentHistory, ForkPolicy, Object });
}

async function fixture(text = source) {
  const directory = mkdtempSync(join(tmpdir(), "durable-subagent-regression-"));
  const options = Object.assign(new DurableStoreOptions(), { path: join(directory, "session.jsonl") });
  const creates = [], resumes = [], sends = [], aborts = [];
  const histories = new Map();
  function session(id) {
    const listeners = new Set();
    const events = histories.get(id) ?? [];
    histories.set(id, events);
    return {
      sessionId: id,
      on(listener) { listeners.add(listener); return () => listeners.delete(listener); },
      async getEvents() { return events; },
      async send({ prompt }) {
        sends.push({ id, prompt });
        for (const [type, data] of [
          ["assistant.message", { content: `reply: ${prompt}` }], ["session.idle", {}],
        ]) {
          const event = { type, data, id: `${id}-${events.length}` };
          events.push(event);
          for (const listener of listeners) listener(event);
        }
      },
      async abort() { aborts.push(id); },
    };
  }
  const client = {
    async createSession(config) { creates.push(config); return session(config.sessionId); },
    async getSessionMetadata(id) { return histories.has(id) ? { sessionId: id } : null; },
    async resumeSession(id) { resumes.push(id); return session(id); },
    async stop() { return []; },
  };
  let host, store, chat, loaded;
  async function open() {
    loaded = actualTool(text);
    const registry = new ExtensionRegistry();
    registry.install(new Extension("subagent-tools", [loaded.tool]));
    store = DurableStore.create(options);
    host = new DurableHarness(client, store, registry);
    chat = await host.rootAsync(new AgentChoices({
      extensions: ["subagent-tools"], permissions: ["custom-tool:subagent"],
    }));
    loaded.bind(host, chat);
  }
  await open();
  return {
    creates, resumes, sends, aborts,
    call(args) { return chat.agent().tools.find(tool => tool.name === "subagent").handler(args); },
    snapshot() { return chat.snapshot(loaded.subagents); },
    get host() { return host; },
    get store() { return store; },
    get chat() { return chat; },
    async reopen() { await host.disposeAsync(); store.dispose(); await open(); },
    async close() {
      try { await host.disposeAsync(); }
      finally { store.dispose(); rmSync(directory, { recursive: true }); }
    },
  };
}

for (const name of names) {
  test(`TS22 fresh registry spawns and stores own key ${name}`, async () => {
    const f = await fixture();
    try {
      assert.equal(await f.call({ action: "spawn", name }), `Started ${name}.`);
      const agents = f.snapshot();
      assert.ok(Object.hasOwn(agents, name), `${name} is persisted as an own registry key`);
      assert.equal(f.creates.length, 2, "one root and exactly one child SDK session");
      assert.equal(f.store.conversations.length, 2, "exactly one real child conversation");
      assert.equal(f.host.findConversation(agents[name].conversationId).record.ownerConversationId, f.chat.id);
      assert.equal(f.creates[1].model, "mai-code-1.1-flash");
      assert.equal(f.creates[1].systemMessage.content,
        `<instructions>\nYou are the subagent "${name}". Answer the main agent's requests.\n</instructions>`);
    } finally { await f.close(); }
  });

  test(`TS22 fresh registry routes missing ${name} without inherited entries`, async () => {
    const f = await fixture();
    try {
      assert.equal(await f.call({ action: "status", name }), "No subagents.");
      for (const action of ["send", "stop"])
        assert.equal(await f.call({ action, name, message: "hello" }), `No subagent named ${name}.`);
      assert.equal(f.creates.length, 1);
      assert.equal(f.resumes.length, 0);
      assert.deepEqual(f.sends, []);
      assert.deepEqual(f.aborts, []);
      assert.equal(f.snapshot(), null);
    } finally { await f.close(); }
  });
}

async function lifecycle(text = source) {
  const prototype = Object.getOwnPropertyDescriptors(Object.prototype);
  const f = await fixture(text);
  try {
    for (const name of names)
      assert.equal(await f.call({ action: "spawn", name }), `Started ${name}.`);
    const before = f.snapshot();
    assert.deepEqual(Object.keys(before), names, "all arbitrary keys are serialized as own properties");
    assert.deepEqual(Object.getOwnPropertyDescriptors(Object.prototype), prototype, "no Object.prototype pollution");
    assert.equal(Object.getPrototypeOf(before), Object.prototype);
    assert.equal(f.store.tasks.length, 0, "named subagents remain conversations, not new tasks");
    for (const restarted of [false, true]) {
      if (restarted) {
        await f.reopen();
        assert.deepEqual(f.snapshot(), before, "the actual JSONL model survives close/reopen");
      }
      assert.equal(await f.call({ action: "status" }), names.map(name => `${name}: idle`).join("\n"));
      for (const name of names) {
        const entry = f.snapshot()[name];
        assert.ok(Object.hasOwn(f.snapshot(), name));
        assert.equal(entry.reported, "0");
        const count = f.store.conversations.length;
        const entries = f.store.entries(f.chat.id).length;
        assert.equal(await f.call({ action: "spawn", name }), `${name} already exists; use send.`);
        assert.equal(f.store.conversations.length, count, "duplicates do not create conversations");
        assert.equal(f.store.entries(f.chat.id).length, entries, "duplicates do not rewrite the registry");
        assert.equal(await f.call({ action: "status", name }), `${name}: idle`);
        assert.equal(await f.call({ action: "send", name, message: "hello" }), `[${name} answered] reply: hello`);
        const target = f.host.findConversation(entry.conversationId);
        assert.deepEqual(f.sends.at(-1), { id: target.sessionId, prompt: "hello" });
        assert.equal(await f.call({ action: "stop", name }), `Stopped ${name}.`);
        assert.equal(f.aborts.at(-1), target.sessionId);
        assert.equal(await f.call({ action: "send", name }), "send needs a message.");
      }
      assert.equal(await f.call({ action: "status", name: "valueOf" }), "No subagents.");
      for (const action of ["send", "stop"])
        assert.equal(await f.call({ action, name: "valueOf", message: "hello" }), "No subagent named valueOf.");
      assert.deepEqual(f.snapshot(), before, "status/send/stop preserve the registry");
    }
    assert.equal(f.creates.length, 1 + names.length, "each named child was created once across recovery");
    assert.equal(f.resumes.length, 1 + names.length, "the root and each child resume their existing SDK session");
    assert.deepEqual(Object.getOwnPropertyDescriptors(Object.prototype), prototype);
  } finally { await f.close(); }
}

test("TS22 actual handler lifecycle preserves arbitrary keys and routes after JSONL recovery", async () => {
  await lifecycle();
});

test("TS22 actual handler preserves invalid/missing argument behavior", async () => {
  const f = await fixture();
  try {
    for (const args of [null, 1, {}, { action: 1 }])
      await assert.rejects(f.call(args), /subagent action must be a string/);
    for (const action of ["spawn", "send", "stop"])
      for (const name of [undefined, "", 42])
        assert.equal(await f.call({ action, name }), `${action} needs a name.`);
    assert.equal(await f.call({ action: "status" }), "No subagents.");
    assert.equal(await f.call({ action: "send", name: "absent", message: "hello" }), "No subagent named absent.");
    assert.equal(f.creates.length, 1);
    assert.equal(f.store.conversations.length, 1);
    assert.equal(f.snapshot(), null);
  } finally { await f.close(); }
});

async function spawnConstructor(text) {
  const f = await fixture(text);
  try {
    assert.equal(await f.call({ action: "spawn", name: "constructor" }), "Started constructor.");
    assert.ok(Object.hasOwn(f.snapshot(), "constructor"));
    assert.equal(f.store.conversations.length, 2);
  } finally { await f.close(); }
}

async function missingRoutes(text, action) {
  const f = await fixture(text);
  try {
    for (const name of names) {
      assert.equal(await f.call({ action, name, message: "hello" }),
        action === "status" ? "No subagents." : `No subagent named ${name}.`);
    }
    assert.equal(f.creates.length, 1);
    assert.equal(f.resumes.length, 0);
    assert.deepEqual(f.sends, []);
    assert.deepEqual(f.aborts, []);
  } finally { await f.close(); }
}

for (const [label, before, after, exercise, diagnostic] of [
  ["spawn inherited lookup", "if (Object.hasOwn(agents, name))", "if (agents[name])",
    spawnConstructor,
    /Started constructor/],
  ["status inherited lookup", "Object.hasOwn(agents, each) ? agents[each] : undefined", "agents[each]",
    text => missingRoutes(text, "status"), /constructor: idle/],
  ["send inherited lookup", "Object.hasOwn(agents, name) ? agents[name] : undefined", "agents[name]",
    text => missingRoutes(text, "send"), /Unknown conversation 'undefined'/],
  ["stop inherited lookup", "Object.hasOwn(agents, name) ? agents[name] : undefined", "agents[name]",
    text => missingRoutes(text, "stop"),
    /Unknown conversation 'undefined'/],
  ["unsafe __proto__ assignment",
    'draft.value = { ...draft.value, [name]: { conversationId: child.id, reported: "0" } };',
    'draft.value[name] = { conversationId: child.id, reported: "0" };',
    lifecycle,
    /all arbitrary keys are serialized as own properties/],
]) {
  test(`TS22 actual-source mutant is killed: ${label}`, async (t) => {
    assert.ok(source.includes(before), `actual mutation anchor: ${before}`);
    await exercise(source);
    await assert.rejects(exercise(source.replace(before, after)), error => {
      assert.match(error.message, diagnostic);
      t.diagnostic(`Killed ${label}: ${error.message}`);
      return true;
    });
  });
}
