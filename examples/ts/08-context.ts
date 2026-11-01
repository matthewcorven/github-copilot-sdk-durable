// 08 — Context. LIVE: the recorded runtime cannot fork sessions.
import { Check } from "./check.ts";
import { runExample } from "./example.ts";

await runExample(async (harness) => {
  const transcript = await harness.createConversationAsync();
  console.log(`effective model: ${(await transcript.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  await transcript.commitAsync(tx => {
    tx.appendEntry(transcript.id, "message", "read a and b");
    tx.appendEntry(transcript.id, "message", "I crashed");
    tx.appendEntry(transcript.id, "message", "reading");
    tx.appendEntry(transcript.id, "message", "a and b look fine");
    tx.appendEntry(transcript.id, "note", "display only");
  });
  const reply = await transcript.submitAsync("Say exactly: CONTEXT");
  console.log(`answer: ${reply?.data.content ?? "(no answer)"}`);
  const first = Check.notNull(transcript.entries(10).at(-1), "the transcript has a fork boundary");
  const fork = await harness.forkAsync(transcript, first);
  Check.equal("CONTEXT", reply?.data.content, "the model answered as asked");
  Check.that(transcript.entries(10).length >= 1, "the conversation stored at least one entry");
  const events = await transcript.contextAsync();
  Check.that(events.some(e => e.type === "user.message"), "the model context holds the user message");
  Check.that(events.some(e => e.type === "assistant.message"), "the model context holds the assistant message");
  Check.that(events.some(e => e.type === "system.message"), "the model context holds the system message");
  const forkEntries = fork.entries(10);
  Check.that(forkEntries.length <= transcript.entries(10).length, "the fork holds no more entries than the parent");
  Check.that(forkEntries.includes(first), "the fork inherited the entry it was taken at");
  Check.equal(transcript.id, fork.record.parentConversationId, "the fork records its parent");
});
