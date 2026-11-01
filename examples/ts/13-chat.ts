// 13 - Chat (LIVE). Requires the native SDK runtime and model access.
import { AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, PromptSection } from "../../lib/ts/durable.ts";

const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
registry.install(new Extension("terse", [], [new PromptSection("preamble", async () => "You answer in one word.", false)]));
const harness = await DurableHarness.openAsync(store, registry);
try {
  const root = await harness.rootAsync(new AgentChoices({ extensions: ["terse"] }));
  const report = await root.waitForEffectiveModelAsync();
  console.log(`effective model: ${report.model ?? "(not reported)"}`);
  const answer = await root.submitAsync("Capital of France? Answer in one word.");
  Check.equal("Paris", answer?.data.content.trim(), "the model answered the question");
  Check.equal(0, root.entries(10).length, "a plain chat turn has no app-written entries");
  const events = await root.contextAsync();
  Check.that(events.some(e => e.type === "user.message"), "the transcript holds the user message");
  Check.that(events.some(e => e.type === "assistant.message"), "the transcript holds the assistant message");
  Check.that(events.some(e => e.type === "system.message"), "the transcript holds the system message");
  console.log(`answer: ${answer?.data.content}`);
  for (const event of events) {
    if (event.type === "user.message" || event.type === "assistant.message" || event.type === "system.message") {
      console.log(`  ${event.type}: ${event.data.content}`);
    }
  }
} finally {
  await harness.disposeAsync();
  store.dispose();
}
