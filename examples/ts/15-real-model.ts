// 15 - A real model (LIVE). Verifies all streamed deltas against the final message.
import { AgentChoices, Check, DurableHarness, DurableStore, Extension, ExtensionRegistry, PromptSection } from "../../lib/ts/durable.ts";

const store = DurableStore.createEphemeral();
const registry = new ExtensionRegistry();
registry.install(new Extension("concise", [], [new PromptSection("preamble", async () => "You are a concise assistant.", false)]));
const harness = await DurableHarness.openAsync(store, registry);
let unsubscribe: (() => void) | undefined;
try {
  const root = await harness.rootAsync(new AgentChoices({ extensions: ["concise"], streaming: true }));
  const report = await root.waitForEffectiveModelAsync();
  console.log(`effective model: ${report.model ?? "(not reported)"}`);
  let printed = 0;
  unsubscribe = root.session.on(event => {
    if (event.type === "assistant.message_delta" && event.data.deltaContent) {
      process.stdout.write(event.data.deltaContent);
      printed += event.data.deltaContent.length;
    }
  });
  process.stdout.write("answer: ");
  const answer = Check.notNull(await root.submitAsync("In one sentence, what makes a durable agent different from a stateless one?"), "the model answered");
  console.log();
  Check.that(printed > 0, "the answer streamed as deltas");
  Check.equal(answer.data.content.length, printed, "the streamed deltas add up to the final message");
  Check.that(answer.data.content.length > 0, "the final message is not empty");
  console.log(`streamed ${printed} chars; final message: ${answer.data.content}`);
} finally {
  unsubscribe?.();
  await harness.disposeAsync();
  store.dispose();
}
