// 00 — Conversations. Mirrors every assertion in 00-conversation.cs.
import { Check } from "./check.ts";
import { runExample } from "./example.ts";

await runExample(async (harness, store) => {
  const standalone = await store.commitAsync(tx => tx.createConversation());
  console.log(`standalone conversation: ${standalone.id}`);
  console.log(`  parent: ${standalone.parentConversationId ?? "(none)"}`);
  console.log(`  owner: ${standalone.ownerTaskId ?? "(none)"}`);
  const conversation = await harness.createConversationAsync();
  console.log(`copilot session: ${conversation.sessionId}`);
  console.log(`conversation count: ${store.conversations.length}`);
  console.log(`effective model: ${(await conversation.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  Check.that(standalone.id.length > 0, "a conversation has an id");
  Check.equal(null, standalone.parentConversationId, "a standalone conversation has no parent");
  Check.equal(null, standalone.ownerTaskId, "a standalone conversation has no owner task");
  Check.that(store.conversations.some(c => c.id === standalone.id), "the conversation is in the store");
  Check.that(store.conversations.some(c => c.id === conversation.id), "the live conversation is in the store");
  Check.equal(2, store.conversations.length, "both conversations were created");
  Check.notNull(conversation.sessionId, "the live conversation has a real SDK session");
  const before = store.conversations.length;
  try {
    await store.commitAsync(tx => {
      tx.createConversation();
      tx.createConversation();
      throw new Error("boom");
    });
  } catch (error) {
    if (!(error instanceof Error) || error.message !== "boom") throw error;
  }
  Check.equal(before, store.conversations.length, "a failed transaction writes nothing");
});
