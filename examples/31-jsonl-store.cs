// 31 — The JSONL store.
//
// The durable tier of the store is a JSONL file: one JSON record per line, appended on
// every commit and flushed before the commit returns. The store lives where the caller
// points it, or at the per-user default path when it is opened without a path.
//
// The example demonstrates both. First an explicit path: commit a document, close, reopen,
// and read the same value back. Then the default path: open a store with only a file name,
// and it resolves under the documented per-user state directory. A unique file name and a
// cleanup keep the demonstration from touching any real state.
//
// There is no numbered Pi Durable example to mirror here; the JSONL store is the C#
// analogue of Pi Durable's `JsonlStorage` backend.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using System.Text.Json;

var Notes = new DocumentDefinition<string>(
    "example.notes",
    history: DocumentHistory.Rewindable,
    initial: () => "");

// ─── An explicit path ───────────────────────────────────────────────────────
//
// The caller chooses where the store lives. A new file is written in the canonical
// JSONL format; one JSON record per line, appended on every commit.
var directory = Path.Combine(Path.GetTempPath(), $"durable-example-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var storePath = Path.Combine(directory, "store.jsonl");

var explicitStore = DurableStore.Create(new DurableStoreOptions
{
    Path = storePath,
    Format = DurableStoreFormat.Jsonl,
});
Console.WriteLine($"store at {storePath}");
Console.WriteLine($"  persistent: {explicitStore.IsPersistent}");

// Write a document inside a transaction: create the conversation that owns it, append
// the entry, and save the document value committed with that entry.
var explicitValue = "written at the explicit path";
var explicitOwner = await explicitStore.CommitAsync(tx =>
{
    var conversation = tx.CreateConversation();
    var draft = tx.Doc(Notes, conversation.Id);
    draft.Value = explicitValue;
    draft.Save(tx.AppendEntry(conversation.Id, "notes"));
    return Task.FromResult(conversation.Id);
});

// The commit is on disk before it returns: the JSONL file exists at the chosen location,
// and its lines are JSONL records -- JSON objects with a record type.
Check.That(File.Exists(storePath), "the JSONL file exists at the chosen location");
var firstLine = File.ReadLines(storePath).First();
using (var line = JsonDocument.Parse(firstLine))
{
    Check.That(
        line.RootElement.ValueKind == JsonValueKind.Object && line.RootElement.TryGetProperty("type", out _),
        "each line of the store file is a JSONL record with a type");
}
Console.WriteLine($"  records on disk: {File.ReadLines(storePath).Count()}");

// Reopen the same file in a fresh store. The records replay from the file, so the
// document comes back with the value that was committed.
var reopened = DurableStore.Create(new DurableStoreOptions
{
    Path = storePath,
    Format = DurableStoreFormat.Jsonl,
});
var reopenedValue = reopened.Document(Notes.Kind, explicitOwner)!.Latest!.Value.GetString();
Console.WriteLine($"  reopened value: {reopenedValue}");
Check.Equal(explicitValue, reopenedValue, "a reopened store returns the committed document value");

// ─── The default path ───────────────────────────────────────────────────────
//
// A persistent store opened without a path lands on the per-user default: the file name
// under the `durable` directory of the user's state root (LocalApplicationData on
// Windows; $XDG_STATE_HOME, else ~/.local/state, elsewhere).
//
// The demonstration uses a unique file name and deletes it afterwards, so it exercises
// the real default directory without leaving anything behind in it.
var fileName = $"jsonl-store-example-{Guid.NewGuid():N}.jsonl";
var defaultPath = DurableStore.DefaultStorePath(fileName);
Console.WriteLine($"default path resolves to {defaultPath}");

var defaultStore = DurableStore.Create(new DurableStoreOptions { FileName = fileName });
Check.Equal(
    Path.Combine("durable", fileName),
    Path.Combine(Path.GetFileName(Path.GetDirectoryName(defaultPath)!), Path.GetFileName(defaultPath)),
    "the default path places the store file in the durable directory of the per-user state root");

var defaultValue = "written at the default path";
var defaultOwner = await defaultStore.CommitAsync(tx =>
{
    var conversation = tx.CreateConversation();
    var draft = tx.Doc(Notes, conversation.Id);
    draft.Value = defaultValue;
    draft.Save(tx.AppendEntry(conversation.Id, "notes"));
    return Task.FromResult(conversation.Id);
});

// The resolved path is where the store actually wrote, and a store opened again with
// the same file name resolves to the same file and reads the same value back.
Check.That(File.Exists(defaultPath), "the store opened without a path wrote to the resolved default path");
var defaultReopened = DurableStore.Create(new DurableStoreOptions { FileName = fileName });
var defaultReopenedValue = defaultReopened.Document(Notes.Kind, defaultOwner)!.Latest!.Value.GetString();
Check.Equal(defaultValue, defaultReopenedValue, "a store reopened at the default path returns the committed value");
Console.WriteLine($"  reopened value: {defaultReopenedValue}");

// For contrast, the ephemeral tier: an in-memory store never touches the filesystem.
Check.That(defaultStore.IsPersistent, "a store opened without a path is on the durable tier");
Check.That(!DurableStore.CreateEphemeral().IsPersistent, "CreateEphemeral is the ephemeral tier");

File.Delete(defaultPath);
Directory.Delete(directory, recursive: true);
