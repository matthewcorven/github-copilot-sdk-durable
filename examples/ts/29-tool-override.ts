// The winning shell implementation is wrapped without capturing the base tool.
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import {
  AgentChoices, DurableHarness, DurableStore, Extension, ExtensionRegistry, ToolWrapper, type DurableTool,
} from "../../lib/ts/durable.ts";

async function main() {
  const project = mkdtempSync(join(tmpdir(), "durable-venv-"));
  mkdirSync(join(project, ".venv", "bin"), { recursive: true });
  writeFileSync(join(project, ".venv", "bin", "activate"), `export VIRTUAL_ENV="${project}/.venv"\n`);
  const shell = promisify(execFile);
  function tool(activate: boolean): DurableTool {
    return {
      name: "run_shell", description: "Run a shell command",
      parameters: { type: "object", properties: { command: { type: "string" } }, required: ["command"] },
      handler: async (args) => {
        if (args === null || typeof args !== "object" || !("command" in args) ||
          typeof args.command !== "string") throw new Error("run_shell command must be a string");
        // POSIX dot works with /bin/sh on both Linux and macOS.
        const command = activate ? `. .venv/bin/activate && ${args.command}` : args.command;
        return (await shell("/bin/sh", ["-c", command], { cwd: project })).stdout.trim();
      },
    };
  }
  const timings: number[] = [];
  const registry = new ExtensionRegistry();
  registry.install(new Extension("shell", [tool(false)]));
  registry.install(new Extension("timing", [], [], [], [
    new ToolWrapper("run_shell", (inner) => {
      const handler = inner.handler;
      if (!handler) throw new Error("the winning run_shell tool has no handler");
      return {
        ...inner, description: "Run a shell command (timed)",
        handler: async (args, invocation) => {
          const started = performance.now();
          try { return await handler(args, invocation); }
          finally { timings.push(Math.floor(performance.now() - started)); }
        },
      };
    }),
  ]));
  registry.install(new Extension("venv", [tool(true)]));
  const store = DurableStore.createEphemeral();
  try {
    const harness = await DurableHarness.openAsync(store, registry);
    try {
      const plain = await harness.rootAsync(new AgentChoices({
        extensions: ["shell", "timing"], tools: ["run_shell"], permissions: ["custom-tool:run_shell"],
      }));
      const effective = await plain.waitForEffectiveModelAsync();
      console.log(`effective model: ${effective.model ?? "(not reported)"}`);
      const python = await harness.createConversationAsync(new AgentChoices({
        extensions: ["shell", "timing", "venv"], tools: ["run_shell"], permissions: ["custom-tool:run_shell"],
      }));
      console.log(`plain tools: [${plain.agent().tools.map((tool) => tool.name)}]`);
      console.log(`python tools: [${python.agent().tools.map((tool) => tool.name)}]`);
      await plain.submitAsync("Use the run_shell tool to run 'echo plain: $VIRTUAL_ENV', then tell me the output.");
      await python.submitAsync("Use the run_shell tool to run 'echo venv: $VIRTUAL_ENV', then tell me the output.");
      const plainAnswer = plain.answers.at(-1) ?? "";
      const venvAnswer = python.answers.at(-1) ?? "";
      console.log(`plain answer: ${plainAnswer}`);
      console.log(`python answer: ${venvAnswer}`);
      console.log(`timed bash calls: ${timings.length}`);
      assert.ok(venvAnswer.includes(join(project, ".venv")), "the venv shell replaced the plain one");
      assert.ok(!plainAnswer.includes(join(project, ".venv")), "the plain conversation kept the plain shell");
      assert.equal(timings.length, 2, "the wrapper timed both shell calls");
    } finally { await harness.disposeAsync(); }
  } finally {
    store.dispose();
    rmSync(project, { recursive: true });
  }
}

main().catch((error: unknown) => { console.error(error); process.exitCode = 1; });
