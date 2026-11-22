import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, symlinkSync } from "node:fs";
import { stripTypeScriptTypes } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DurableHarness } from "../../lib/ts/durable.ts";

let sequence = 0;
const source = readFileSync(new URL("../../examples/ts/25-coding-agent.ts", import.meta.url), "utf8");

async function runExample(aliased, mutation) {
  const parent = mkdtempSync(join(realpathSync(tmpdir()), "durable-agent-regression-"));
  const physical = join(parent, "physical");
  const alias = join(parent, "alias");
  mkdirSync(physical);
  symlinkSync(physical, alias, "dir");
  let text = source;
  if (mutation) {
    assert.ok(text.includes(mutation[0]), "mutate the actual standalone workspace declaration");
    text = text.replace(...mutation);
  }
  text = text.replace("../../lib/ts/durable.ts", new URL("../../lib/ts/durable.ts", import.meta.url).href);
  const entry = 'main().catch((error: unknown) => { console.error(error); process.exitCode = 1; });';
  assert.ok(text.includes(entry));
  text = text.replace(entry, "await main();");
  const calls = [];
  const client = {
    async createSession(config) {
      const listeners = new Set();
      const events = [{ type: "session.start", data: { selectedModel: "mock-reported-model" } }];
      return {
        sessionId: config.sessionId,
        on(listener) { listeners.add(listener); return () => listeners.delete(listener); },
        async getEvents() { return events; },
        async setTools(tools) { config.tools = tools; },
        async send() {
          const tool = config.tools.find(tool => tool.name === "run_shell");
          assert.ok(tool?.handler, "run the actual facade-selected shell handler");
          const output = await tool.handler({ command: "pwd" }, { sessionId: config.sessionId });
          calls.push({ directory: config.workingDirectory, output });
          for (const [type, data] of [["assistant.message", { content: output }], ["session.idle", {}]]) {
            const event = { type, data, id: `event-${events.length}` };
            events.push(event);
            for (const listener of listeners) listener(event);
          }
        },
      };
    },
    async stop() { return []; },
  };
  const open = DurableHarness.openAsync;
  const previousTmp = process.env.TMPDIR;
  process.env.TMPDIR = aliased ? alias : physical;
  DurableHarness.openAsync = async (store, registry) => new DurableHarness(client, store, registry);
  try {
    await import(`data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(text)).toString("base64")}#${sequence++}`);
    assert.equal(calls.length, 2, "both actual shell invocations execute across reconfiguration");
    assert.ok(calls[0].directory.startsWith(`${physical}/durable-agent-`), "SDK cwd is physically canonical");
    assert.equal(calls[0].output.split(" (exit")[0], calls[0].directory);
    assert.equal(calls[1].output.split(" (exit")[0], join(calls[0].directory, "app"));
  } finally {
    DurableHarness.openAsync = open;
    if (previousTmp === undefined) delete process.env.TMPDIR;
    else process.env.TMPDIR = previousTmp;
    rmSync(parent, { recursive: true, force: true });
  }
}

test("TS25 actual standalone assertions and real shell pwd pass with a physical TMPDIR", async () => {
  await runExample(false);
});

test("TS25 actual standalone assertions and real shell pwd pass with a symlinked TMPDIR", async () => {
  await runExample(true);
});

test("TS25 actual uncanonicalized-workspace mutant fails its original directory assertion", async () => {
  await assert.rejects(runExample(true, [
    'realpathSync(mkdtempSync(join(tmpdir(), "durable-agent-")))',
    'mkdtempSync(join(tmpdir(), "durable-agent-"))',
  ]), /the tool ran in the conversation's directory/);
});
