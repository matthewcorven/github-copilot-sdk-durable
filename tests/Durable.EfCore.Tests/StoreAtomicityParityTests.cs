using Durable.EfCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Durable.EfCore.Tests;

/// <summary>Which of the two stores a parity scenario runs against.</summary>
public enum StoreKind
{
    /// <summary>The in-memory store, whose atomicity comes from the undo log.</summary>
    InMemory,

    /// <summary>The EF Core store, whose atomicity comes from a database transaction.</summary>
    EfCore,
}

/// <summary>
/// The atomicity contract, run as the same scenarios against both stores.
/// </summary>
/// <remarks>
/// The two stores reach atomicity by genuinely different mechanisms, so the guarantee has
/// to be demonstrated rather than assumed:
/// <list type="bullet">
/// <item>The in-memory store registers a compensating action for every mutation and runs
/// them most-recent-first when a transaction fails (<c>UndoLog</c>).</item>
/// <item>The EF Core store writes the commit inside one database transaction and lets the
/// database roll it back (<c>EfCoreStoreProvider.Persist</c>).</item>
/// </list>
/// <para>
/// Every scenario below is written so it can fail. The assertions are on the store's
/// monotonic write sequence and on the exact ids it minted, not on "something was
/// returned": the sequence is what a torn or half-applied transaction corrupts, and the
/// ids are minted from it, so identical ids across the two stores prove identical
/// sequence accounting.
/// </para>
/// </remarks>
public sealed class StoreAtomicityParityTests : IAsyncLifetime
{
    private static readonly DocumentDefinition<Dictionary<string, string>> Notes = new("notes");

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"durable-atomicity-{Guid.NewGuid():N}");

    private readonly List<StoreHarness> _harnesses = [];

    /// <summary>Creates the directory the EF Core store's database lives in.</summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        // Apply the checked-in migration before any store opens, so the schema under test
        // is the migration's rather than something the provider created on the fly.
        using var context = new DurableDbContext(EfCoreStoreProvider.ContextOptionsFor(DatabasePath));
        context.Database.Migrate();

        return Task.CompletedTask;
    }

    /// <summary>Disposes every store opened by a test, then deletes the directory.</summary>
    public Task DisposeAsync()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }

        _harnesses.Clear();

        // SQLite pools its connections, so a pooled handle can outlive the context that
        // opened it and keep the file open. Clearing the pool releases them.
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private string DatabasePath => Path.Combine(_directory, "durable.db");

    private StoreHarness Open(StoreKind kind)
    {
        var harness = new StoreHarness(kind, DatabasePath);
        _harnesses.Add(harness);
        return harness;
    }

    // ---------------------------------------------------------------------------------
    // Scenario 1: a failed write leaves no partial state.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A transaction whose body throws leaves the sequence and every collection exactly
    /// where they were, in both stores.
    /// </summary>
    /// <remarks>
    /// The store is seeded first, so "unchanged" is asserted against a non-empty store
    /// rather than against an empty one, where a rollback that did nothing would pass.
    /// </remarks>
    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.EfCore)]
    public async Task FailedTransaction_LeavesTheSequenceAndEveryCollectionUnchanged(StoreKind kind)
    {
        using var harness = Open(kind);
        var store = harness.Store;

        var seededConversation = await store.CommitAsync(transaction =>
        {
            var conversation = transaction.CreateConversation();
            var draft = transaction.Doc(Notes, conversation.Id);
            draft.Value = new Dictionary<string, string> { ["text"] = "seeded" };
            draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));
            return Task.FromResult(conversation.Id);
        });

        var sequenceBefore = harness.Sequence;
        var conversationsBefore = store.Conversations.Count;
        var documentsBefore = store.Documents.Count;
        var tasksBefore = store.Tasks.Count;
        var entriesBefore = store.Entries(seededConversation).Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(async transaction =>
        {
            var conversation = transaction.CreateConversation();

            var draft = transaction.Doc(Notes, conversation.Id);
            draft.Value = new Dictionary<string, string> { ["text"] = "discarded" };
            draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));

            transaction.CreateTask(
                DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
                    "atomicity.task",
                    () => new Dictionary<string, string> { ["phase"] = "run" },
                    (_, _, runtime) => runtime.CommitAsync(
                        () => TaskCommit.Completed(DurableJson.ToElement("done")))),
                DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }),
                conversation.Id);

            await Task.CompletedTask;
            throw new InvalidOperationException("the transaction body failed");
        }));

        // The sequence is the contract: a rollback that restores the collections but not
        // the sequence would let the next transaction mint an id that was already used.
        Assert.Equal(sequenceBefore, harness.Sequence);
        Assert.Equal(conversationsBefore, store.Conversations.Count);
        Assert.Equal(documentsBefore, store.Documents.Count);
        Assert.Equal(tasksBefore, store.Tasks.Count);
        Assert.Equal(entriesBefore, store.Entries(seededConversation).Count);

        // The seeded conversation is still readable, so the rollback did not over-reach.
        Assert.NotNull(store.Conversation(seededConversation));
        Assert.Single(store.Document("notes", seededConversation)!.Versions);
    }

    /// <summary>
    /// A write that fails inside the database transaction leaves no row behind.
    /// </summary>
    /// <remarks>
    /// The seam is a real one, reachable through the public store API: an entry appended
    /// to a conversation that was never created. The <c>turns</c> row carries a foreign key
    /// to the session it belongs to, so the insert fails inside <c>Persist()</c> — after
    /// <c>BeginTransaction()</c> and after the session pass has already run. No test-only
    /// hook is added to production code.
    /// <para>
    /// This asserts the row-level half of the contract, which the database transaction does
    /// deliver. The sequence half is asserted by
    /// <see cref="EfCore_FailedWrite_LeavesTheSequenceUnchanged_AndTheStoreUsable"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EfCore_WriteThatFailsInsideTheDatabaseTransaction_LeavesNoRowBehind()
    {
        using var harness = Open(StoreKind.EfCore);
        var store = harness.Store;

        var seededConversation = await store.CommitAsync(transaction =>
        {
            var conversation = transaction.CreateConversation();
            transaction.AppendEntry(conversation.Id, "notes.write");
            return Task.FromResult(conversation.Id);
        });

        var conversationsBefore = store.Conversations.Count;

        await Assert.ThrowsAsync<DbUpdateException>(() => store.CommitAsync(async transaction =>
        {
            transaction.AppendEntry("c-never-created", "notes.write");
            await Task.CompletedTask;
        }));

        using var context = harness.OpenContext();

        // The rollback is real: not one row of the failed transaction survived, and the
        // session pass that had already run was rolled back with it.
        Assert.Equal(conversationsBefore, context.Sessions.Count());
        Assert.Equal(0, context.Turns.Count(turn => turn.SessionId == "c-never-created"));
        Assert.Equal(0, context.Events.Count(@event => @event.SessionId == "c-never-created"));

        // The committed state from before the failure is untouched.
        Assert.NotNull(store.Conversation(seededConversation));
        Assert.Single(store.Entries(seededConversation));
    }

    /// <summary>
    /// A write that fails inside the database transaction leaves the sequence exactly where
    /// it was, and the store is still usable afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the parity assertion for #68. The EF Core store used to fail both halves:
    /// the sequence advanced even though the transaction rolled back, and the failed
    /// entities stayed in the <c>DbContext</c> change tracker so every later
    /// <c>SaveChanges()</c> re-attempted the same failing insert. The in-memory store
    /// passes the equivalent assertion in
    /// <see cref="FailedTransaction_LeavesTheSequenceAndEveryCollectionUnchanged"/>.
    /// </para>
    /// <para>
    /// The assertions are the contract, not the current behaviour, so this test running
    /// un-skipped is the proof that #68 is fixed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EfCore_FailedWrite_LeavesTheSequenceUnchanged_AndTheStoreUsable()
    {
        using var harness = Open(StoreKind.EfCore);
        var store = harness.Store;

        await store.CommitAsync(transaction =>
        {
            var conversation = transaction.CreateConversation();
            transaction.AppendEntry(conversation.Id, "notes.write");
            return Task.FromResult(conversation.Id);
        });

        var sequenceBefore = harness.Sequence;
        var conversationsBefore = store.Conversations.Count;

        await Assert.ThrowsAsync<DbUpdateException>(() => store.CommitAsync(async transaction =>
        {
            transaction.AppendEntry("c-never-created", "notes.write");
            await Task.CompletedTask;
        }));

        // The transaction rolled back, so the sequence did not advance and the next
        // transaction mints the id the failed one would have used.
        Assert.Equal(sequenceBefore, harness.Sequence);

        var next = await store.CommitAsync(transaction => Task.FromResult(transaction.CreateConversation().Id));
        Assert.Equal($"c{sequenceBefore + 1}", next);
        Assert.Equal(conversationsBefore + 1, store.Conversations.Count);
    }

    // ---------------------------------------------------------------------------------
    // Scenario 2: the same transaction produces the same observable state in both stores.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The same scripted transaction produces the same ids, the same sequence and the same
    /// document history in both stores.
    /// </summary>
    /// <remarks>
    /// Ids are minted from the sequence (<c>c{n}</c>, <c>e{n}</c>, <c>t{n}</c>), so equal
    /// ids are a stronger statement than equal counts: they say the two stores advanced
    /// their sequence by the same amount, in the same order.
    /// </remarks>
    [Fact]
    public async Task IdenticalTransaction_ProducesIdenticalObservableState_InBothStores()
    {
        using var inMemory = Open(StoreKind.InMemory);
        using var efCore = Open(StoreKind.EfCore);

        var inMemoryOutcome = await RunScriptedTransaction(inMemory.Store);
        var efCoreOutcome = await RunScriptedTransaction(efCore.Store);

        // Compared field by field: ScriptedOutcome is a record, but its members are
        // IReadOnlyList<string>, which a record compares by reference. Asserting on the
        // record itself would compare list identities and pass or fail for the wrong reason.
        Assert.Equal(inMemoryOutcome.ConversationId, efCoreOutcome.ConversationId);
        Assert.Equal(inMemoryOutcome.EntryIds, efCoreOutcome.EntryIds);
        Assert.Equal(inMemoryOutcome.TaskIds, efCoreOutcome.TaskIds);
        Assert.Equal(inMemoryOutcome.DocumentVersionEntryIds, efCoreOutcome.DocumentVersionEntryIds);
        Assert.Equal(inMemoryOutcome.Sequence, efCoreOutcome.Sequence);

        Assert.Equal(inMemory.Sequence, efCore.Sequence);
        Assert.Equal(inMemoryOutcome.Sequence, inMemory.Sequence);

        // Guard against the comparison passing because both sides are empty.
        Assert.Equal(2, inMemoryOutcome.EntryIds.Count);
        Assert.Single(inMemoryOutcome.TaskIds);
        Assert.Equal(2, inMemoryOutcome.DocumentVersionEntryIds.Count);
    }

    private static async Task<ScriptedOutcome> RunScriptedTransaction(DurableStore store)
    {
        var conversationId = await store.CommitAsync(transaction =>
        {
            var conversation = transaction.CreateConversation();

            var draft = transaction.Doc(Notes, conversation.Id);
            draft.Value = new Dictionary<string, string> { ["text"] = "first" };
            draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));

            transaction.CreateTask(
                DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
                    "atomicity.task",
                    () => new Dictionary<string, string> { ["phase"] = "run" },
                    (_, _, runtime) => runtime.CommitAsync(
                        () => TaskCommit.Completed(DurableJson.ToElement("done")))),
                DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }),
                conversation.Id);

            return Task.FromResult(conversation.Id);
        });

        await store.CommitAsync(transaction =>
        {
            var draft = transaction.Doc(Notes, conversationId);
            draft.Value = new Dictionary<string, string> { ["text"] = "second" };
            draft.Save(transaction.AppendEntry(conversationId, "notes.write"));
            return Task.CompletedTask;
        });

        var document = store.Document("notes", conversationId)!;

        return new ScriptedOutcome(
            conversationId,
            store.Entries(conversationId),
            store.Tasks.Select(task => task.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            document.Versions.Select(version => version.EntryId).ToList(),
            store.Provider.Sequence);
    }

    /// <summary>The observable state one scripted transaction leaves behind.</summary>
    private sealed record ScriptedOutcome(
        string ConversationId,
        IReadOnlyList<string> EntryIds,
        IReadOnlyList<string> TaskIds,
        IReadOnlyList<string> DocumentVersionEntryIds,
        long Sequence);

    // ---------------------------------------------------------------------------------
    // Scenario 3: concurrent writers do not interleave into a half-applied transaction.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Concurrent writers each land as a whole transaction: the sequence advances by
    /// exactly the number of values the writers minted, and the ids form a contiguous run
    /// with no gap and no duplicate.
    /// </summary>
    /// <remarks>
    /// SQLite serialises writers, so "two threads wrote at once and nothing crashed" would
    /// pass trivially. The assertion here is on the observable outcome instead: a
    /// half-applied transaction would either burn a sequence value (leaving a gap) or reuse
    /// one (leaving a duplicate), and both are caught by comparing the minted ids against
    /// the contiguous run the sequence says they must be.
    /// <para>
    /// Each writer mints three values — the conversation id, the entry id, and the entry
    /// record's own sequence — so <paramref name="writers"/> writers must leave the
    /// sequence at exactly <c>3 * writers</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.EfCore)]
    public async Task ConcurrentWriters_DoNotInterleaveIntoAHalfAppliedTransaction(StoreKind kind)
    {
        const int writers = 8;

        using var harness = Open(kind);
        var store = harness.Store;

        var conversations = await Task.WhenAll(Enumerable.Range(0, writers).Select(index =>
            store.CommitAsync(transaction =>
            {
                var conversation = transaction.CreateConversation();
                transaction.AppendEntry(conversation.Id, $"writer.{index}");
                return Task.FromResult(conversation.Id);
            })));

        Assert.Equal(3 * writers, harness.Sequence);
        Assert.Equal(writers, store.Conversations.Count);
        Assert.Equal(writers, conversations.Distinct(StringComparer.Ordinal).Count());

        // Every conversation holds exactly the one entry its own writer appended: no
        // writer's entry landed in another writer's conversation.
        foreach (var conversationId in conversations)
        {
            Assert.Single(store.Entries(conversationId));
        }

        // Every id the writers minted is distinct and inside the run the sequence says it
        // must be. A torn transaction shows up here as a repeated value or as an id past
        // the sequence's end.
        var minted = conversations
            .Select(id => long.Parse(id[1..]))
            .Concat(conversations.SelectMany(id => store.Entries(id)).Select(id => long.Parse(id[1..])))
            .ToList();

        Assert.Equal(2 * writers, minted.Distinct().Count());
        Assert.All(minted, value => Assert.InRange(value, 1, 3 * writers));
    }

    /// <summary>
    /// The outcome of concurrent writers is identical in both stores, and the EF Core
    /// store's outcome survives a restart.
    /// </summary>
    [Fact]
    public async Task ConcurrentWriters_ProduceTheSameOutcome_InBothStores_AndSurviveARestart()
    {
        const int writers = 6;

        using var inMemory = Open(StoreKind.InMemory);
        using var efCore = Open(StoreKind.EfCore);

        var inMemoryIds = await WriteConcurrently(inMemory.Store, writers);
        var efCoreIds = await WriteConcurrently(efCore.Store, writers);

        Assert.Equal(inMemoryIds, efCoreIds);
        Assert.Equal(inMemory.Sequence, efCore.Sequence);

        var sequenceBeforeRestart = efCore.Sequence;
        efCore.Dispose();

        using var reopened = Open(StoreKind.EfCore);

        Assert.Equal(sequenceBeforeRestart, reopened.Sequence);
        Assert.Equal(efCoreIds, reopened.Store.Conversations
            .Select(conversation => conversation.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList());

        foreach (var conversationId in efCoreIds)
        {
            Assert.Single(reopened.Store.Entries(conversationId));
        }
    }

    private static async Task<IReadOnlyList<string>> WriteConcurrently(DurableStore store, int writers) =>
        (await Task.WhenAll(Enumerable.Range(0, writers).Select(index =>
            store.CommitAsync(transaction =>
            {
                var conversation = transaction.CreateConversation();
                transaction.AppendEntry(conversation.Id, $"writer.{index}");
                return Task.FromResult(conversation.Id);
            }))))
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();

    // ---------------------------------------------------------------------------------
    // Scenario 4: a restart between commit and read returns the committed state.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A store disposed after a commit and reopened over the same database returns the
    /// committed state.
    /// </summary>
    /// <remarks>
    /// The sequence half of the restart contract is asserted by
    /// <see cref="EfCore_RestartBetweenCommitAndRead_ContinuesTheSequence"/>.
    /// </remarks>
    [Fact]
    public async Task EfCore_RestartBetweenCommitAndRead_ReturnsTheCommittedState()
    {
        string conversationId;
        string taskId;

        using (var first = Open(StoreKind.EfCore))
        {
            (conversationId, taskId) = await CommitConversationDocumentAndTask(first.Store);
        }

        using var second = Open(StoreKind.EfCore);

        Assert.NotNull(second.Store.Conversation(conversationId));
        Assert.NotNull(second.Store.Task(taskId));
        Assert.Single(second.Store.Entries(conversationId));

        var document = second.Store.Document("notes", conversationId);
        Assert.NotNull(document);
        Assert.Equal(
            new Dictionary<string, string> { ["text"] = "committed" },
            DurableJson.FromElement<Dictionary<string, string>>(document!.Latest!.Value));
    }

    /// <summary>
    /// A store reopened over the same database continues its sequence rather than
    /// restarting, so it cannot mint an id the first store already used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the parity assertion for #69. <c>Replay()</c> used to rebuild the sequence
    /// from <c>turns.Sequence</c> and document-version event sequences only. A task id
    /// consumes a sequence value that no row carries, so after a restart the sequence went
    /// backwards and the reopened store minted <c>t1</c> a second time — overwriting the
    /// task the first store committed. The commit now records the sequence it reached.
    /// </para>
    /// <para>
    /// The assertions are the contract, not the current behaviour, so this test running
    /// un-skipped is the proof that #69 is fixed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EfCore_RestartBetweenCommitAndRead_ContinuesTheSequence()
    {
        long sequenceAfterCommit;

        using (var first = Open(StoreKind.EfCore))
        {
            await CommitConversationDocumentAndTask(first.Store);
            sequenceAfterCommit = first.Sequence;
        }

        using var second = Open(StoreKind.EfCore);

        Assert.Equal(sequenceAfterCommit, second.Sequence);

        var next = await second.Store.CommitAsync(transaction => Task.FromResult(transaction.CreateConversation().Id));
        Assert.Equal($"c{sequenceAfterCommit + 1}", next);
        Assert.True(second.Sequence > sequenceAfterCommit);
    }

    private static async Task<(string ConversationId, string TaskId)> CommitConversationDocumentAndTask(DurableStore store) =>
        await store.CommitAsync(transaction =>
        {
            var conversation = transaction.CreateConversation();

            var draft = transaction.Doc(Notes, conversation.Id);
            draft.Value = new Dictionary<string, string> { ["text"] = "committed" };
            draft.Save(transaction.AppendEntry(conversation.Id, "notes.write"));

            var task = transaction.CreateTask(
                DurableTask.Define<Dictionary<string, string>, Dictionary<string, string>, string>(
                    "atomicity.task",
                    () => new Dictionary<string, string> { ["phase"] = "run" },
                    (_, _, runtime) => runtime.CommitAsync(
                        () => TaskCommit.Completed(DurableJson.ToElement("done")))),
                DurableJson.ToElement(new Dictionary<string, string> { ["input"] = "value" }),
                conversation.Id);

            return Task.FromResult((conversation.Id, task.Id));
        });

    /// <summary>
    /// The in-memory store does not survive a restart, by design: it is the ephemeral
    /// tier. This is the one documented divergence between the two stores, and it is a
    /// property of the tier rather than of the atomicity mechanism.
    /// </summary>
    [Fact]
    public async Task EphemeralStore_DoesNotSurviveARestart_ByDesign()
    {
        using (var first = DurableStore.CreateEphemeral())
        {
            await first.CommitAsync(transaction =>
            {
                transaction.CreateConversation();
                return Task.CompletedTask;
            });

            Assert.Single(first.Conversations);
            Assert.False(first.IsPersistent);
        }

        using var second = DurableStore.CreateEphemeral();

        Assert.Empty(second.Conversations);
        Assert.Equal(0, second.Provider.Sequence);
    }

    // ---------------------------------------------------------------------------------
    // The harness: one store of the requested kind, plus the sequence it is at.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// One store of a given kind, opened over the test's database when the kind is
    /// <see cref="StoreKind.EfCore"/>.
    /// </summary>
    private sealed class StoreHarness : IDisposable
    {
        private readonly string? _databasePath;

        public StoreHarness(StoreKind kind, string databasePath)
        {
            Kind = kind;

            if (kind == StoreKind.EfCore)
            {
                _databasePath = databasePath;
                Store = DurableStore.Create(new DurableStoreOptions
                {
                    Path = databasePath,
                    Provider = _ => new EfCoreStoreProvider(),
                });
            }
            else
            {
                Store = DurableStore.CreateEphemeral();
            }
        }

        public StoreKind Kind { get; }

        public DurableStore Store { get; private set; }

        /// <summary>The store's monotonic write sequence, read off the provider.</summary>
        public long Sequence => Store.Provider.Sequence;

        /// <summary>Opens a context over the same database, for asserting on the rows.</summary>
        public DurableDbContext OpenContext() =>
            new(EfCoreStoreProvider.ContextOptionsFor(
                _databasePath ?? throw new InvalidOperationException("The in-memory store has no database.")));

        public void Dispose() => Store.Dispose();
    }
}
