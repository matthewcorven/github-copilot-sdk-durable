// 04 — Watches. Consume the native document watch, not an invented event stream.
import { setTimeout as delay } from "node:timers/promises";
import { DocumentDefinition, DocumentHistory, ForkPolicy, watchDocument } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";

await runExample(async (harness, store) => {
  const notes = new DocumentDefinition("example.notes", DocumentHistory.Rewindable, ForkPolicy.AsOf, () => ({ text: "" }));
  const chat = await harness.createConversationAsync();
  console.log(`effective model: ${(await chat.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  await chat.commitAsync(tx => {
    const draft = tx.doc(notes, chat.id);
    draft.value.text = "first";
    draft.save(tx.appendEntry(chat.id, "note", "first"));
  });
  const seen: string[] = [];
  let delivered!: () => void;
  const completion = new Promise<void>(resolve => { delivered = resolve; });
  const watch = watchDocument(store, notes, chat.id, async frame => {
    await delay(50);
    seen.push(frame.value.text);
    delivered();
  });
  Check.equal("first", watch.baseline.text, "the watch acquisition baseline is stable");
  try {
    await chat.commitAsync(tx => {
      const draft = tx.doc(notes, chat.id);
      draft.value.text = "observed asynchronously";
      draft.save(tx.appendEntry(chat.id, "note", "second"));
    });
    let timer!: ReturnType<typeof setTimeout>;
    try {
      await Promise.race([
        completion,
        new Promise<never>((_, reject) => {
          timer = setTimeout(() => reject(new Error("the watch delivered no frame within five seconds")), 5000);
        }),
      ]);
    } finally {
      clearTimeout(timer);
    }
  } finally {
    watch.close();
  }
  Check.equal("observed asynchronously", chat.snapshot(notes)!.text, "the committed value is what the watch saw");
  Check.that(seen.length >= 1, "the watch delivered at least one update");
  Check.equal("observed asynchronously", seen.at(-1), "the last delivered frame is the committed value");
  Check.that(seen.every(frame => frame === "observed asynchronously"), "the watch only ever delivered committed values");
});
