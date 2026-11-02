import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { stripTypeScriptTypes } from "node:module";
import { test } from "node:test";
import { DurableHarness } from "../../lib/ts/durable.ts";

const facade = new URL("../../lib/ts/durable.ts", import.meta.url).href;
let sequence = 0;

async function runExample(name, mutation) {
  let source = readFileSync(new URL(`../../examples/ts/${name}.ts`, import.meta.url), "utf8");
  if (mutation) {
    assert.ok(source.includes(mutation[0]), `mutation anchor: ${mutation[0]}`);
    source = source.replace(...mutation);
  }
  source = source.replace("../../lib/ts/durable.ts", facade);
  const entry = 'main().catch((error: unknown) => { console.error(error); process.exitCode = 1; });';
  assert.ok(source.includes(entry), "await the actual standalone main without swallowing its assertion");
  source = source.replace(entry, "await main();");
  const calls = [];
  const sessions = [];
  const client = {
    async createSession(config) {
      const listeners = new Set();
      const events = [{ type: "session.start", data: { selectedModel: "mock-reported-model" } }];
      const emit = (type, data) => {
        const event = { type, data, id: `event-${events.length}` };
        events.push(event);
        for (const listener of listeners) listener(event);
      };
      const session = {
        sessionId: config.sessionId,
        on(listener) { listeners.add(listener); return () => listeners.delete(listener); },
        async getEvents() { return events; },
        async send({ prompt }) {
          if (name === "27-reviewer") {
            for (const [content, blocked] of [["Check user.name.", true], ["nO fUrThEr FiNdInGs.", false]]) {
              emit("assistant.message", { content });
              const decision = await config.hooks.onAgentStop({});
              assert.equal(decision?.decision === "block", blocked, "actual stop hook accepts mixed-case completion");
            }
          } else {
            const tool = config.tools.find(tool => tool.name === "run_shell");
            assert.ok(tool?.handler, "invoke the facade-selected actual shell handler");
            const command = prompt.includes("echo plain:") ? "echo plain: $VIRTUAL_ENV" : "echo venv: $VIRTUAL_ENV";
            const output = await tool.handler({ command }, { sessionId: config.sessionId });
            calls.push({ command, output });
            emit("assistant.message", { content: output });
          }
          emit("session.idle", {});
        },
      };
      sessions.push(config);
      return session;
    },
    async stop() { return []; },
  };
  const original = DurableHarness.openAsync;
  const ambient = process.env.VIRTUAL_ENV;
  process.env.VIRTUAL_ENV = "/ambient/recommended-setup/.venv";
  DurableHarness.openAsync = async (store, registry) => new DurableHarness(client, store, registry);
  try {
    await import(`data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source)).toString("base64")}#${sequence++}`);
    if (name === "29-tool-override") {
      assert.equal(calls.length, 2, "both real handlers executed");
      assert.equal(calls[0].output, "plain: /ambient/recommended-setup/.venv");
      assert.match(calls[1].output, /^venv: .*\/durable-venv-[^/]+\/\.venv$/);
      assert.notEqual(calls[0].output.slice(7), calls[1].output.slice(6));
    } else {
      assert.equal(sessions[1].model, "mai-code-1.1-flash");
    }
  } finally {
    DurableHarness.openAsync = original;
    if (ambient === undefined) delete process.env.VIRTUAL_ENV;
    else process.env.VIRTUAL_ENV = ambient;
  }
}

test("TS27 actual completed review accepts mixed-case final marker", async () => {
  await runExample("27-reviewer");
});

test("TS29 actual selected/wrapped shell handlers allow an unrelated ambient venv", async () => {
  await runExample("29-tool-override");
});

test("advanced actual-source assertion and tool-selection mutants fail at the protected gate", async () => {
  for (const [name, before, after, diagnostic] of [
    ["27-reviewer", "?.toLowerCase().includes(done.toLowerCase())", "?.includes(done)", /the last pass says/],
    ["29-tool-override", 'new Extension("venv", [tool(true)])', 'new Extension("venv", [tool(false)])', /the venv shell replaced/],
    ["29-tool-override", 'extensions: ["shell", "timing"], tools:', 'extensions: ["shell", "timing", "venv"], tools:', /the plain conversation kept/],
  ]) {
    await assert.rejects(runExample(name, [before, after]), diagnostic);
  }
});
