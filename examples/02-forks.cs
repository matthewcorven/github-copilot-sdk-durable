// 02 — Forks.
//
// Fork a conversation.
//
// A fork is a new conversation that continues from one entry of another. It sees the parent's
// transcript up to that entry, and each document follows its own `fork` setting. The fork's
// copy is independent: editing it leaves the parent unchanged.
//
// Mirrors the Pi Durable example `02-forks.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);

var Notes = new DocumentDefinition<Dictionary<string, string>>(
    "example.notes",
    DocumentHistory.Rewindable,
    ForkPolicy.AsOf,
    () => new Dictionary<string, string> { ["text"] = "" });

var chat = await harness.CreateConversationAsync();

var firstEntry = await chat.CommitAsync(async tx =>
{
    var entry = tx.AppendEntry(chat.Id, "note", "hello");
    var notes = tx.Doc(Notes, chat.Id);
    notes.Value["text"] = "after hello";
    notes.Save(entry);
    return entry;
});

await chat.CommitAsync(async tx =>
{
    var entry = tx.AppendEntry(chat.Id, "note", "goodbye");
    var notes = tx.Doc(Notes, chat.Id);
    notes.Value["text"] = "after goodbye";
    notes.Save(entry);
});

// A fork is a new conversation that continues from one entry of another. It
// sees the parent's transcript up to that entry, and each document follows its
// own `fork` setting. Notes uses "asOf", so the fork starts with the notes
// value from the fork entry.
var branch = await harness.ForkAsync(chat, firstEntry);

// The fork sees "hello" (inherited from the parent) but not "goodbye", which came later.
Console.WriteLine($"fork transcript: {string.Join(", ", branch.Entries(10).Select(entry => entry))}");
Console.WriteLine($"fork notes: {branch.Snapshot(Notes)!["text"]}");

// The fork's copy is independent: editing it leaves the parent unchanged.
await branch.CommitAsync(async tx =>
{
    var notes = tx.Doc(Notes, branch.Id);
    notes.Value["text"] = "changed only in the fork";
    notes.Save(tx.AppendEntry(branch.Id, "note", "edit"));
});

Console.WriteLine($"fork notes after edit: {branch.Snapshot(Notes)!["text"]}");
Console.WriteLine($"parent notes after edit: {chat.Snapshot(Notes)!["text"]}");

// The fork records where it came from.
Console.WriteLine($"fork parent: {branch.Record.ParentConversationId}");
Console.WriteLine($"fork entry: {branch.Record.ForkEntryId}");