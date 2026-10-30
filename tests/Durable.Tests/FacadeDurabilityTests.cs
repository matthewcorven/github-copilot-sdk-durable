using System.ComponentModel;
using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// The durable record a facade-created conversation leaves behind: the session it is bound to,
/// the admission of each prompt, and the checkpoint of each completed turn and tool call.
/// </summary>
/// <remarks>
/// The conversation is driven through the recorded runtime, so a turn really is sent and really
/// does complete. The assertions read the store, which is what makes them about durability
/// rather than about the in-memory handle.
/// </remarks>
public class FacadeDurabilityTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    private static readonly DocumentDefinition<AgentChoices> AgentDocument =
        new("durable.agent", DocumentHistory.Rewindable, ForkPolicy.AsOf);

    private static readonly DocumentDefinition<SessionBinding> BindingDocument =
        new("durable.session-binding", DocumentHistory.Rewindable, ForkPolicy.Initial);

    [Fact]
    public async Task Turn_LeavesACheckpointAndASessionBinding()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry(), ReplayOptions());

        var conversation = await harness.CreateConversationAsync(new AgentChoices { Model = "replay-model" });

        var answer = await conversation.SubmitAsync("hello").WaitAsync(EventTimeout);

        Assert.NotNull(answer);
        Assert.Equal("Hello from the recorded transcript.", answer!.Data.Content);

        // The binding names the session the conversation is served by, and it is in the store,
        // not only on the handle.
        var binding = conversation.Binding();
        Assert.NotNull(binding);
        Assert.Equal(conversation.SessionId, binding!.SessionId);
        Assert.Equal("replay-model", binding.Model);

        // The turn's checkpoint is committed by the time the submission returns.
        var checkpoint = conversation.Checkpoint();
        Assert.NotNull(checkpoint);
        Assert.Equal("turn", checkpoint!.Kind);
        Assert.Equal("turn:msg-0001", checkpoint.EventId);
        Assert.Equal("Hello from the recorded transcript.", checkpoint.Content);
    }

    [Fact]
    public async Task DuplicateEvent_DoesNotProduceASecondCheckpoint()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry(), ReplayOptions());

        var conversation = await harness.CreateConversationAsync(new AgentChoices { Model = "replay-model" });
        await conversation.SubmitAsync("hello").WaitAsync(EventTimeout);

        var first = conversation.Checkpoint();
        Assert.NotNull(first);

        var versionsBefore = store.Document("durable.checkpoint", conversation.Id)!.Versions.Count;

        // The same event delivered again: the identifier is the deduplication key, so the
        // document does not grow a second version.
        await conversation.CheckpointAsync(first!);
        await conversation.CheckpointBarrierAsync();

        var versionsAfter = store.Document("durable.checkpoint", conversation.Id)!.Versions.Count;
        Assert.Equal(versionsBefore, versionsAfter);
        Assert.Equal(first!.CheckpointId, conversation.Checkpoint()!.CheckpointId);
    }

    [Fact]
    public async Task Admission_IsPersistedBeforeThePromptIsSent()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry(), ReplayOptions());

        var conversation = await harness.CreateConversationAsync(new AgentChoices { Model = "replay-model" });

        var admissionId = await conversation.AdmitAsync("hello");

        // The admission is committed, and it is committed as "admitted": the prompt has not
        // been sent yet, so the record says exactly that.
        var admitted = conversation.Admission();
        Assert.NotNull(admitted);
        Assert.Equal(admissionId, admitted!.AdmissionId);
        Assert.Equal("admitted", admitted.State);
        Assert.Equal("hello", admitted.Prompt);
        Assert.Null(admitted.SettledAt);

        await conversation.SettleAsync(admissionId, "completed");

        var settled = conversation.Admission();
        Assert.Equal("completed", settled!.State);
        Assert.NotNull(settled.SettledAt);
    }

    [Fact]
    public async Task Submit_SettlesTheAdmissionItCommitted()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry(), ReplayOptions());

        var conversation = await harness.CreateConversationAsync(new AgentChoices { Model = "replay-model" });
        await conversation.SubmitAsync("hello").WaitAsync(EventTimeout);

        var admission = conversation.Admission();
        Assert.NotNull(admission);
        Assert.Equal("completed", admission!.State);
        Assert.Equal("hello", admission.Prompt);
    }

    [Fact]
    public async Task Resume_WithAMissingTranscript_ThrowsRecoveryRequired()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"durable-facade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "store.json");

        try
        {
            string conversationId;
            const string sessionId = "session-not-recorded";

            using (var store = new DurableStore(path))
            {
                // A conversation bound to a session the durable layer did not choose: the id was
                // learned from the runtime after the session existed, so the durable layer cannot
                // ask for it again. This is the shape a record written before session ids were
                // pinned has, and the shape a host that bound a session by hand leaves behind.
                var conversation = await store.CommitAsync(tx =>
                {
                    var created = tx.CreateConversation();

                    var agent = tx.Doc(AgentDocument, created.Id);
                    agent.Value = new AgentChoices { Model = "replay-model" };
                    agent.Save(tx.AppendEntry(created.Id, "agent"));

                    var binding = tx.Doc(BindingDocument, created.Id);
                    binding.Value = new SessionBinding { SessionId = sessionId, Model = "replay-model" };
                    binding.Save(tx.AppendEntry(created.Id, "session-binding"));

                    return Task.FromResult(created);
                });

                conversationId = conversation.Id;
            }

            // The recorded session is not one the runtime knows, which is what a deleted native
            // transcript looks like from the client's side.
            using (var reopened = new DurableStore(path))
            {
                await using var harness = await DurableHarness.OpenAsync(reopened, new ExtensionRegistry(), ReplayOptions());

                var failure = await Assert.ThrowsAsync<DurableRecoveryRequiredException>(
                    () => harness.ResumeConversationAsync(conversationId));

                Assert.Equal(conversationId, failure.ConversationId);
                Assert.Equal(sessionId, failure.SessionId);

                // The failure is explicit and non-destructive: the record is still there, still
                // bound to the session the host has to decide about, and no replacement session
                // was opened behind its back.
                Assert.Single(reopened.Conversations);
                var binding = DurableJson.FromElement<SessionBinding>(
                    reopened.Document(BindingDocument.Kind, conversationId)!.Latest!.Value);
                Assert.Equal(sessionId, binding!.SessionId);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Facade_ExposesTheConversationItCreated()
    {
        await using var client = new DurableClientBuilder()
            .UseStore(DurableStore.CreateEphemeral())
            .UseClient(ReplayClientOptions())
            .Build();

        var conversation = await client.CreateConversationAsync(new AgentChoices { Model = "replay-model" });

        Assert.Same(conversation, client.FindConversation(conversation.Id));
        Assert.Same(conversation, client.FindConversation(c => c.Id == conversation.Id));
        Assert.Null(client.FindConversation("no-such-conversation"));

        var config = await client.GetSessionConfigAsync(conversation.Id);
        Assert.Equal("replay-model", config.Model);
    }

    private static DurableHarnessOptions ReplayOptions() => new()
    {
        RuntimePath = ReplayRuntimePath(),
        RuntimeArguments = ["--fixture", FixturePath("basic.json")],
    };

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
