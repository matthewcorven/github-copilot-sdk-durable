using System.Text.Json;
using Durable;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// The fork contract: a fork inherits the parent's transcript at a durable entry boundary, each
/// document follows its own <see cref="ForkPolicy"/>, the two conversations then diverge
/// independently, the fork is configured from the choices the parent had at the boundary, and an
/// interrupted fork is recoverable because its intent is durable before the native fork runs.
/// </summary>
public class ForkTests
{
    /// <summary>
    /// The persisted fork-intent document. The kind is spelled out rather than taken from the
    /// runtime because it is part of the on-disk contract: a store written by one build has to be
    /// readable by the next, so the name is asserted here on purpose.
    /// </summary>
    private static readonly DocumentDefinition<ForkIntent> ForkDocument =
        new("durable.fork", DocumentHistory.Rewindable, ForkPolicy.Initial);

    private static DocumentDefinition<Dictionary<string, string>> Doc(string kind, ForkPolicy policy) =>
        new(kind, DocumentHistory.Rewindable, policy, () => new Dictionary<string, string> { ["text"] = "initial" });

    private static ForkIntent? IntentOf(DurableStore store, string conversationId) =>
        store.Document(ForkDocument.Kind, conversationId)?.Latest is { } latest
            ? latest.Deserialize<ForkIntent>(DurableJson.Options)
            : null;

    [Fact]
    public async Task Fork_AppliesEachDocumentsForkPolicy()
    {
        var store = new DurableStore();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var asOf = Doc("test.asof", ForkPolicy.AsOf);
        var current = Doc("test.current", ForkPolicy.Current);
        var initial = Doc("test.initial", ForkPolicy.Initial);

        var parent = await harness.CreateConversationAsync();

        var boundary = await parent.CommitAsync(async tx =>
        {
            var entry = tx.AppendEntry(parent.Id, "note", "boundary");
            foreach (var definition in new[] { asOf, current, initial })
            {
                var draft = tx.Doc(definition, parent.Id);
                draft.Value["text"] = "v1";
                draft.Save(entry);
            }

            await Task.CompletedTask;
            return entry;
        });

        await parent.CommitAsync(async tx =>
        {
            var entry = tx.AppendEntry(parent.Id, "note", "after boundary");
            foreach (var definition in new[] { asOf, current, initial })
            {
                var draft = tx.Doc(definition, parent.Id);
                draft.Value["text"] = "v2";
                draft.Save(entry);
            }

            await Task.CompletedTask;
        });

        var fork = await harness.ForkAsync(parent, boundary);

        // AsOf is the parent's value at the boundary, Current is its latest value, and Initial is
        // the value the document started with.
        Assert.Equal("v1", fork.Snapshot(asOf)!["text"]);
        Assert.Equal("v2", fork.Snapshot(current)!["text"]);
        Assert.Equal("initial", fork.Snapshot(initial)!["text"]);

        // The parent is untouched by the fork.
        Assert.Equal("v2", parent.Snapshot(asOf)!["text"]);
        Assert.Equal("v2", parent.Snapshot(current)!["text"]);
        Assert.Equal("v2", parent.Snapshot(initial)!["text"]);
    }

    [Fact]
    public async Task Fork_IsolatesDocumentsFromTheParent()
    {
        var store = new DurableStore();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var doc = Doc("test.isolation", ForkPolicy.AsOf);
        var parent = await harness.CreateConversationAsync();

        var boundary = await parent.CommitAsync(async tx =>
        {
            var entry = tx.AppendEntry(parent.Id, "note", "boundary");
            var draft = tx.Doc(doc, parent.Id);
            draft.Value["text"] = "shared";
            draft.Save(entry);
            await Task.CompletedTask;
            return entry;
        });

        var fork = await harness.ForkAsync(parent, boundary);
        Assert.Equal("shared", fork.Snapshot(doc)!["text"]);

        await fork.CommitAsync(async tx =>
        {
            var draft = tx.Doc(doc, fork.Id);
            draft.Value["text"] = "fork";
            draft.Save(tx.AppendEntry(fork.Id, "note", "fork edit"));
            await Task.CompletedTask;
        });

        await parent.CommitAsync(async tx =>
        {
            var draft = tx.Doc(doc, parent.Id);
            draft.Value["text"] = "parent";
            draft.Save(tx.AppendEntry(parent.Id, "note", "parent edit"));
            await Task.CompletedTask;
        });

        Assert.Equal("fork", fork.Snapshot(doc)!["text"]);
        Assert.Equal("parent", parent.Snapshot(doc)!["text"]);
    }

