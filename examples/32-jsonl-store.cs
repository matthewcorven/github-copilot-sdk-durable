// 32 — The store file.
//
// Where a durable store lives, and how it writes.
//
// The store has two tiers. An ephemeral store keeps everything in the process; a durable
// store writes a file, and that file is what makes a conversation outlive the process.
//
// This example is about the file. An explicit path is honoured exactly. A store opened
// without a path resolves the per-user default location instead, so an app that says nothing
// still gets a durable store. And the file itself is JSONL — one record per line, appended on
// every commit — so a crash mid-write costs the torn tail and nothing else.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;

var directory = Path.Combine(Path.GetTempPath(), $"durable-example-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var storePath = Path.Combine(directory, "state.jsonl");

var notes = new DocumentDefinition<Dictionary<string, string>>("example.notes");

// ─── An explicit path is the file the store writes ──────────────────────────

using (var store = DurableStore.Create(new DurableStoreOptions { Path = storePath }))
{
    Check.That(store.IsPersistent, "an explicit path makes the store persistent");

    await store.CommitAsync(async tx =>
    {
        var conversation = tx.CreateConversation();
        var draft = tx.Doc(notes, conversation.Id);
        draft.Value = new Dictionary<string, string> { ["text"] = "written before the restart" };
        draft.Save(tx.AppendEntry(conversation.Id, "notes.write"));
        await Task.CompletedTask;
    });
}

Check.That(File.Exists(storePath), "the store file exists after the commit");

// The file is JSONL: every line is one record, not one snapshot of the whole store.
var lines = File.ReadAllLines(storePath).Where(line => line.Length > 0).ToList();
Check.That(lines.Count > 1, "the store writes one record per line");
Check.That(
    lines.All(line => line.StartsWith('{') && line.Contains("\"type\"", StringComparison.Ordinal)),
    "every line is a JSONL record envelope");

// ─── Reopening the same path reads back the same state ──────────────────────

using (var reopened = DurableStore.Create(new DurableStoreOptions { Path = storePath }))
{
    var document = Check.NotNull(reopened.Documents.SingleOrDefault(), "the document was read back");
    var value = DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value);
    Check.Equal(
        "written before the restart",
        value!["text"],
        "the reopened store holds the value the first store committed");
}

// ─── A torn tail is discarded, not fatal ────────────────────────────────────

// A process that dies mid-append leaves a partial record with no line terminator.
File.AppendAllText(storePath, "{\"v\":1,\"seq\":9999,\"ty");

using (var recovered = DurableStore.Create(new DurableStoreOptions { Path = storePath }))
{
    Check.Equal(
        1,
        recovered.Documents.Single().Versions.Count,
        "the committed record survived the torn tail");
}

Check.That(
    !File.ReadAllText(storePath).Contains("\"seq\":9999", StringComparison.Ordinal),
    "the torn tail was truncated away at open");

// ─── No path means the per-user default location ────────────────────────────

// The default lives under a per-user state directory, not in the current directory. The
// example only resolves it: writing there would leave a file behind in the user's home.
var defaultPath = DurableStore.DefaultStorePath();
var defaultDirectory = Path.GetDirectoryName(defaultPath)!;

Check.Equal("durable-store.jsonl", Path.GetFileName(defaultPath), "the default file name is stable");
Check.Equal("durable", Path.GetFileName(defaultDirectory), "the default file sits in a durable directory");
Check.That(
    Path.IsPathRooted(defaultPath),
    "the default path is absolute, so it does not depend on the working directory");

// ─── The ephemeral tier touches no file at all ──────────────────────────────

using (var ephemeral = DurableStore.CreateEphemeral())
{
    Check.That(!ephemeral.IsPersistent, "an ephemeral store persists nothing");
}

Console.WriteLine($"explicit store file: {storePath}");
Console.WriteLine($"default store file:  {defaultPath}");
Console.WriteLine($"tiers: durable writes {lines.Count} JSONL record(s); ephemeral writes none");

Directory.Delete(directory, recursive: true);

// Mirrors the Pi Durable example `32-jsonl-store.ts`: the store file is JSONL, an explicit
// path wins, and a store with no path falls back to the per-user default.
