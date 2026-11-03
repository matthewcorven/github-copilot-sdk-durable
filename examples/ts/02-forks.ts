// 02 — Forks. Native SDK forking is required, not a replacement fresh session.
import { DocumentDefinition, DocumentHistory, ForkPolicy } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";

await runExample(async (harness) => {
  const notes = new DocumentDefinition("example.notes", DocumentHistory.Rewindable, ForkPolicy.AsOf, () => ({ text: "" }));
  const chat = await harness.createConversationAsync();
  console.log(`effective model: ${(await chat.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  const firstEntry = await chat.commitAsync(tx => {
    const entry = tx.appendEntry(chat.id, "note", "hello");
    const draft = tx.doc(notes, chat.id);
    draft.value.text = "after hello";
    draft.save(entry);
    return entry;
  });
  const secondEntry = await chat.commitAsync(tx => {
    const entry = tx.appendEntry(chat.id, "note", "goodbye");
    const draft = tx.doc(notes, chat.id);
    draft.value.text = "after goodbye";
    draft.save(entry);
    return entry;
  });
  const branch = await harness.forkAsync(chat, firstEntry);
  console.log(`fork transcript: ${branch.entries(10).join(", ")}`);
  console.log(`fork notes: ${branch.snapshot(notes)!.text}`);
  const forkNotesAtFork = branch.snapshot(notes)!.text;
  await branch.commitAsync(tx => {
    const draft = tx.doc(notes, branch.id);
    draft.value.text = "changed only in the fork";
    draft.save(tx.appendEntry(branch.id, "note", "edit"));
  });
  console.log(`fork notes after edit: ${branch.snapshot(notes)!.text}`);
  console.log(`parent notes after edit: ${chat.snapshot(notes)!.text}`);
  console.log(`fork parent: ${branch.record.parentConversationId}`);
  console.log(`fork entry: ${branch.record.forkEntryId}`);
  const forkEntries = branch.entries(10);
  Check.equal(2, forkEntries.length, "the fork holds the inherited entry and its own edit");
  Check.equal(firstEntry, forkEntries.at(-1), "the inherited entry is the oldest one in the fork");
  Check.that(!forkEntries.includes(secondEntry), "the fork did not inherit entries written after the fork entry");
  Check.equal("after hello", forkNotesAtFork, "the fork starts with the document value as of the fork entry");
  Check.equal("changed only in the fork", branch.snapshot(notes)!.text, "the fork holds its own edit");
  Check.equal("after goodbye", chat.snapshot(notes)!.text, "the parent is unchanged by the fork's edit");
  Check.equal(chat.id, branch.record.parentConversationId, "the fork records its parent");
  Check.equal(firstEntry, branch.record.forkEntryId, "the fork records the entry it was taken at");
  Check.that(branch.id !== chat.id, "the fork is a different conversation");
});