    [Fact]
    public async Task Fork_InheritsChoicesAtTheBoundary_NotTheParentsCurrentChoices()
    {
        var store = new DurableStore();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var parent = await harness.CreateConversationAsync(new AgentChoices { Instructions = "at the boundary" });
        var boundary = await parent.AppendEntryAsync("note", "fork point");

        // The parent moves on after the boundary. A fork taken at the boundary must not see this.
        await parent.ConfigureAsync(new AgentChoices { Instructions = "after the boundary" });

        var fork = await harness.ForkAsync(parent, boundary);

        Assert.Equal("at the boundary", fork.Choices()!.Instructions);
        Assert.Equal("after the boundary", parent.Choices()!.Instructions);
    }

    [Fact]
    public async Task Fork_PersistsItsIntentBeforeTheNativeFork()
    {
        var store = new DurableStore();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var parent = await harness.CreateConversationAsync();
        var boundary = await parent.AppendEntryAsync("note", "fork point");

        var fork = await harness.ForkAsync(parent, boundary);

        var record = store.Conversation(fork.Id)!;
        Assert.Equal(parent.Id, record.ParentConversationId);
        Assert.Equal(boundary, record.ForkEntryId);

        // The first committed intent is Pending and the last is Completed: the durable half was
        // written before the native fork was attempted, so a crash in between is detectable.
        var versions = store.Document(ForkDocument.Kind, fork.Id)!.Versions;
        Assert.Equal(2, versions.Count);
        Assert.Equal(ForkIntentState.Pending, versions[0].Value.Deserialize<ForkIntent>(DurableJson.Options)!.State);
        Assert.Equal(ForkIntentState.Completed, versions[^1].Value.Deserialize<ForkIntent>(DurableJson.Options)!.State);

        var intent = IntentOf(store, fork.Id)!;
        Assert.Equal(parent.Id, intent.ParentConversationId);
        Assert.Equal(boundary, intent.ForkEntryId);
        Assert.Equal(fork.SessionId, intent.SessionId);
    }

