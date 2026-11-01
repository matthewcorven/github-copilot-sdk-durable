// 05 — The harness. Stored names resolve against the live registry.
import { AgentChoices, DocumentDefinition, DocumentHistory, Extension, ForkPolicy } from "../../lib/ts/durable.ts";
import { Check } from "./check.ts";
import { runExample } from "./example.ts";
import { fileTool } from "./tools.ts";

await runExample(async (harness, _store, registry) => {
  registry.install(new Extension("files", [fileTool("read", "Read a file")]));
  const notes = new DocumentDefinition("example.notes", DocumentHistory.Rewindable, ForkPolicy.AsOf, () => ({ text: "" }));
  const root = await harness.rootAsync(new AgentChoices({
    extensions: ["files"], reasoningEffort: "low", permissions: ["custom-tool:read"],
  }));
  await root.commitAsync(tx => {
    const draft = tx.doc(notes, root.id);
    draft.value.text = "root notes";
    draft.save(tx.appendEntry(root.id, "note", "root notes"));
  });
  console.log(`effective model: ${(await root.waitForEffectiveModelAsync()).model ?? "(not reported)"}`);
  const agent = root.agent();
  const again = await harness.rootAsync(new AgentChoices({ reasoningEffort: "high" }));
  Check.equal("low", root.choices()!.reasoningEffort, "the stored reasoning effort is the one that was configured");
  Check.equal("low", agent.reasoningEffort, "the resolved agent carries the stored reasoning effort");
  Check.sequence(["files"], agent.extensions.map(e => e.name), "the resolved agent carries the selected extension");
  Check.sequence(["read"], agent.tools.map(t => t.name), "the resolved agent carries the selected tool");
  Check.equal(root.id, again.id, "the root conversation is stable across calls");
  Check.equal("low", root.choices()!.reasoningEffort, "a second RootAsync() does not overwrite the stored choices");
  Check.equal("root notes", root.snapshot(notes)!.text, "the committed document value is readable");
});
