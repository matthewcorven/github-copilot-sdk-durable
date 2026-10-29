using System.Text.Json;
using Durable.EfCore;
using Microsoft.EntityFrameworkCore;

namespace Durable.EfCore.Tests;

/// <summary>
/// The behavioural contract every provider has to satisfy, run against the EF Core
/// provider. These are the same behaviours <c>tests/Durable.Tests/DurableStoreTests.cs</c>
/// proves for the JSONL provider, so a difference between the two backends fails here.
/// </summary>
public sealed class EfCoreStoreProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"durable-efcore-provider-{Guid.NewGuid():N}");

    public EfCoreStoreProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string StorePath(string name = "state.db") => Path.Combine(_directory, name);

    private static DurableStore CreateEfCoreStore(string path) =>
        DurableStore.Create(new DurableStoreOptions
        {
            Path = path,
            Provider = options => new EfCoreStoreProvider(),
        });

    private static readonly DocumentDefinition<Dictionary<string, string>> Notes = new("notes");

    [Fact]
    public async Task Provider_Persists_And_RoundTrips_Every_Member_Of_The_Store_State()
    {
        var path = StorePath();
        string conversationId;
        string taskId;

        using (var first = CreateEfCoreStore(path))
        {
            Assert.True(first.IsPersistent);

            (conversationId, taskId) = await first.CommitAsync(async transaction =>
            {
                var conversation = transaction.CreateConversation();

                var draft = transaction.Doc(Notes, conversation.Id);
                draft.Value = new Dictionary<string, string> { ["text"] = "first" };
                draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));

                var task = transaction.CreateTask(
                    DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
                        "efcore.task",
                        () => new Dictionary<string, string> { ["phase"] = "run" },
                        (_, _, runtime) => runtime.CommitAsync(
                            () => TaskCommit.Completed(DurableJson.ToElement("done")))),
                    DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }),
                    conversation.Id);

                await Task.CompletedTask;
                return (conversation.Id, task.Id);
            });

            await first.CommitAsync(async transaction =>
            {
                var draft = transaction.Doc(Notes, conversationId);
                draft.Value = new Dictionary<string, string> { ["text"] = "second" };
                draft.Save(transaction.AppendEntry(conversationId, "notes.write"));
                await Task.CompletedTask;
            });
        }

        using var second = CreateEfCoreStore(path);

        var conversation = second.Conversation(conversationId);
        Assert.NotNull(conversation);
        Assert.Single(second.Conversations);

        var task = second.Task(taskId);
        Assert.NotNull(task);
        Assert.Equal("efcore.task", task!.Name);
        Assert.Equal(TaskStatus.Pending, task.Status);
        Assert.Equal(conversationId, task.OwnerConversationId);
        Assert.Single(second.Tasks);

        var document = second.Document("notes", conversationId);
        Assert.NotNull(document);
        Assert.Equal(2, document!.Versions.Count);
        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "second" },
            DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value));

        var entries = second.Entries(conversationId);
        Assert.Equal(2, entries.Count);
        Assert.Equal(document.Versions.Select(version => version.EntryId), entries);
    }

    [Fact]
    public async Task FailedTransaction_RollsBackAllWrites()
    {
        var path = StorePath();
        using var store = CreateEfCoreStore(path);

        var beforeConversations = store.Conversations.Count;
        var beforeDocuments = store.Documents.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await store.CommitAsync(async transaction =>
            {
                var conversation = transaction.CreateConversation();
                var draft = transaction.Doc(Notes, conversation.Id);
                draft.Value = new Dictionary<string, string> { ["text"] = "discarded" };
                draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
                await Task.CompletedTask;
                throw new InvalidOperationException("deterministic failure mid-commit");
            });
        });

        Assert.Equal(beforeConversations, store.Conversations.Count);
        Assert.Equal(beforeDocuments, store.Documents.Count);
    }

    [Fact]
    public async Task RolledBack_Writes_Are_Not_Persisted()
    {
        var path = StorePath();

        using (var store = CreateEfCoreStore(path))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await store.CommitAsync(async transaction =>
                {
                    var conversation = transaction.CreateConversation();
                    var draft = transaction.Doc(Notes, conversation.Id);
                    draft.Value = new Dictionary<string, string> { ["text"] = "discarded" };
                    draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
                    await Task.CompletedTask;
                    throw new InvalidOperationException("deterministic failure mid-commit");
                });
            });
        }

        using var reopened = CreateEfCoreStore(path);
        Assert.Empty(reopened.Conversations);
        Assert.Empty(reopened.Documents);
        Assert.Empty(reopened.Entries("c1"));
    }

    [Fact]
    public async Task Reopen_MintsSequenceGreaterThanEveryReplayedOne()
    {
        var path = StorePath();
        long highestBefore;

        using (var first = CreateEfCoreStore(path))
        {
            await first.CommitAsync(async transaction =>
            {
                var draft = transaction.Doc(Notes, "owner");
                draft.Value = new Dictionary<string, string> { ["text"] = "one" };
                draft.Save("e1");
                await Task.CompletedTask;
            });

            await first.CommitAsync(async transaction =>
            {
                var draft = transaction.Doc(Notes, "owner");
                draft.Value = new Dictionary<string, string> { ["text"] = "two" };
                draft.Save("e2");
                await Task.CompletedTask;
            });

            highestBefore = first.Documents
                .SelectMany(document => document.Versions)
                .Max(version => version.Sequence);
        }

        using var second = CreateEfCoreStore(path);

        // A replayed version keeps the sequence it was minted with.
        var replayed = second.Documents
            .SelectMany(document => document.Versions)
            .Select(version => version.Sequence)
            .ToList();
        Assert.Equal(2, replayed.Count);
        Assert.Equal(highestBefore, replayed.Max());
        Assert.Equal(replayed.Count, replayed.Distinct().Count());

        await second.CommitAsync(async transaction =>
        {
            var draft = transaction.Doc(Notes, "owner");
            draft.Value = new Dictionary<string, string> { ["text"] = "three" };
            draft.Save("e3");
            await Task.CompletedTask;
        });

        var mintedAfterReopen = second.Documents
            .SelectMany(document => document.Versions)
            .Max(version => version.Sequence);

        Assert.True(
            mintedAfterReopen > highestBefore,
            $"a reopened store must mint past every replayed sequence: {mintedAfterReopen} <= {highestBefore}");
    }

    [Fact]
    public async Task Fork_InheritsOnlyEntriesUpToTheForkPoint_AndHasIndependentState()
    {
        var path = StorePath();
        string parentId;
        string entry1;
        string entry2;

        using (var first = CreateEfCoreStore(path))
        {
            parentId = await first.CommitAsync(async transaction =>
            {
                var conversation = transaction.CreateConversation();

                var draft = transaction.Doc(Notes, conversation.Id);
                draft.Value = new Dictionary<string, string> { ["text"] = "v1" };
                draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));

                var draft2 = transaction.Doc(Notes, conversation.Id);
                draft2.Value = new Dictionary<string, string> { ["text"] = "v2" };
                draft2.Save(transaction.AppendEntry(conversation.Id, "notes.write"));

                await Task.CompletedTask;
                return conversation.Id;
            });
        }

        ForkRecord fork;
        using (var second = CreateEfCoreStore(path))
        {
            var entries = second.Entries(parentId);
            entry1 = entries[0];
            entry2 = entries[1];

            // The fork work the harness does, without a live Copilot session behind it.
            fork = await second.CommitAsync(async transaction =>
            {
                var created = transaction.CreateConversation(
                    parentConversationId: parentId,
                    forkEntryId: entry1);

                second.Provider.CopyEntries(parentId, created.Id, entry1);
                foreach (var document in second.Documents.Where(d => d.OwnerId == parentId).ToList())
                {
                    second.Provider.CopyDocument(document, created.Id, entry1);
                }

                await Task.CompletedTask;
                return new ForkRecord(created.Id, created.ParentConversationId, created.ForkEntryId);
            });
        }

        using var reopened = CreateEfCoreStore(path);

        var thread = reopened.Conversation(fork.Id);
        Assert.NotNull(thread);
        Assert.Equal(parentId, fork.ParentConversationId);
        Assert.Equal(entry1, fork.ForkEntryId);

        // The fork inherits its parent's transcript up to the fork point, and no further.
        Assert.Equal([entry1], reopened.Entries(fork.Id));
        Assert.Equal([entry1, entry2], reopened.Entries(parentId));

        // The fork's document is its own: as-of the fork point, then diverges.
        var forkDocument = reopened.Document("notes", fork.Id);
        Assert.NotNull(forkDocument);
        Assert.Single(forkDocument!.Versions);
        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "v1" },
            DurableJson.FromElement<Dictionary<string, string>>(forkDocument.Latest!.Value));

        await reopened.CommitAsync(async transaction =>
        {
            var draft = transaction.Doc(Notes, fork.Id);
            draft.Value = new Dictionary<string, string> { ["text"] = "fork" };
            draft.Save(transaction.AppendEntry(fork.Id, "notes.write"));
            await Task.CompletedTask;
        });

        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "v2" },
            DurableJson.FromElement<Dictionary<string, string>>(
                reopened.Document("notes", parentId)!.Latest!.Value));
        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "fork" },
            DurableJson.FromElement<Dictionary<string, string>>(
                reopened.Document("notes", fork.Id)!.Latest!.Value));
    }

    [Fact]
    public async Task Task_Checkpoint_And_Memos_Survive_Reopen()
    {
        var path = StorePath();
        string taskId;

        using (var first = CreateEfCoreStore(path))
        {
            taskId = await CreateTaskAsync(first, "checkpoint.task");

            await UpdateTaskAsync(first, taskId, record =>
            {
                record.Checkpoint = DurableJson.ToElement(new Dictionary<string, string>
                {
                    ["phase"] = "next",
                    ["n"] = "2",
                });
                record.Status = TaskStatus.Running;
                record.Memos["effect-1"] = DurableJson.ToElement("once");
            });
        }

        using var reopened = CreateEfCoreStore(path);
        var task = reopened.Task(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Running, task!.Status);
        Assert.Equal("next", task.Checkpoint.GetProperty("phase").GetString());
        Assert.True(task.Memos.ContainsKey("effect-1"));
        Assert.Equal("once", task.Memos["effect-1"].GetString());
    }

    [Fact]
    public async Task Reopened_Store_Keeps_The_Checkpoint_And_The_Memos_It_Replayed()
    {
        var path = StorePath();
        string taskId;

        using (var first = CreateEfCoreStore(path))
        {
            taskId = await CreateTaskAsync(first, "counter.task");

            await UpdateTaskAsync(first, taskId, record =>
            {
                record.Memos["done-1"] = DurableJson.ToElement(true);
                record.Checkpoint = DurableJson.ToElement(new Dictionary<string, string>
                {
                    ["phase"] = "step",
                    ["n"] = "2",
                });
                record.Status = TaskStatus.Running;
            });
        }

        // The completion for step 1 is memoized, so a resumed run does not repeat it.
        using var second = CreateEfCoreStore(path);
        var replayed = second.Task(taskId);
        Assert.NotNull(replayed);
        Assert.Equal("2", replayed!.Checkpoint.GetProperty("n").GetString());
        Assert.True(replayed.Memos.ContainsKey("done-1"));
        Assert.False(replayed.Memos.ContainsKey("done-2"));
    }

    [Fact]
    public async Task Task_Abort_Is_Persisted_With_Its_Outcome()
    {
        var path = StorePath();
        string taskId;

        using (var first = CreateEfCoreStore(path))
        {
            taskId = await CreateTaskAsync(first, "abort.task");

            await UpdateTaskAsync(first, taskId, record =>
            {
                record.Status = TaskStatus.Terminal;
                record.Outcome = DurableJson.ToElement(new TaskOutcome("aborted"));
            });
        }

        using var reopened = CreateEfCoreStore(path);
        var replayed = reopened.Task(taskId);
        Assert.NotNull(replayed);
        Assert.Equal(TaskStatus.Terminal, replayed!.Status);
        Assert.Equal("aborted", replayed.Outcome!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public void Open_SecondWriterOnSamePath_IsRefusedNamingTheFile()
    {
        var path = StorePath();

        using var first = CreateEfCoreStore(path);
        var error = Assert.Throws<InvalidOperationException>(() => CreateEfCoreStore(path));

        Assert.Contains(path, error.Message);
        Assert.Contains("already open", error.Message);
        Assert.Contains(Environment.ProcessId.ToString(), error.Message);
    }

    [Fact]
    public void Provider_Is_Selected_By_Name_Through_The_Registration_Seam()
    {
        EfCoreStoreProvider.Register();
        try
        {
            Assert.True(DurableStore.IsProviderRegistered(EfCoreStoreProvider.RegisteredName));

            using var store = DurableStore.Create(new DurableStoreOptions
            {
                Path = StorePath(),
                ProviderName = EfCoreStoreProvider.RegisteredName,
            });

            Assert.True(store.IsPersistent);
            Assert.IsType<EfCoreStoreProvider>(store.Provider);
        }
        finally
        {
            Assert.True(DurableStore.UnregisterProvider(EfCoreStoreProvider.RegisteredName));
        }

        Assert.False(DurableStore.IsProviderRegistered(EfCoreStoreProvider.RegisteredName));
    }

    [Fact]
    public void Provider_Factory_In_The_Options_Wins_Over_The_Format()
    {
        using var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = StorePath(),
            Format = DurableStoreFormat.Jsonl,
            Provider = _ => new EfCoreStoreProvider(),
        });

        Assert.IsType<EfCoreStoreProvider>(store.Provider);
        Assert.True(store.IsPersistent);
    }

    [Fact]
    public void Unregistered_Provider_Name_Is_Reported_At_Create()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DurableStore.Create(new DurableStoreOptions
            {
                Path = StorePath(),
                ProviderName = "no-such-provider",
            }));

        Assert.Contains("no-such-provider", error.Message);
        Assert.Contains(nameof(DurableStore.RegisterProvider), error.Message);
    }

    [Fact]
    public async Task Provider_Over_A_Context_Factory_Writes_Through_The_Model()
    {
        var path = StorePath();

        using (var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = path,
            Provider = options => new EfCoreStoreProvider(
                _ => new DbContextOptionsBuilder<DurableDbContext>()
                    .UseSqlite($"Data Source={options.Path}")
                    .Options),
        }))
        {
            await store.CommitAsync(async transaction =>
            {
                var conversation = transaction.CreateConversation();
                var draft = transaction.Doc(Notes, conversation.Id);
                draft.Value = new Dictionary<string, string> { ["text"] = "written" };
                draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
                await Task.CompletedTask;
            });
        }

        using var context = new DurableDbContext(
            new DbContextOptionsBuilder<DurableDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options);

        var session = Assert.Single(context.Sessions.AsNoTracking().ToList());
        var turn = Assert.Single(context.Turns.AsNoTracking().ToList());
        Assert.Equal(session.Id, turn.SessionId);
        Assert.NotEmpty(context.Events.AsNoTracking().ToList());
    }

    /// <summary>Creates a task with a single-phase definition.</summary>
    private static Task<string> CreateTaskAsync(DurableStore store, string name) =>
        store.CommitAsync(async transaction =>
        {
            var task = transaction.CreateTask(
                DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
                    name,
                    () => new Dictionary<string, string> { ["phase"] = "run" },
                    (_, _, runtime) => runtime.CommitAsync(
                        () => TaskCommit.Completed(DurableJson.ToElement("done")))),
                DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }));

            await Task.CompletedTask;
            return task.Id;
        });

    /// <summary>
    /// Mutates a task the way the store's own task runtime does: the record is changed
    /// in place and the provider is told about the new state inside the transaction.
    /// </summary>
    private static Task UpdateTaskAsync(DurableStore store, string taskId, Action<DurableTaskRecord> mutate) =>
        store.CommitAsync(async transaction =>
        {
            var record = store.Task(taskId)!;
            mutate(record);
            store.Provider.UpdateTask(record);
            await Task.CompletedTask;
        });

    private sealed record ForkRecord(string Id, string? ParentConversationId, string? ForkEntryId);
}
