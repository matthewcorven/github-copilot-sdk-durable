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

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await chat.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

var firstEntry = await chat.CommitAsync(async tx =>
{
    var entry = tx.AppendEntry(chat.Id, "note", "hello");
    var notes = tx.Doc(Notes, chat.Id);
    notes.Value["text"] = "after hello";
    notes.Save(entry);
    return entry;
});

var secondEntry = await chat.CommitAsync(async tx =>
{
    var entry = tx.AppendEntry(chat.Id, "note", "goodbye");
    var notes = tx.Doc(Notes, chat.Id);
    notes.Value["text"] = "after goodbye";
    notes.Save(entry);
    return entry;
});

// A fork is a new conversation that continues from one entry of another. It
// sees the parent's transcript up to that entry, and each document follows its
// own `fork` setting. Notes uses "asOf", so the fork starts with the notes
// value from the fork entry.
var branch = await harness.ForkAsync(chat, firstEntry);

// The fork sees "hello" (inherited from the parent) but not "goodbye", which came later.
Console.WriteLine($"fork transcript: {string.Join(", ", branch.Entries(10).Select(entry => entry))}");
Console.WriteLine($"fork notes: {branch.Snapshot(Notes)!["text"]}");

// The fork starts with the document value as of the fork entry.
var forkNotesAtFork = branch.Snapshot(Notes)!["text"];

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

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// A fork inherits the parent's transcript up to the fork entry, and nothing
// after it.
// Entries() reads newest first, so the fork's own edit comes before the inherited
// entry it was forked at.
var forkEntries = branch.Entries(10);
Check.Equal(2, forkEntries.Count, "the fork holds the inherited entry and its own edit");
Check.Equal(firstEntry, forkEntries[^1], "the inherited entry is the oldest one in the fork");
Check.That(!forkEntries.Contains(secondEntry), "the fork did not inherit entries written after the fork entry");
Check.Equal("after hello", forkNotesAtFork, "the fork starts with the document value as of the fork entry");

// The fork's copy is independent: editing it leaves the parent unchanged.
Check.Equal("changed only in the fork", branch.Snapshot(Notes)!["text"], "the fork holds its own edit");
Check.Equal("after goodbye", chat.Snapshot(Notes)!["text"], "the parent is unchanged by the fork's edit");

// The fork records where it came from.
Check.Equal(chat.Id, branch.Record.ParentConversationId, "the fork records its parent");
Check.Equal(firstEntry, branch.Record.ForkEntryId, "the fork records the entry it was taken at");
Check.That(branch.Id != chat.Id, "the fork is a different conversation");