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

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// A rewindable document keeps every committed version, and each version can be
// read back as of the entry that wrote it.
Check.Equal("after goodbye", chat.Snapshot(Notes)!["text"], "the latest value is the last one written");
Check.Equal("after hello", chat.SnapshotAsOf(Notes, firstEntry)!["text"], "the value as of the first entry is readable");
Check.Equal("after goodbye", chat.SnapshotAsOf(Notes, secondEntry)!["text"], "the value as of the second entry is readable");
Check.Equal(2, document.Versions.Count, "both committed versions are retained");
Check.Equal(firstEntry, document.Versions[0].EntryId, "the first version belongs to the first entry");
Check.Equal(secondEntry, document.Versions[1].EntryId, "the second version belongs to the second entry");

// A document that was never written has no value at all.
var untouched = new DocumentDefinition<Dictionary<string, string>>(
    "example.untouched",
    DocumentHistory.Latest,
    ForkPolicy.AsOf,
    () => new Dictionary<string, string> { ["text"] = "" });
Check.That(chat.Snapshot(untouched) is null, "a document that was never written has no value");

// A failed transaction leaves the document exactly as it was.
var before = chat.Snapshot(Notes)!["text"];
try
{
    await chat.CommitAsync(async tx =>
    {
        var notes = tx.Doc(Notes, chat.Id);
        notes.Value["text"] = "should not survive";
        notes.Save(tx.AppendEntry(chat.Id, "note", "doomed"));
        throw new InvalidOperationException("boom");
    });
}
catch (InvalidOperationException ex) when (ex.Message == "boom")
{
    // Expected.
}

Check.Equal(before, chat.Snapshot(Notes)!["text"], "a failed transaction does not change the document");
Check.Equal(2, store.Document(Notes.Kind, chat.Id)!.Versions.Count, "a failed transaction adds no version");