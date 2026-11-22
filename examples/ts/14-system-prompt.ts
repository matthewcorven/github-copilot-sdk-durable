// 14 - System prompt. Configuration isolation and per-request prompt rendering.
import { mkdtempSync, mkdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { AgentChoices, Check, DurableHarness, DurableHarnessOptions, DurableStore, Extension, ExtensionRegistry, PromptSection } from "../../lib/ts/durable.ts";

const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
registry.install(new Extension("coding", [], [
  new PromptSection("preamble", async () => "You are a coding agent.", false),
  new PromptSection("cwd", async input => input.workingDirectory),
]));
registry.install(new Extension("agents-md", [], [
  new PromptSection("agents_md", async () => "Run npm run check after changes."),
]));
const repo = mkdtempSync(join(tmpdir(), "durable-repo-"));
let harness: DurableHarness | undefined;
try {
  const fixture = process.env.DURABLE_EXAMPLE_FIXTURE;
  harness = await DurableHarness.openAsync(store, registry, new DurableHarnessOptions({
    runtimeArguments: fixture ? ["--fixture", fixture] : [],
  }));
  const root = await harness.rootAsync(new AgentChoices({ extensions: ["coding", "agents-md"], workingDirectory: repo }));
  const report = root.effectiveModel;
  console.log(`effective model: ${report.model ?? "(not reported)"}`);
  const subagent = await harness.createConversationAsync(new AgentChoices({
    extensions: ["coding"], workingDirectory: repo, instructions: "Only read; never edit files.",
  }));
  const rootAgent = root.agent();
  const rootInstructions = rootAgent.instructions;
  const subagentInstructions = subagent.agent().instructions;
  const rootPrompt = await harness.buildSystemMessage(rootAgent, rootAgent.extensions.flatMap(e => [...e.sections]), root);
  const childAgent = subagent.agent();
  const childPrompt = await harness.buildSystemMessage(childAgent, childAgent.extensions.flatMap(e => [...e.sections]), subagent);
  console.log(`root system prompt:\n${rootPrompt}\nsubagent system prompt:\n${childPrompt}`);
  const packages = join(repo, "packages");
  mkdirSync(packages);
  await root.configureAsync(new AgentChoices({ extensions: ["coding", "agents-md"], workingDirectory: packages }));
  console.log(`root cwd now: ${root.agent().workingDirectory}`);
  console.log(`subagent cwd still: ${subagent.agent().workingDirectory}`);
  console.log(`subagent instructions still: ${subagent.agent().instructions}`);
  Check.equal(packages, root.agent().workingDirectory, "the root's working directory changed");
  Check.that(subagent.agent().workingDirectory !== packages, "the subagent's working directory is unchanged");
  Check.equal(subagentInstructions, subagent.agent().instructions, "the subagent's instructions are unchanged");
  Check.equal(rootInstructions, root.agent().instructions, "the root's instructions are unchanged");
  Check.that(root.agent().extensions.length >= 2, "the root selects both extensions");
  Check.that(rootPrompt.includes(repo), "the root prompt renders its working directory");
  Check.that(rootPrompt.includes("Run npm run check after changes."), "the root renders AGENTS.md");
  Check.that(!childPrompt.includes("Run npm run check after changes."), "the child deselects AGENTS.md");
  Check.that(childPrompt.includes("Only read; never edit files."), "the child renders its own instructions");
} finally {
  await harness?.disposeAsync();
  store.dispose();
  rmSync(repo, { recursive: true });
}
