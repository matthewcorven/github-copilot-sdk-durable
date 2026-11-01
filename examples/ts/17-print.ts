// 17 - Print mode (LIVE). Awaits this submission rather than global idle.
import { AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, PromptSection } from "../../lib/ts/durable.ts";

const prompt = process.argv[2] ?? "What is in this directory?";
const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
registry.install(new Extension("coding", [], [new PromptSection("preamble", async () => "You are a concise coding assistant.", false)]));
const harness = await DurableHarness.openAsync(store, registry);
try {
  const root = await harness.rootAsync(new AgentChoices({ extensions: ["coding"], permissions: ["read"] }));
  const report = await root.waitForEffectiveModelAsync();
  console.error(`effective model: ${report.model ?? "(not reported)"}`);
  const answer = Check.notNull(await root.submitAsync(prompt), "the model answered");
  Check.that(answer.data.content.trim().length > 0, "the answer is not empty");
  Check.that(root.answers.length === 1, "the turn produced exactly one answer");
  console.log(answer.data.content);
} finally {
  await harness.disposeAsync();
  store.dispose();
}
