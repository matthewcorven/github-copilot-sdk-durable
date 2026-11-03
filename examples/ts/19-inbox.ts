// 19 - The inbox (LIVE). Overlapping submissions retain independent answers.
import { Check, DurableHarness, DurableStore, ExtensionRegistry } from "../../lib/ts/durable.ts";

const store = DurableStore.createEphemeral();
const harness = await DurableHarness.openAsync(store, new ExtensionRegistry());
try {
  const root = await harness.rootAsync();
  const report = await root.waitForEffectiveModelAsync();
  console.log(`effective model: ${report.model ?? "(not reported)"}`);
  const first = root.submitAsync("First question: what is 2+2? Answer with just the number.");
  const followUp = root.submitAsync("A follow-up: what is 3+3? Answer with just the number.");
  const [firstAnswer, followUpAnswer] = await Promise.all([first, followUp]);
  const context = await root.contextAsync();
  const messages = context.filter(e => e.type === "user.message").map(e => e.data.content);
  Check.equal(2, messages.length, "both prompts are in the transcript");
  Check.that(messages.some(m => m.includes("2+2")), "the first prompt is in the transcript");
  Check.that(messages.some(m => m.includes("3+3")), "the queued prompt is in the transcript");
  const answeredFirst = Check.notNull(firstAnswer, "the first prompt was answered");
  const answeredFollowUp = Check.notNull(followUpAnswer, "the queued prompt was answered");
  Check.that(answeredFirst.data.content.trim().length > 0, "the first prompt was answered");
  Check.that(answeredFollowUp.data.content.trim().length > 0, "the queued prompt was answered");
  Check.that(root.answers.length === 2, "the conversation produced one answer per prompt");
  console.log(`first: ${answeredFirst.data.content}\nfollow-up: ${answeredFollowUp.data.content}`);
} finally {
  await harness.disposeAsync();
  store.dispose();
}
