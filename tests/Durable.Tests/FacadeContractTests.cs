using Durable;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// The public path through <see cref="DurableCopilotClient"/>: the contract a host relies on when
/// it drives the durable layer through the facade alone, without reaching for the runtime, the
/// store's provider, or the SDK client.
/// </summary>
/// <remarks>
/// <para>
/// Every fact enters through the facade and then reads the store, because a handle that answers
/// from its own memory proves nothing about durability. Facts that need a real turn drive the
/// recorded runtime; facts about recovery, forks, and configuration use the bundled runtime or a
/// client that is never started, so the question they ask is answered before the SDK is involved.
/// </para>
/// <para>
/// Each fact owns its store — an ephemeral one, or its own directory on disk — so no fact can
/// pass on state another one left behind.
/// </para>
/// </remarks>
public class FacadeContractTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The session id the bundled recording names.</summary>
    private const string RecordedSessionId = "session-replay-1";

    /// <summary>
    /// The stored agent choices. The kinds are spelled out rather than read from the runtime
    /// because they are the on-disk contract, not an implementation detail.
    /// </summary>
    private static readonly DocumentDefinition<AgentChoices> AgentDocument =
        new("durable.agent", DocumentHistory.Rewindable, ForkPolicy.AsOf);

    private static readonly DocumentDefinition<SessionBinding> BindingDocument =
        new("durable.session-binding", DocumentHistory.Rewindable, ForkPolicy.Initial);

    private static readonly DocumentDefinition<ConversationCheckpoint> CheckpointDocument =
        new("durable.checkpoint", DocumentHistory.Rewindable, ForkPolicy.AsOf);

    private static readonly DocumentDefinition<ForkIntent> ForkDocument =
        new("durable.fork", DocumentHistory.Rewindable, ForkPolicy.Initial);

    // ----- Identity and resume -------------------------------------------------------------

    [Fact]
    public async Task Reopen_RestoresTheRecordedConversationWithItsChoicesAndItsTurnRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"durable-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.jsonl");

        try
        {
            string conversationId;
            string checkpointId;

            await using (var first = new DurableClientBuilder()
                .UseStore(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl })
                .UseClient(ReplayClientOptions())
                .Build())
            {
                var conversation = await first.CreateConversationAsync(new AgentChoices
                {
                    Model = "replay-model",
                    Instructions = "restart contract",
                });

                conversationId = conversation.Id;

                await conversation.SubmitAsync("hello").WaitAsync(EventTimeout);
                checkpointId = conversation.Checkpoint()!.CheckpointId!;
            }

            // The store is reopened through the facade alone, exactly as a second process would.
            await using var reopened = new DurableClientBuilder()
                .UseStore(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl })
                .UseClient(ReplayClientOptions())
                .Build();

            var restored = await reopened.RootAsync();

            // The conversation that comes back is the recorded one, not a second conversation
            // created beside it, and it is the one the facade now answers lookups for.
            Assert.Equal(conversationId, restored.Id);
            Assert.Equal(conversationId, restored.Record.Id);
            Assert.Null(restored.Record.ParentConversationId);
            Assert.Null(restored.Record.ForkEntryId);
            Assert.Same(restored, reopened.FindConversation(conversationId));
            Assert.Single(reopened.Store.Conversations);

            // A reopened conversation has to run, so it is given a session; the binding records
            // the session it is actually on rather than the one the previous process left behind.
            Assert.False(string.IsNullOrWhiteSpace(restored.SessionId));
            var binding = Assert.IsType<SessionBinding>(restored.Binding());
            Assert.Equal(restored.SessionId, binding.SessionId);
            Assert.Equal("replay-model", binding.Model);
            Assert.NotNull(binding.BoundAt);

            // The choices are the conversation's own, not the build's defaults.
            var choices = Assert.IsType<AgentChoices>(restored.Choices());
            Assert.Equal("replay-model", choices.Model);
            Assert.Equal("restart contract", choices.Instructions);

            // The turn the previous process committed is still the conversation's checkpoint: the
            // same checkpoint, under the same event key, with the same answer.
            var checkpoint = Assert.IsType<ConversationCheckpoint>(restored.Checkpoint());
            Assert.Equal(checkpointId, checkpoint.CheckpointId);
            Assert.Equal("turn", checkpoint.Kind);
            Assert.Equal("turn:msg-0001", checkpoint.EventId);
            Assert.Equal("Hello from the recorded transcript.", checkpoint.Content);
            Assert.NotNull(checkpoint.MessageId);
            Assert.NotNull(checkpoint.RecordedAt);

            // And the admission that turn settled.
            var admission = Assert.IsType<InputAdmission>(restored.Admission());
            Assert.Equal("completed", admission.State);
            Assert.Equal("hello", admission.Prompt);
            Assert.NotNull(admission.AdmittedAt);
            Assert.NotNull(admission.SettledAt);

            // Answers are the handle's in-process history, so a reopened handle starts empty even
            // though the durable record is complete.
            Assert.Empty(restored.Answers);

            // "Restored" has to mean the conversation answers: the reopened handle runs a turn,
            // against the same event key, so the checkpoint log does not grow a duplicate.
            var answer = await restored.SubmitAsync("again").WaitAsync(EventTimeout);
            Assert.NotNull(answer);
            Assert.Equal("Hello from the recorded transcript.", answer!.Data.Content);
            Assert.Single(restored.Answers);
            Assert.Equal("turn:msg-0001", restored.Checkpoint()!.EventId);
            Assert.Equal(1, VersionCount(reopened.Store, CheckpointDocument, conversationId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_RecoversTheBoundSessionItsChoicesAndItsTurnRecord()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(
            store,
            new AgentChoices { Model = "replay-model", Instructions = "resume contract" },
            bindSession: RecordedSessionId);

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseClient(ReplayClientOptions())
            .Build();

        var resumed = await client.ResumeConversationAsync(conversationId);

        // Resume rebinds the stored conversation to the session the binding names. It must not
        // open a replacement session: the conversation is the identity, the session is what the
        // record says it is.
        Assert.Equal(conversationId, resumed.Id);
        Assert.Equal(conversationId, resumed.Record.Id);
        Assert.Null(resumed.Record.ParentConversationId);
        Assert.Null(resumed.Record.ForkEntryId);
        Assert.Equal(RecordedSessionId, resumed.SessionId);
        Assert.Same(resumed, client.FindConversation(conversationId));

        var binding = Assert.IsType<SessionBinding>(resumed.Binding());
        Assert.Equal(RecordedSessionId, binding.SessionId);
        Assert.Equal("replay-model", binding.Model);

        var choices = Assert.IsType<AgentChoices>(resumed.Choices());
        Assert.Equal("replay-model", choices.Model);
        Assert.Equal("resume contract", choices.Instructions);

        // Nothing has run on the resumed handle, and nothing had run before it: the record is
        // empty until a turn commits it.
        Assert.Null(resumed.Checkpoint());
        Assert.Null(resumed.Admission());
        Assert.Empty(resumed.Answers);

        // Recovered means the session answers. The turn's durable half is written in full.
        var answer = await resumed.SubmitAsync("hello").WaitAsync(EventTimeout);
        Assert.NotNull(answer);
        Assert.Equal("Hello from the recorded transcript.", answer!.Data.Content);
        Assert.Single(resumed.Answers);

        var checkpoint = Assert.IsType<ConversationCheckpoint>(resumed.Checkpoint());
        Assert.Equal("turn", checkpoint.Kind);
        Assert.Equal("turn:msg-0001", checkpoint.EventId);
        Assert.Equal("Hello from the recorded transcript.", checkpoint.Content);
        Assert.NotNull(checkpoint.CheckpointId);
        Assert.NotNull(checkpoint.RecordedAt);

        var admission = Assert.IsType<InputAdmission>(resumed.Admission());
        Assert.Equal("completed", admission.State);
        Assert.Equal("hello", admission.Prompt);
        Assert.NotNull(admission.AdmittedAt);
        Assert.NotNull(admission.SettledAt);
        Assert.NotNull(admission.AdmissionId);

        // A second turn advances the checkpoint log rather than replacing its single entry.
        var second = await resumed.SubmitAsync("again").WaitAsync(EventTimeout);
        Assert.NotNull(second);
        Assert.Equal(2, resumed.Answers.Count);

        var advanced = Assert.IsType<ConversationCheckpoint>(resumed.Checkpoint());
        Assert.Equal("turn:msg-0002", advanced.EventId);
        Assert.Equal(2, VersionCount(store, CheckpointDocument, conversationId));
    }

    [Fact]
    public async Task Resume_WithAMissingTranscript_ReportsRecoveryAndLeavesTheRecordIntact()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(
            store,
            new AgentChoices { Model = "replay-model" },
            bindSession: "session-not-recorded");

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseClient(ReplayClientOptions())
            .Build();

        var failure = await Assert.ThrowsAsync<DurableRecoveryRequiredException>(
            () => client.ResumeConversationAsync(conversationId));

        Assert.Equal(conversationId, failure.ConversationId);
        Assert.Equal("session-not-recorded", failure.SessionId);
        Assert.Contains("no recoverable native transcript", failure.Message);
        Assert.Contains("re-create its session deliberately", failure.Message);

        // The failure is explicit and non-destructive: the conversation is still stored and still
        // bound to the session the host has to decide about, and no replacement session was opened
        // behind its back.
        var record = Assert.Single(store.Conversations);
        Assert.Equal(conversationId, record.Id);

        var binding = Assert.IsType<SessionBinding>(Stored(store, BindingDocument, conversationId));
        Assert.Equal("session-not-recorded", binding.SessionId);
        Assert.Null(client.FindConversation(conversationId));
    }

    [Fact]
    public async Task Resume_FromASecondProcess_RecoversTheConversationItCreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"durable-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.jsonl");

        try
        {
            string conversationId;
            string sessionId;

            await using (var first = new DurableClientBuilder()
                .UseStore(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl })
                .UseClient(ReplayClientOptions())
                .Build())
            {
                var conversation = await first.CreateConversationAsync(new AgentChoices
                {
                    Model = "replay-model",
                    Instructions = "cross-process contract",
                });

                conversationId = conversation.Id;
                sessionId = conversation.SessionId!;

                await conversation.SubmitAsync("hello").WaitAsync(EventTimeout);
            }

            // The second facade runs against a runtime that has never heard of this conversation:
            // the process that created the session is gone, and the runtime's session table went
            // with it. Resume has to be able to ask for the session the record names.
            await using var second = new DurableClientBuilder()
                .UseStore(new DurableStoreOptions { Path = path, Format = DurableStoreFormat.Jsonl })
                .UseClient(ReplayClientOptions())
                .Build();

            var resumed = await second.ResumeConversationAsync(conversationId);

            // The conversation comes back on the session the durable layer recorded, not on a
            // replacement it had to invent because it could not name the original.
            Assert.Equal(conversationId, resumed.Id);
            Assert.Equal(sessionId, resumed.SessionId);
            Assert.Same(resumed, second.FindConversation(conversationId));

            var binding = Assert.IsType<SessionBinding>(resumed.Binding());
            Assert.Equal(sessionId, binding.SessionId);
            Assert.True(binding.Pinned);

            // And it answers: the recovered session runs a turn and commits it durably.
            var answer = await resumed.SubmitAsync("again").WaitAsync(EventTimeout);
            Assert.NotNull(answer);
            Assert.Equal("Hello from the recorded transcript.", answer!.Data.Content);

            var checkpoint = Assert.IsType<ConversationCheckpoint>(resumed.Checkpoint());
            Assert.Equal("turn:msg-0001", checkpoint.EventId);
            Assert.Equal("Hello from the recorded transcript.", checkpoint.Content);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ----- Checkpoint failure --------------------------------------------------------------

    [Fact]
    public async Task Submit_WithAFailingCheckpoint_ReportsTheFailureAndDoesNotClaimSuccess()
    {
        var provider = new RefusingCheckpointStore();

        await using var client = new DurableClientBuilder()
            .UseStore(new DurableStoreOptions
            {
                Format = DurableStoreFormat.InMemory,
                Provider = _ => provider,
            })
            .UseClient(ReplayClientOptions())
            .Build();

        var conversation = await client.CreateConversationAsync(new AgentChoices { Model = "replay-model" });

        // From here the provider refuses the commit that would carry a checkpoint and accepts
        // every other one, so the failure is one the conversation has to survive.
        provider.Arm();

        var failure = await Assert.ThrowsAsync<IOException>(
            () => conversation.SubmitAsync("hello").WaitAsync(EventTimeout));

        Assert.Equal(RefusingCheckpointStore.Refusal, failure.Message);
        Assert.Equal(1, provider.Refusals);

        // The submission did not report success: it threw, and the admission it committed says the
        // outcome is uncertain rather than completed.
        var admission = Assert.IsType<InputAdmission>(conversation.Admission());
        Assert.Equal("uncertain", admission.State);
        Assert.Equal("hello", admission.Prompt);
        Assert.NotNull(admission.SettledAt);

        // The completion barrier reports the same failure, named with the conversation it belongs
        // to, rather than leaving the caller with a turn that only looks durable.
        var barrier = await Assert.ThrowsAsync<InvalidOperationException>(
            () => conversation.CheckpointBarrierAsync());

        Assert.Contains($"Conversation '{conversation.Id}' could not commit a checkpoint", barrier.Message);
        Assert.Contains(RefusingCheckpointStore.Refusal, barrier.Message);
        Assert.IsType<IOException>(barrier.InnerException);
    }

    // ----- Duplicate event processing ------------------------------------------------------

    [Fact]
    public async Task DuplicateCheckpointDelivery_IsDeduplicatedEvenWhenDeliveredAtOnce()
    {
        await using var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .UseClient(ReplayClientOptions())
            .Build();

        var conversation = await client.CreateConversationAsync(new AgentChoices { Model = "replay-model" });
        await conversation.SubmitAsync("hello").WaitAsync(EventTimeout);

        var first = Assert.IsType<ConversationCheckpoint>(conversation.Checkpoint());
        var versionsBefore = VersionCount(client.Store, CheckpointDocument, conversation.Id);
        var entriesBefore = client.Store.Entries(conversation.Id).Count;

        // The same event delivered three times concurrently: the identifier is the deduplication
        // key and the writes are serialized, so neither the document nor the transcript grows.
        await Task.WhenAll(
            conversation.CheckpointAsync(first),
            conversation.CheckpointAsync(first),
            conversation.CheckpointAsync(first));
        await conversation.CheckpointBarrierAsync();

        Assert.Equal(versionsBefore, VersionCount(client.Store, CheckpointDocument, conversation.Id));
        Assert.Equal(entriesBefore, client.Store.Entries(conversation.Id).Count);
        Assert.Equal(first.CheckpointId, conversation.Checkpoint()!.CheckpointId);

        // A different event delivered concurrently with itself is written exactly once: the second
        // write deduplicates against what the first committed, not against a stale read.
        var repeated = new ConversationCheckpoint
        {
            CheckpointId = "c-duplicate-probe",
            Kind = "turn",
            EventId = "turn:duplicate-probe",
            Content = "duplicate probe",
            RecordedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        await Task.WhenAll(
            conversation.CheckpointAsync(repeated),
            conversation.CheckpointAsync(repeated));
        await conversation.CheckpointBarrierAsync();

        Assert.Equal(versionsBefore + 1, VersionCount(client.Store, CheckpointDocument, conversation.Id));
        Assert.Equal("c-duplicate-probe", conversation.Checkpoint()!.CheckpointId);
        Assert.Equal("turn:duplicate-probe", conversation.Checkpoint()!.EventId);

        // Re-delivery is not a second turn: the admission the checkpoint belongs to stays settled.
        Assert.Equal("completed", conversation.Admission()!.State);
    }

    // ----- Interrupted forks ---------------------------------------------------------------

    [Fact]
    public async Task InterruptedFork_IsCompletedThroughTheFacadeWithItsInheritedChoices()
    {
        var store = new DurableStore();
        var registry = new ExtensionRegistry();
        string parentId;
        string boundary;
        string forkId;

        await using (var harness = await DurableHarness.OpenAsync(store, registry))
        {
            var parent = await harness.CreateConversationAsync(
                new AgentChoices { Instructions = "inherited at the fork" });

            parentId = parent.Id;
            boundary = await parent.AppendEntryAsync("note", "fork point");

            // The durable half of a fork with no native fork: the state a crash between the commit
            // and the RPC leaves behind.
            forkId = await CommitPendingForkAsync(store, parent, boundary);
        }

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseRegistry(registry)
            .Build();

        // Opening the store is what completes the interrupted fork: the host only asks for its
        // root conversation.
        var root = await client.RootAsync();
        Assert.Equal(parentId, root.Id);

        var fork = Assert.IsType<DurableConversation>(client.FindConversation(forkId));

        var intent = Assert.IsType<ForkIntent>(Stored(store, ForkDocument, forkId));
        Assert.Equal(ForkIntentState.Completed, intent.State);
        Assert.Equal(parentId, intent.ParentConversationId);
        Assert.Equal(boundary, intent.ForkEntryId);
        Assert.Equal(fork.SessionId, intent.SessionId);

        // The fork got its own session rather than inheriting the parent's, and the interrupted
        // half it committed is what the completed fork is built on.
        Assert.NotNull(fork.SessionId);
        Assert.NotEqual(root.SessionId, fork.SessionId);
        Assert.Contains(boundary, fork.Entries(10));

        // The child runs with the choices the parent had at the boundary.
        var choices = Assert.IsType<AgentChoices>(fork.Choices());
        Assert.Equal("inherited at the fork", choices.Instructions);
    }

    // ----- Hook composition ----------------------------------------------------------------

    [Fact]
    public async Task HookComposition_ComposesTheDurableSectionsWithTheApplicationsHooks()
    {
        var registry = new ExtensionRegistry();
        registry.Install(new Extension(
            "preamble",
            sections: [new PromptSection("preamble", _ => Task.FromResult<string?>("Be concise."), tag: false)]));

        await using var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .UseRegistry(registry)
            .UseClient(ReplayClientOptions())
            .Build();

        // Created without stored choices, so the caller's choices are the effective ones.
        var conversation = await client.CreateConversationAsync();

        var hooks = new SessionHooks
        {
            OnUserPromptSubmitted = (input, _) => Task.FromResult<UserPromptSubmittedHookOutput?>(
                new UserPromptSubmittedHookOutput { ModifiedPrompt = $"{input.Prompt} (reviewed)" }),
            OnAgentStop = (_, _) => Task.FromResult<AgentStopHookOutput?>(null),
        };

        var config = client.GetSessionConfig(
            new AgentChoices { Extensions = ["preamble"], Hooks = hooks },
            conversation);

        // The configuration carries the application's hook set, with the hooks it did not ask the
        // durable layer to compose still in place. The set is a copy, so composing onto it leaves
        // the caller's own instance alone.
        Assert.NotSame(hooks, config.Hooks);
        Assert.NotNull(config.Hooks!.OnAgentStop);
        var composed = Assert.IsType<Func<UserPromptSubmittedHookInput, HookInvocation, Task<UserPromptSubmittedHookOutput?>>>(
            config.Hooks.OnUserPromptSubmitted);

        // Invoking it runs the application's own hook first and then appends the rendered section,
        // which is what "compose" has to mean for a host that keeps its own prompt policy.
        var output = await composed(
            new UserPromptSubmittedHookInput { Prompt = "original", SessionId = conversation.SessionId },
            new HookInvocation { SessionId = conversation.SessionId });

        Assert.NotNull(output);
        Assert.Equal("original (reviewed)", output!.ModifiedPrompt);
        Assert.Equal("Be concise.", output.AdditionalContext);

        // The caller's own hook is still the one it supplied, and it still runs on its own.
        var uncomposed = await hooks.OnUserPromptSubmitted!(
            new UserPromptSubmittedHookInput { Prompt = "original", SessionId = conversation.SessionId },
            new HookInvocation { SessionId = conversation.SessionId });
        Assert.Equal("original (reviewed)", uncomposed!.ModifiedPrompt);
        Assert.Null(uncomposed.AdditionalContext);
    }

    [Fact]
    public async Task HookComposition_LeavesTheCallersOwnHooksUnchanged()
    {
        var registry = new ExtensionRegistry();
        registry.Install(new Extension(
            "preamble",
            sections: [new PromptSection("preamble", _ => Task.FromResult<string?>("Be concise."), tag: false)]));

        await using var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .UseRegistry(registry)
            .UseClient(ReplayClientOptions())
            .Build();

        var conversation = await client.CreateConversationAsync();

        var supplied = (UserPromptSubmittedHookInput input, HookInvocation _) =>
            Task.FromResult<UserPromptSubmittedHookOutput?>(
                new UserPromptSubmittedHookOutput { ModifiedPrompt = input.Prompt });

        var hooks = new SessionHooks
        {
            OnUserPromptSubmitted = supplied,
            OnAgentStop = (_, _) => Task.FromResult<AgentStopHookOutput?>(null),
        };

        var choices = new AgentChoices { Extensions = ["preamble"], Hooks = hooks };
        var config = client.GetSessionConfig(choices, conversation);

        // The configuration is built from the caller's hooks, not on top of them.
        Assert.NotSame(hooks, config.Hooks);

        // Building a configuration is a read of the caller's choices, so the caller's own hook set
        // is exactly what it was handed over as.
        Assert.Same(supplied, hooks.OnUserPromptSubmitted);

        // And building twice does not stack the composition: the second configuration composes
        // onto the caller's hook, not onto the first configuration's composed hook.
        var again = client.GetSessionConfig(choices, conversation);
        Assert.NotSame(config.Hooks, again.Hooks);
        Assert.NotSame(config.Hooks!.OnUserPromptSubmitted, again.Hooks!.OnUserPromptSubmitted);
        Assert.Same(supplied, hooks.OnUserPromptSubmitted);

        var recomposed = await again.Hooks.OnUserPromptSubmitted!(
            new UserPromptSubmittedHookInput { Prompt = "original", SessionId = conversation.SessionId },
            new HookInvocation { SessionId = conversation.SessionId });

        Assert.Equal("original", recomposed!.ModifiedPrompt);
        Assert.Equal("Be concise.", recomposed.AdditionalContext);
    }

    // ----- Ownership -----------------------------------------------------------------------

    [Fact]
    public async Task BorrowedStore_OutlivesTheClientAndKeepsTheRecordItWrote()
    {
        var store = DurableStore.CreateEphemeral();
        string conversationId;
        string sessionId;

        await using (var client = new DurableClientBuilder()
            .UseStore(store)
            .UseClient(ReplayClientOptions())
            .Build())
        {
            var conversation = await client.CreateConversationAsync(new AgentChoices { Model = "replay-model" });
            conversationId = conversation.Id;
            sessionId = conversation.SessionId!;

            await conversation.SubmitAsync("hello").WaitAsync(EventTimeout);
        }

        // The store is the caller's, so it is still open and still holds what the client wrote.
        var binding = Assert.IsType<SessionBinding>(Stored(store, BindingDocument, conversationId));
        Assert.Equal(sessionId, binding.SessionId);
        Assert.IsType<ConversationCheckpoint>(Stored(store, CheckpointDocument, conversationId));

        var appended = await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));

        Assert.Equal(2, store.Conversations.Count);
        Assert.Contains(store.Conversations, conversation => conversation.Id == appended.Id);
    }

    [Fact]
    public async Task BorrowedClient_IsLeftRunningAfterTheFacadeIsDisposed()
    {
        await using var native = new CopilotClient(ReplayClientOptions());
        await native.StartAsync();

        await using (var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .UseClient(native)
            .Build())
        {
            var conversation = await client.CreateConversationAsync(new AgentChoices { Model = "replay-model" });
            Assert.NotNull(conversation.SessionId);
        }

        // The client was supplied, so the facade must not stop it: its owner is the caller, and a
        // process the durable layer started is the only one it may shut down.
        var pong = await native.PingAsync("still running");

        Assert.Equal("still running", pong.Message);
    }

    [Fact]
    public async Task OwnedStore_IsDisposedExactlyOnceByTheFacade()
    {
        var client = new DurableClientBuilder()
            .UseStore(new DurableStoreOptions { Format = DurableStoreFormat.InMemory })
            .UseClient(UnstartedClient())
            .Build();

        await client.InitializeAsync();
        var owned = client.Store;

        await client.DisposeAsync();
        Assert.False(client.IsInitialized);

        // A second disposal is a no-op rather than a second teardown, which is what makes it safe
        // in a host's own cleanup path.
        await client.DisposeAsync();
        Assert.False(client.IsInitialized);

        // The store the durable layer created was disposed, exactly once, by the disposal above.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => owned.CommitAsync(tx => Task.FromResult(tx.CreateConversation())));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.InitializeAsync());
    }

    // ----- Historical choices at the fork boundary ------------------------------------------

    [Fact]
    public async Task FacadeFork_AppliesEachDocumentsPolicyAtTheBoundary()
    {
        await using var client = new DurableClientBuilder()
            .UseStore(new DurableStore())
            .UseRegistry(new ExtensionRegistry())
            .Build();

        var asOf = Doc("contract.asof", ForkPolicy.AsOf);
        var current = Doc("contract.current", ForkPolicy.Current);
        var initial = Doc("contract.initial", ForkPolicy.Initial);

        var parent = await client.CreateConversationAsync(
            new AgentChoices { Instructions = "at the boundary" });

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
            var entry = tx.AppendEntry(parent.Id, "note", "after the boundary");
            foreach (var definition in new[] { asOf, current, initial })
            {
                var draft = tx.Doc(definition, parent.Id);
                draft.Value["text"] = "v2";
                draft.Save(entry);
            }

            await Task.CompletedTask;
        });

        // The parent moves on after the boundary. A fork taken at the boundary must not see this,
        // in its documents or in its choices.
        await parent.ConfigureAsync(new AgentChoices { Instructions = "after the boundary" });

        var fork = await client.ForkAsync(parent, boundary);

        Assert.Equal("v1", fork.Snapshot(asOf)!["text"]);
        Assert.Equal("v2", fork.Snapshot(current)!["text"]);
        Assert.Equal("initial", fork.Snapshot(initial)!["text"]);
        Assert.Equal("v2", parent.Snapshot(asOf)!["text"]);

        Assert.Equal("at the boundary", fork.Choices()!.Instructions);
        Assert.Equal("after the boundary", parent.Choices()!.Instructions);

        // The child's documents carry the policy their definition declares, so forking the fork
        // applies the same rule rather than whatever the child happened to be created with.
        Assert.Equal(ForkPolicy.AsOf, client.Store.Document(AgentDocument.Kind, fork.Id)!.Fork);
        Assert.Equal(ForkPolicy.Initial, client.Store.Document(BindingDocument.Kind, fork.Id)!.Fork);
        Assert.Equal(ForkPolicy.Initial, client.Store.Document(ForkDocument.Kind, fork.Id)!.Fork);
    }

    // ----- Sandbox propagation --------------------------------------------------------------

    [Fact]
    public async Task FacadeConfig_ExpandsTheStoredPolicyAgainstTheStoredWorkingDirectory()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(store, new AgentChoices
        {
            Model = "replay-model",
            WorkingDirectory = "/srv/agent-space",
            Sandbox = new DurableSandboxPolicy
            {
                Enabled = true,
                TrustWorkingDirectory = false,
                KeychainAccess = false,
                Auth = new DurableSandboxAuth { Gh = true, Git = false },
                Filesystem = new DurableSandboxFilesystem
                {
                    DeniedPaths = ["{workspace}/private"],
                    ReadonlyPaths = ["{workspace}/docs"],
                    ReadwritePaths = ["{workspace}/src"],
                },
                Network = new DurableSandboxNetwork { AllowedHosts = ["api.github.com"] },
            },
        });

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseClient(UnstartedClient())
            .Build();

        // The configuration is inspected before any session exists. The client is bound to an
        // address nothing listens on, so a session attempt would fail rather than pass quietly.
        var config = await client.GetSessionConfigAsync(conversationId);

        // The stored working directory is the base the workspace token expands against, so the
        // effective policy is the one the stored conversation asked for and not one expressed
        // against the host's own directory.
        Assert.Equal("/srv/agent-space", config.WorkingDirectory);

        var sandbox = Assert.IsType<SandboxConfig>(config.SandboxConfig);
        Assert.True(sandbox.Enabled);
        Assert.True(sandbox.Auth!.Gh);
        Assert.False(sandbox.Auth.Git);
        Assert.False(sandbox.UserPolicy!.Seatbelt!.KeychainAccess);
        Assert.Equal(["/srv/agent-space/private"], sandbox.UserPolicy.Filesystem!.DeniedPaths);
        Assert.Equal(["/srv/agent-space/docs"], sandbox.UserPolicy.Filesystem.ReadonlyPaths);
        Assert.Equal(["/srv/agent-space/src"], sandbox.UserPolicy.Filesystem.ReadwritePaths);
        Assert.Equal(["api.github.com"], sandbox.UserPolicy.Network!.AllowedHosts);

        // TrustWorkingDirectory is a session-level flag rather than part of the sandbox config,
        // and the stored value reaches the create configuration the facade would hand the SDK.
        Assert.Equal(WorkingDirectoryTrust.Session, config.TrustWorkingDirectory);

        // Nothing was created or resumed to answer the question.
        Assert.Null(client.FindConversation(conversationId));
        Assert.Single(store.Conversations);
    }

    [Fact]
    public async Task Resume_WithAnUnappliableStoredPolicy_FailsThroughTheFacadeBeforeAnySession()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(
            store,
            new AgentChoices
            {
                Model = "replay-model",
                Sandbox = new DurableSandboxPolicy
                {
                    Enabled = false,
                    Network = new DurableSandboxNetwork { AllowedHosts = ["api.github.com"] },
                },
            },
            bindSession: "session-unreachable");

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseClient(UnstartedClient())
            .Build();

        // The client cannot reach a session at all, so the policy failure has to be raised while
        // the resume configuration is built: running the session without the protection the stored
        // policy asks for would be a silent downgrade, not a resume.
        var failure = await Assert.ThrowsAsync<DurableSandboxPolicyException>(
            () => client.ResumeConversationAsync(conversationId));

        Assert.Contains("disables the sandbox", failure.Reason);
        Assert.Contains("silent downgrade", failure.Message);
        Assert.IsType<DurableSandboxPolicy>(failure.Policy);

        Assert.Null(client.FindConversation(conversationId));
        Assert.Single(store.Conversations);
    }

    // ----- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// An in-memory provider that refuses exactly the commit that would carry a checkpoint and
    /// accepts every other one, so the failure is one a conversation has to survive rather than
    /// one that stops the whole store.
    /// </summary>
    private sealed class RefusingCheckpointStore : InMemoryStoreProvider
    {
        public const string Refusal = "the store refused the checkpoint write";

        private bool _armed;

        /// <summary>How many commits were refused.</summary>
        public int Refusals { get; private set; }

        public void Arm() => _armed = true;

        public override void Commit()
        {
            if (_armed && Documents.Any(document => document.Kind == CheckpointDocument.Kind))
            {
                _armed = false;
                Refusals++;
                throw new IOException(Refusal);
            }

            base.Commit();
        }
    }

    private static DocumentDefinition<Dictionary<string, string>> Doc(string kind, ForkPolicy policy) =>
        new(kind, DocumentHistory.Rewindable, policy, () => new Dictionary<string, string> { ["text"] = "initial" });

    /// <summary>
    /// Commits a conversation directly into the store, with its agent choices and optionally a
    /// binding to a session id, without opening any session.
    /// </summary>
    private static async Task<string> SeedAsync(
        DurableStore store,
        AgentChoices choices,
        string? bindSession = null)
    {
        var conversation = await store.CommitAsync(tx =>
        {
            var created = tx.CreateConversation();

            var agent = tx.Doc(AgentDocument, created.Id);
            agent.Value = choices;
            agent.Save(tx.AppendEntry(created.Id, "agent"));

            if (bindSession is not null)
            {
                var binding = tx.Doc(BindingDocument, created.Id);
                binding.Value = new SessionBinding { SessionId = bindSession, Model = choices.Model };
                binding.Save(tx.AppendEntry(created.Id, "session-binding"));
            }

            return Task.FromResult(created);
        });

        return conversation.Id;
    }

    private static T? Stored<T>(DurableStore store, DocumentDefinition<T> definition, string conversationId)
        where T : class =>
        store.Document(definition.Kind, conversationId)?.Latest is { } latest
            ? DurableJson.FromElement<T>(latest)
            : null;

    private static int VersionCount<T>(
        DurableStore store,
        DocumentDefinition<T> definition,
        string conversationId) =>
        store.Document(definition.Kind, conversationId)?.Versions.Count ?? 0;

    /// <summary>
    /// Commits the durable half of a fork — record, inherited transcript, inherited documents, and
    /// a <see cref="ForkIntentState.Pending"/> intent — without attempting the native fork.
    /// </summary>
    private static Task<string> CommitPendingForkAsync(
        DurableStore store,
        DurableConversation parent,
        string entryId) =>
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

    /// <summary>A client that was never started: no process is spawned and no RPC is issued.</summary>
    private static CopilotClient UnstartedClient() => new(new CopilotClientOptions
    {
        Connection = RuntimeConnection.ForUri("127.0.0.1:1"),
    });

    private static CopilotClientOptions ReplayClientOptions() => new()
    {
        Connection = RuntimeConnection.ForStdio(ReplayRuntimePath(), ["--fixture", FixturePath("basic.json")]),
    };

    private static string ReplayRuntimePath()
    {
        var name = OperatingSystem.IsWindows() ? "ReplayRuntime.exe" : "ReplayRuntime";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"the replay runtime was not built next to the tests: {path}");
        return path;
    }

    private static string FixturePath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Assert.True(File.Exists(path), $"the fixture was not copied next to the tests: {path}");
        return path;
    }
}
