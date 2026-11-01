// 07 — Harness conversations. LIVE: models.list reasoning-effort resolution.
import { AgentChoices } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";

await runExample(async (harness, store) => {
  const root = await harness.rootAsync(new AgentChoices({ model: "gpt-6-luna", reasoningEffort: "high" }));
  const hello = await root.commitAsync(tx => tx.appendEntry(root.id, "message", "hello"));
  console.log(`effective model: ${(await root.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  const helper = await harness.createConversationAsync(new AgentChoices({ model: "mai-code-1.1-flash", reasoningEffort: "minimal" }));
  const retry = await harness.forkAsync(root, hello);
  Check.equal("mai-code-1.1-flash", helper.agent().model, "the created conversation uses its own model");
  Check.equal("minimal", helper.agent().reasoningEffort, "the created conversation uses its own reasoning effort");
  Check.equal(root.agent().model, retry.agent().model, "the fork inherits the parent's model");
  Check.equal(root.agent().reasoningEffort, retry.agent().reasoningEffort, "the fork inherits the parent's reasoning effort");
  Check.notNull(store.conversation(retry.id), "the fork is in the store");
  Check.that(retry.id !== root.id, "the fork is a different conversation");
  Check.equal(root.id, retry.record.parentConversationId, "the fork records its parent");
  Check.notNull(root.sessionId, "the root has a native session");
  Check.notNull(helper.sessionId, "the helper has a native session");
  Check.notNull(retry.sessionId, "the fork has a native session");
  Check.that(root.sessionId !== helper.sessionId, "each conversation has its own session");
  Check.that(root.sessionId !== retry.sessionId, "the fork has its own session");
  Check.that(retry.entries(10).includes(hello), "the fork inherited the fork entry");
  Check.equal(root.entries(10).length, retry.entries(10).length, "the fork inherited the parent's entries");
});
