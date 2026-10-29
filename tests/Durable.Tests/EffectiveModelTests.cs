using GitHub.Copilot;

namespace Durable.Tests;

/// <summary>
/// The model the runtime <i>reported</i>, on open and per turn.
/// </summary>
/// <remarks>
/// The reported model is an observation about a live session, so these tests drive the
/// sequencing rules through <see cref="DurableModelTracker"/> with the runtime's own event
/// types, and then confirm at the harness level that a runtime which reports no model leaves
/// the value explicitly absent rather than falling back to
/// <see cref="DurableHarness.DefaultModel"/>.
/// </remarks>
public class EffectiveModelTests
{
    [Fact]
    public void NotReported_IsDistinguishableFromAReportedModel()
    {
        var report = DurableModelReport.NotReported;

        Assert.Null(report.Model);
        Assert.Null(report.Source);
        Assert.False(report.HasModel);
        Assert.Equal("(no model reported)", report.ToString());
    }

    [Fact]
    public void NotReported_RequiredModel_ThrowsRatherThanInventingTheDefaultModel()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => DurableModelReport.NotReported.RequiredModel);

        Assert.Contains("reported no model", error.Message);
        Assert.DoesNotContain(DurableHarness.DefaultModel, error.Message);
    }

    [Fact]
    public async Task Harness_AfterOpenAsync_ReportsNoModelBecauseNoSessionExistsYet()
    {
        using var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var model = harness.EffectiveModel;

        Assert.False(model.HasModel);
        Assert.Null(model.Model);
        Assert.Throws<InvalidOperationException>(() => model.RequiredModel);
    }

    [Fact]
    public void UntrackedSession_EffectiveModelIsNotReported()
    {
        var tracker = new DurableModelTracker();

        Assert.False(tracker.Effective.HasModel);
        Assert.Empty(tracker.Turns);
    }

    [Fact]
    public void SessionStart_ReportsTheSelectedModelWithoutRecordingATurn()
    {
        var tracker = new DurableModelTracker();

        var report = tracker.Record(Event(
            """{"type":"session.start","data":{"sessionId":"s1","selectedModel":"reported-model","copilotVersion":"1.0.0","producer":"test","startTime":"2026-01-01T00:00:00Z","version":1}}"""));

        Assert.NotNull(report);
        Assert.Equal("reported-model", tracker.Effective.Model);
        Assert.Equal("session", tracker.Effective.Source);
        Assert.True(tracker.Effective.HasModel);

        // A session-level report describes the session, not an assistant pass.
        Assert.Empty(tracker.Turns);
    }

    [Fact]
    public void ModelChange_UpdatesTheReportedModel()
    {
        var tracker = new DurableModelTracker();

        tracker.Record(Event(
            """{"type":"session.start","data":{"sessionId":"s1","selectedModel":"first-model","copilotVersion":"1.0.0","producer":"test","startTime":"2026-01-01T00:00:00Z","version":1}}"""));
        tracker.Record(Event(
            """{"type":"session.model_change","data":{"newModel":"switched-model"}}"""));

        Assert.Equal("switched-model", tracker.Effective.Model);
        Assert.Equal("session", tracker.Effective.Source);
    }

    [Fact]
    public void TurnStartThenUsage_IsOneTurn_AndTheUsageRefinesIt()
    {
        var tracker = new DurableModelTracker();

        tracker.Record(Event(
            """{"type":"assistant.turn_start","data":{"turnId":"t1","model":"turn-model"}}"""));
        tracker.Record(Event(
            """{"type":"assistant.usage","data":{"model":"usage-model"}}"""));

        var turn = Assert.Single(tracker.Turns);
        Assert.Equal("usage-model", turn.Model);
        Assert.Equal("usage", turn.Source);
        Assert.Equal("usage-model", tracker.Effective.Model);
    }

    [Fact]
    public void ClosingTheTurn_StartsANewEntryForTheNextPass()
    {
        var tracker = new DurableModelTracker();

        tracker.RecordTurn("first-model", "turn");
        tracker.CloseTurn();
        tracker.RecordTurn("second-model", "turn");

        Assert.Collection(
            tracker.Turns,
            first => Assert.Equal("first-model", first.Model),
            second => Assert.Equal("second-model", second.Model));
        Assert.Equal("second-model", tracker.Effective.Model);
    }

    [Fact]
    public void APassThatReportsNothing_StaysVisibleAndDoesNotBorrowThePreviousModel()
    {
        var tracker = new DurableModelTracker();

        tracker.RecordTurn("first-model", "turn");
        tracker.CloseTurn();
        tracker.RecordTurn(model: null, source: "usage");

        Assert.Collection(
            tracker.Turns,
            first => Assert.Equal("first-model", first.Model),
            second => Assert.False(second.HasModel));

        // Silence is not a report: the effective value keeps the last model the runtime named.
        Assert.Equal("first-model", tracker.Effective.Model);
    }

    [Fact]
    public void ARefine_DoesNotEraseAModelTheRuntimeAlreadyStated()
    {
        var tracker = new DurableModelTracker();

        tracker.RecordTurn("stated-model", "turn");
        var refine = tracker.RecordTurn(model: "  ", source: "usage");

        Assert.False(refine.HasModel);
        Assert.Equal("stated-model", Assert.Single(tracker.Turns).Model);
    }

    [Fact]
    public void EventsWithoutAModel_AreNotRecorded()
    {
        var tracker = new DurableModelTracker();

        var report = tracker.Record(Event(
            """{"type":"assistant.message","data":{"messageId":"m1","content":"hello"}}"""));

        Assert.Null(report);
        Assert.False(tracker.Effective.HasModel);
        Assert.Empty(tracker.Turns);
    }

    [Fact]
    public async Task Conversation_RuntimeThatReportsNoModel_LeavesTheModelExplicitlyAbsent()
    {
        // A deterministic runtime only reports a model when its recording carries one. The
        // replay runtime in this repo emits assistant messages and tool events and never a
        // model, so this is the shape a session over it produces: events arrive, none of them
        // names a model, and the harness must say so instead of echoing what it asked for.
        await using var runtime = await UnstartedRuntimeAsync();
        using var store = DurableStore.CreateEphemeral();
        var record = await store.CommitAsync(async tx => tx.CreateConversation());
        var conversation = new DurableConversation(runtime, null!, record);

        conversation.Observe(Event(
            """{"type":"assistant.message","data":{"messageId":"m1","content":"hello"}}"""));

        Assert.False(conversation.EffectiveModel.HasModel);
        Assert.Null(conversation.EffectiveModel.Model);
        Assert.NotEqual(DurableHarness.DefaultModel, conversation.EffectiveModel.Model);
        Assert.Throws<InvalidOperationException>(() => conversation.EffectiveModel.RequiredModel);

        // Nothing in the transcript named a model, so no turn has a model to show either.
        Assert.Empty(conversation.TurnModels);

        Assert.Equal("hello", Assert.Single(conversation.Answers));
    }

    [Fact]
    public async Task Conversation_ReportsTheModelTheRuntimeNamed_NotTheOneTheHostRequested()
    {
        await using var runtime = await UnstartedRuntimeAsync();
        using var store = DurableStore.CreateEphemeral();
        var record = await store.CommitAsync(async tx => tx.CreateConversation());
        var conversation = new DurableConversation(runtime, null!, record);

        conversation.Observe(Event(
            """{"type":"session.start","data":{"sessionId":"s1","selectedModel":"reported-model","copilotVersion":"1.0.0","producer":"test","startTime":"2026-01-01T00:00:00Z","version":1}}"""));

        Assert.Equal("reported-model", conversation.EffectiveModel.Model);
        Assert.Equal("session", conversation.EffectiveModel.Source);

        conversation.Observe(Event(
            """{"type":"assistant.turn_start","data":{"turnId":"t1","model":"turn-model"}}"""));
        conversation.Observe(Event(
            """{"type":"assistant.usage","data":{"model":"turn-model"}}"""));
        conversation.Observe(Event(
            """{"type":"assistant.message","data":{"messageId":"m1","content":"hello"}}"""));

        var turn = Assert.Single(conversation.TurnModels);
        Assert.Equal("turn-model", turn.Model);
        Assert.Equal("usage", turn.Source);
        Assert.Equal("turn-model", conversation.EffectiveModel.Model);

        // The conversation reports upward, so the runtime-level value follows the last
        // session or turn rather than the model the host asked for.
        Assert.Equal("turn-model", runtime.EffectiveModel.Model);
    }

    private static SessionEvent Event(string json) => SessionEvent.FromJson(json);

    /// <summary>
    /// A runtime bound to an unstarted client. Nothing is spawned and no RPC is issued: the
    /// observation rules these tests cover are in-process, so connecting a runtime would only
    /// add a dependency that cannot change the result.
    /// </summary>
    private static Task<DurableRuntime> UnstartedRuntimeAsync()
    {
        var runtime = new DurableRuntime(new ExtensionRegistry());
        var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForUri("127.0.0.1:1"),
        });

        runtime.Attach(client, DurableStore.CreateEphemeral(), ownsClient: true, ownsStore: true);
        return Task.FromResult(runtime);
    }
}
