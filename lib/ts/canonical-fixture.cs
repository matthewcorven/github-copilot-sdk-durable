// Persistence interoperability fixture; uses the canonical implementation, not a port.
#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../Durable.cs

using System.Text.Json;
using Durable;

var file = args[1];
var notes = new DocumentDefinition<string[]>("interop.notes", DocumentHistory.Rewindable, ForkPolicy.AsOf, () => []);
var intents = new DocumentDefinition<ForkIntent>("durable.fork", DocumentHistory.Rewindable, ForkPolicy.Initial);
var bindings = new DocumentDefinition<SessionBinding>("durable.session-binding", DocumentHistory.Rewindable, ForkPolicy.Initial);
DurableStore Open() => DurableStore.Create(new DurableStoreOptions { Path = file, Format = DurableStoreFormat.Jsonl });
if (args[0] == "create")
{
    string parentId, childId, boundary;
    using (var store = Open())
    {
        (parentId, childId, boundary) = await store.CommitAsync(tx =>
        {
            var parent = tx.CreateConversation();
            var entry = tx.AppendEntry(parent.Id, "first");
            var draft = tx.Doc(notes, parent.Id);
            draft.Value = ["first"];
            draft.Save(entry);
            var child = tx.CreateConversation(parentConversationId: parent.Id, forkEntryId: entry);
            var intent = tx.Doc(intents, child.Id);
            intent.Value = new ForkIntent { ParentConversationId = parent.Id, ForkEntryId = entry, State = ForkIntentState.Pending };
            intent.Save(tx.AppendEntry(child.Id, "fork"));
            return Task.FromResult((parent.Id, child.Id, entry));
        });
    }
    // Older writers may repeat definitions. Reuse an actual canonical record and
    // require the canonical reader and both ports to retain all historical versions.
    var definition = File.ReadLines(file).First(line =>
    {
        using var record = JsonDocument.Parse(line);
        return record.RootElement.GetProperty("type").GetString() == "document" &&
            record.RootElement.GetProperty("payload").GetProperty("kind").GetString() == notes.Kind;
    });
    File.AppendAllText(file, definition + "\n");
    using (var store = Open())
    {
        await store.CommitAsync(tx =>
        {
            var draft = tx.Doc(notes, parentId);
            draft.Value = ["second"];
            draft.Save(tx.AppendEntry(parentId, "second"));
            var binding = tx.Doc(bindings, childId);
            binding.Value = new SessionBinding { SessionId = "canonical-child", Pinned = true };
            binding.Save(tx.AppendEntry(childId, "session-binding"));
            var intent = tx.Doc(intents, childId);
            intent.Value.SessionId = "canonical-child";
            intent.Value.State = ForkIntentState.Completed;
            intent.Save(tx.AppendEntry(childId, "fork"));
            return Task.CompletedTask;
        });
    }
}
using (var store = Open())
{
    var document = store.Documents.Single(d => d.Kind == notes.Kind);
    if (document.Versions.Count != 2 ||
        document.Versions[0].Value.Deserialize<string[]>(DurableJson.Options)![0] != "first" ||
        document.Versions[1].Value.Deserialize<string[]>(DurableJson.Options)![0] != "second")
        throw new InvalidOperationException("Canonical document history was lost.");
    var forks = store.Documents.Where(d => d.Kind == intents.Kind).ToArray();
    foreach (var fork in forks)
    {
        if (fork.Latest!.Value.Deserialize<ForkIntent>(DurableJson.Options)!.State != ForkIntentState.Completed)
            throw new InvalidOperationException("Fork intent is not canonically completed.");
        var binding = store.Document(bindings.Kind, fork.OwnerId)?.Latest?.Deserialize<SessionBinding>(DurableJson.Options);
        if (binding is { SessionId: not "canonical-child", Pinned: true })
            throw new InvalidOperationException("Runtime-allocated fork id must not be pinned.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { ownerId = document.OwnerId, forkIds = forks.Select(d => d.OwnerId) }, DurableJson.Options));
}
