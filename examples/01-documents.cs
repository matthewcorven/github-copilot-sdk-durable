// 01 — Documents.
//
// Store document state next to transcript entries.
//
// A document is a JSON object attached to a conversation. "Rewindable" keeps old values
// readable, so you can ask what the document looked like when a particular entry was written.
// The `fork` policy says what a forked copy of the conversation starts with.
//
// Mirrors the Pi Durable example `01-documents.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

var store = new DurableStore();
var registry = new ExtensionRegistry();
await using var harness = await DurableHarness.OpenAsync(store, registry);

// A document is a JSON object attached to something; here, one per conversation.
// "rewindable" keeps old values readable, so you can ask what the document
// looked like when a particular entry was written.
// `fork` says what a forked copy of the conversation starts with (see 02-forks.cs).
var Notes = new DocumentDefinition<Dictionary<string, string>>(
    "example.notes",
    DocumentHistory.Rewindable,
    ForkPolicy.AsOf,
    () => new Dictionary<string, string> { ["text"] = "" });

var chat = await harness.CreateConversationAsync();

// tx.Doc() returns an editable copy of the document (created on first use).
// Plain assignments to it are saved when the commit finishes.
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

// Snapshot() reads the latest value. SnapshotAsOf() reads the value that was
// saved in the same commit as the given entry.
Console.WriteLine($"latest notes: {chat.Snapshot(Notes)!["text"]}");
Console.WriteLine($"notes at first entry: {chat.SnapshotAsOf(Notes, firstEntry)!["text"]}");
Console.WriteLine($"notes at second entry: {chat.SnapshotAsOf(Notes, secondEntry)!["text"]}");

// The document's history is retained, so every committed version is readable.
var document = store.Document(Notes.Kind, chat.Id)!;
Console.WriteLine($"committed versions: {document.Versions.Count}");
foreach (var version in document.Versions)
{
    Console.WriteLine($"  {version.EntryId}: {version.Value.GetProperty("text").GetString()}");
}