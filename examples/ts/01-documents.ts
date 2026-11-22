// 01 — Documents. Immutable version history and failed-commit isolation.
import { DocumentDefinition, DocumentHistory, ForkPolicy } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";

await runExample(async (harness, store) => {
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
  const document = Check.notNull(store.document(notes.kind, chat.id), "the notes document exists");
  console.log(`latest notes: ${chat.snapshot(notes)!.text}`);
  console.log(`notes at first entry: ${chat.snapshotAsOf(notes, firstEntry)!.text}`);
  console.log(`notes at second entry: ${chat.snapshotAsOf(notes, secondEntry)!.text}`);
  console.log(`committed versions: ${document.versions.length}`);
  for (const version of document.versions) {
    console.log(`  ${version.entryId}: ${(version.value as { text: string }).text}`);
  }
  Check.equal("after goodbye", chat.snapshot(notes)!.text, "the latest value is the last one written");
  Check.equal("after hello", chat.snapshotAsOf(notes, firstEntry)!.text, "the value as of the first entry is readable");
  Check.equal("after goodbye", chat.snapshotAsOf(notes, secondEntry)!.text, "the value as of the second entry is readable");
  Check.equal(2, document.versions.length, "both committed versions are retained");
  Check.equal(firstEntry, document.versions[0]!.entryId, "the first version belongs to the first entry");
  Check.equal(secondEntry, document.versions[1]!.entryId, "the second version belongs to the second entry");
  const untouched = new DocumentDefinition("example.untouched", DocumentHistory.Latest, ForkPolicy.AsOf, () => ({ text: "" }));
  Check.equal(null, chat.snapshot(untouched), "a document that was never written has no value");
  const before = chat.snapshot(notes)!.text;
  try {
    await chat.commitAsync(tx => {
      const draft = tx.doc(notes, chat.id);
      draft.value.text = "should not survive";
      draft.save(tx.appendEntry(chat.id, "note", "doomed"));
      throw new Error("boom");
    });
  } catch (error) {
    if (!(error instanceof Error) || error.message !== "boom") throw error;
  }
  Check.equal(before, chat.snapshot(notes)!.text, "a failed transaction does not change the document");
  Check.equal(2, store.document(notes.kind, chat.id)!.versions.length, "a failed transaction adds no version");
});