    [Fact]
    public async Task Fork_RejectsAnEntryThatIsNotTheParents()
    {
        var store = new DurableStore();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var parent = await harness.CreateConversationAsync();
        await parent.AppendEntryAsync("note", "fork point");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => harness.ForkAsync(parent, "e999999"));
        Assert.Equal("entryId", error.ParamName);
    }

    [Fact]
    public async Task InterruptedFork_IsCompletedOnReopen()
    {
        var store = new DurableStore();
        var registry = new ExtensionRegistry();
        string parentId;
        string boundary;
        string forkId;

        await using (var harness = await DurableHarness.OpenAsync(store, registry))
        {
            var parent = await harness.CreateConversationAsync(new AgentChoices { Instructions = "inherited" });
            boundary = await parent.AppendEntryAsync("note", "fork point");
            parentId = parent.Id;

            // The durable half of a fork, exactly as ForkAsync commits it, with no native fork:
            // this is the state a crash between the commit and the RPC leaves behind.
            forkId = await CommitPendingForkAsync(store, parent, boundary);
        }

        await using var reopened = await DurableHarness.OpenAsync(store, registry);
        await reopened.RootAsync();

        var fork = Assert.Single(reopened.Conversations, conversation => conversation.Id == forkId);
        var intent = IntentOf(store, forkId)!;

        Assert.Equal(ForkIntentState.Completed, intent.State);
        Assert.Equal(fork.SessionId, intent.SessionId);
        Assert.NotEqual(reopened.Conversations.Single(c => c.Id == parentId).SessionId, fork.SessionId);

        // The inherited transcript and the inherited choices survived the interruption.
        Assert.Contains(boundary, fork.Entries(10));
        Assert.Equal("inherited", fork.Choices()!.Instructions);
    }

    [Fact]
    public async Task InterruptedFork_IsAbandonedWhenTheParentIsGone()
    {
        var store = new DurableStore();
        var registry = new ExtensionRegistry();
        string forkId;

        await using (var harness = await DurableHarness.OpenAsync(store, registry))
        {
            forkId = await store.CommitAsync(tx =>
            {
                var created = tx.CreateConversation(parentConversationId: "c999999", forkEntryId: "e999999");
                var intent = tx.Doc(ForkDocument, created.Id);
                intent.Value = new ForkIntent
                {
                    ParentConversationId = "c999999",
                    ForkEntryId = "e999999",
                    State = ForkIntentState.Pending,
                };
                intent.Save(tx.AppendEntry(created.Id, "fork"));
                return Task.FromResult(created.Id);
            });
        }

        await using var reopened = await DurableHarness.OpenAsync(store, registry);
        await reopened.RootAsync();

        // A fork with no session and no parent can never be completed, so it is abandoned rather
        // than left pending forever. Its durable half stays, but it is never given a session.
        Assert.Equal(ForkIntentState.Abandoned, IntentOf(store, forkId)!.State);
        Assert.DoesNotContain(reopened.Conversations, conversation => conversation.Id == forkId);
    }

    [Fact]
    public async Task FacadeFork_InitializesTheClientAndDelegatesToTheRuntime()
    {
        var store = new DurableStore();
        var registry = new ExtensionRegistry();
        DurableConversation parent;
        string boundary;

        await using (var harness = await DurableHarness.OpenAsync(store, registry))
        {
            parent = await harness.CreateConversationAsync();
            boundary = await parent.AppendEntryAsync("note", "fork point");
        }

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseRegistry(registry)
            .Build();

        Assert.False(client.IsInitialized);

        // The facade initializes itself and hands the fork to the runtime, which is where the
        // entry is validated. A full round trip through the facade needs the conversation
        // creation entry points, which belong to #7.3.
        var error = await Assert.ThrowsAsync<ArgumentException>(() => client.ForkAsync(parent, "e999999"));
        Assert.Equal("entryId", error.ParamName);
        Assert.True(client.IsInitialized);
        Assert.Same(store, client.Store);
    }

    /// <summary>
    /// Commits the durable half of a fork — record, inherited transcript, inherited documents, and
    /// a <see cref="ForkIntentState.Pending"/> intent — without attempting the native fork.
    /// </summary>
    private static Task<string> CommitPendingForkAsync(DurableStore store, DurableConversation parent, string entryId) =>
        store.CommitAsync(tx =>
        {
            var created = tx.CreateConversation(parentConversationId: parent.Id, forkEntryId: entryId);
            store.Provider.CopyEntries(parent.Id, created.Id, entryId);
            foreach (var document in store.Documents.Where(d => d.OwnerId == parent.Id).ToList())
            {
                store.Provider.CopyDocument(document, created.Id, entryId);
            }

            var intent = tx.Doc(ForkDocument, created.Id);
            intent.Value = new ForkIntent
            {
                ParentConversationId = parent.Id,
                ForkEntryId = entryId,
                State = ForkIntentState.Pending,
            };
            intent.Save(tx.AppendEntry(created.Id, "fork"));

            return Task.FromResult(created.Id);
        });
}
