// A coding tool follows the conversation's reconfigured working directory.
import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtempSync, mkdirSync, realpathSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import {
  AgentChoices, DurableHarness, DurableStore, Extension, ExtensionRegistry,
  PromptSection, type DurableTool,
} from "../../lib/ts/durable.ts";

async function main() {
  const workspace = realpathSync(mkdtempSync(join(tmpdir(), "durable-agent-")));
  const app = join(workspace, "app");
  mkdirSync(app);
  let currentDirectory = workspace;
  const userSettings = { parallelTools: true, maxRetries: 3 };
  const shell = promisify(execFile);
  const runShell: DurableTool = {
    name: "run_shell", description: "Run a shell command",
    parameters: {
      type: "object", properties: { command: { type: "string" } }, required: ["command"],
    },
    handler: async (args) => {
      if (args === null || typeof args !== "object" || !("command" in args) ||
        typeof args.command !== "string") throw new Error("run_shell command must be a string");
      const command = args.command;
      const started = performance.now();
      const result = await shell("/bin/sh", ["-c", command], { cwd: currentDirectory });
      return `${result.stdout.trim()} (exit 0, ${Math.floor(performance.now() - started)} ms)`;
    },
  };
  const registry = new ExtensionRegistry();
  registry.install(new Extension("coding", [runShell], [
    new PromptSection("preamble", async () => "You are a coding agent. Use the tools to inspect the project.", false),
    new PromptSection("cwd", async (input) => input.workingDirectory),
  ]));
  const store = DurableStore.createEphemeral();
  try {
    const harness = await DurableHarness.openAsync(store, registry);
    try {
      const root = await harness.rootAsync(new AgentChoices({
        extensions: ["coding"], workingDirectory: workspace,
        permissions: ["custom-tool:run_shell"],
      }));
      const effective = await root.waitForEffectiveModelAsync();
      console.log(`effective model: ${effective.model ?? "(not reported)"}`);
      currentDirectory = root.agent().workingDirectory ?? workspace;
      const first = await root.submitAsync("Use the run_shell tool to run 'pwd', then tell me the directory.");
      const firstDirectory = root.agent().workingDirectory;
      console.log(`answer: ${first?.data.content ?? ""}`);
      await root.configureAsync(new AgentChoices({
        extensions: ["coding"], workingDirectory: app, permissions: ["custom-tool:run_shell"],
      }));
      currentDirectory = root.agent().workingDirectory ?? workspace;
      userSettings.parallelTools = false;
      const second = await root.submitAsync("Use the run_shell tool to run 'pwd' again, then tell me the directory.");
      console.log(`answer: ${second?.data.content ?? ""}`);
      assert.equal(firstDirectory, workspace, "the conversation's working directory is the one that was configured");
      assert.ok(first?.data.content.includes(workspace), "the tool ran in the conversation's directory");
      assert.equal(root.agent().workingDirectory, app, "the conversation's working directory changed");
      assert.ok(second?.data.content.includes(app), "the tool followed the conversation's new directory");
      assert.ok(second?.data.content.includes(app), "the tool ran in the new directory");
      console.log(`live settings: maxRetries=${userSettings.maxRetries} toolExecution=${userSettings.parallelTools ? "parallel" : "sequential"}`);
    } finally {
      await harness.disposeAsync();
    }
  } finally {
    store.dispose();
    rmSync(workspace, { recursive: true });
  }
}

main().catch((error: unknown) => { console.error(error); process.exitCode = 1; });
