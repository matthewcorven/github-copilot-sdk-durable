// Infinite-session context management; compaction is observed, not promised.
import assert from "node:assert/strict";
import {
  AgentChoices, DurableHarness, DurableStore, ExtensionRegistry,
} from "../../lib/ts/durable.ts";

async function main() {
  const store = DurableStore.createEphemeral();
  const harness = await DurableHarness.openAsync(store, new ExtensionRegistry());
  try {
    const root = await harness.rootAsync(new AgentChoices({
      infiniteSessions: {
        enabled: true,
        backgroundCompactionThreshold: 0.80,
        bufferExhaustionThreshold: 0.95,
      },
    }));
    const effective = await root.waitForEffectiveModelAsync();
    console.log(`effective model: ${effective.model ?? "(not reported)"}`);
    const compactions: string[] = [];
    const unsubscribe = root.session.on((event) => {
      if (event.type === "session.compaction_start") {
        compactions.push("start");
        console.log("compaction started");
      } else if (event.type === "session.compaction_complete") {
        compactions.push("complete");
        console.log(`compaction complete: ${JSON.stringify(event.data)}`);
      }
    });
    try {
      const answers = [];
      for (const question of [
        "Where should we stay in Lisbon?", "What should we eat?",
        "Which day trips are worth it?", "Any museums?", "Nightlife?",
      ]) {
        const answer = await root.submitAsync(question);
        answers.push(answer);
        console.log(`asked: ${question}`);
        console.log(`  answered: ${answer?.data.content.length ?? 0} chars`);
      }
      const stored = root.entries(100);
      const context = await root.contextAsync();
      console.log(`stored entries: ${stored.length}`);
      console.log(`context events: ${context.length}`);
      console.log(`compactions observed: ${compactions.length}`);
      for (const event of context) {
        if (event.type === "session.usage_checkpoint") {
          console.log(`usage checkpoint: ${JSON.stringify(event.data)}`);
        }
      }
      assert.equal(answers.length, 5, "every question was answered");
      assert.ok(answers.every((answer) => answer !== undefined && answer.data.content.trim().length > 0),
        "no answer was empty");
      assert.equal(stored.length, 0, "plain chat checkpoints are not public app entries");
      assert.ok(context.length > 0, "the model context is not empty");
    } finally {
      unsubscribe();
    }
  } finally {
    await harness.disposeAsync();
    store.dispose();
  }
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
