// 16 - Coding tools (LIVE). Actual file effects, tool events and SDK hook timing.
import { mkdtempSync, readFileSync, realpathSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { isAbsolute, join, relative, resolve, sep } from "node:path";
import { execFileSync } from "node:child_process";
import { AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry } from "../../lib/ts/durable.ts";

const workspace = mkdtempSync(join(tmpdir(), "durable-coding-"));
writeFileSync(join(workspace, "notes.txt"), "hello world\n");
const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
const stringArg = (args: unknown, name: string): string => {
  if (args === null || typeof args !== "object") throw new Error("Tool arguments must be an object");
  const value: unknown = Reflect.get(args, name);
  if (typeof value !== "string") throw new Error(`Tool argument ${name} must be a string`);
  return value;
};
const fileFor = (name: string): string => {
  const path = realpathSync(resolve(workspace, name));
  const local = relative(realpathSync(workspace), path);
  if (local === ".." || local.startsWith(`..${sep}`) || isAbsolute(local)) throw new Error("File is outside the example workspace");
  return path;
};
const properties = (names: string[]): Record<string, unknown> => ({
  type: "object", properties: Object.fromEntries(names.map(name => [name, { type: "string" }])), required: names,
});
const read = {
  name: "read_file", description: "Read a file", overridesBuiltInTool: true,
  parameters: properties(["path"]),
  handler: (args: unknown) => readFileSync(fileFor(stringArg(args, "path")), "utf8"),
};
const edit = {
  name: "edit_file", description: "Edit a file", parameters: properties(["path", "oldText", "newText"]),
  handler: (args: unknown) => {
    const file = fileFor(stringArg(args, "path"));
    const oldText = stringArg(args, "oldText");
    const content = readFileSync(file, "utf8");
    if (!content.includes(oldText)) throw new Error(`${oldText} not found in ${file}`);
    writeFileSync(file, content.replaceAll(oldText, stringArg(args, "newText")));
    return `edited ${stringArg(args, "path")}`;
  },
};
const shell = {
  name: "run_shell", description: "Run a shell command", parameters: properties(["command"]),
  handler: (args: unknown) => {
    const start = performance.now();
    const output = execFileSync("/bin/sh", ["-c", stringArg(args, "command")], {
      cwd: workspace, encoding: "utf8", timeout: 10000,
    });
    return `${output.trim()} (exit 0, ${Math.round(performance.now() - start)} ms)`;
  },
};
registry.install(new Extension("coding", [read, edit, shell]));
const timings: number[] = [];
const started = new Map<string, number>();
let harness: DurableHarness | undefined;
try {
  harness = await DurableHarness.openAsync(store, registry);
  const root = await harness.rootAsync(new AgentChoices({
    extensions: ["coding"], workingDirectory: workspace,
    permissions: ["custom-tool:read_file", "custom-tool:edit_file", "custom-tool:run_shell"],
    hooks: {
      onPreToolUse: input => {
        if (input.toolName === "run_shell") started.set(JSON.stringify(input.toolArgs), performance.now());
      },
      onPostToolUse: input => {
        if (input.toolName === "run_shell") {
          const key = JSON.stringify(input.toolArgs);
          const begin = started.get(key);
          if (begin !== undefined) {
            timings.push(performance.now() - begin);
            started.delete(key);
          }
        }
      },
    },
  }));
  const report = await root.waitForEffectiveModelAsync();
  console.log(`effective model: ${report.model ?? "(not reported)"}`);
  const answer = await root.submitAsync("Use the read tool on notes.txt, then the edit tool to replace 'world' with 'durable', then the bash tool to run 'cat notes.txt'. Then tell me what the file says.");
  const context = await root.contextAsync();
  const names = context.filter(e => e.type === "tool.execution_start").map(e => e.data.toolName);
  Check.that(names.includes("read_file"), "the app's read_file tool ran");
  Check.that(names.includes("edit_file"), "the app's edit_file tool ran");
  Check.that(names.includes("run_shell"), "the app's run_shell tool ran");
  const failures = context.filter(e => e.type === "tool.execution_complete" && e.data.error != null);
  Check.equal(0, failures.length, "no tool call failed");
  Check.equal("hello durable", readFileSync(join(workspace, "notes.txt"), "utf8").trim(), "the edit reached the file");
  Check.equal(1, timings.length, "the hook timed the shell call");
  console.log(`answer: ${answer?.data.content}; file: hello durable; bash calls timed: ${timings.length}`);
} finally {
  await harness?.disposeAsync();
  store.dispose();
  rmSync(workspace, { recursive: true });
}
