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
    public async Task Harness_WithARuntimeThatReportsNoModel_ReportsNoModelExplicitly()
    {
        var wrapper = ScriptedRuntimeExecutable();
        using var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(
            store,
            new ExtensionRegistry(),
            new DurableHarnessOptions { RuntimePath = wrapper });

        await harness.CreateConversationAsync(new AgentChoices { Model = "requested-model" });

        // The harness asks for "requested-model" and falls back to its own default only when
        // nothing is requested; neither is evidence of what the runtime did. The runtime here
        // reports nothing, so the harness must say so rather than echoing either value.
        Assert.False(harness.EffectiveModel.HasModel);
        Assert.Null(harness.EffectiveModel.Model);
        Assert.NotEqual(DurableHarness.DefaultModel, harness.EffectiveModel.Model);
        Assert.Throws<InvalidOperationException>(() => harness.EffectiveModel.RequiredModel);
    }

    private static SessionEvent Event(string json) => SessionEvent.FromJson(json);

    /// <summary>
    /// An executable the harness can launch as its runtime. It answers the handshake and the
    /// two calls <see cref="DurableHarness.CreateConversationAsync"/> makes, then reports a
    /// session start and one assistant message that carry no model. Staying silent about the
    /// model is the case the harness has to distinguish from one that substitutes a model.
    /// </summary>
    private static string ScriptedRuntimeExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The scripted runtime is a POSIX shell script.");
        }

        var directory = Directory.CreateTempSubdirectory("durable-model-");
        var script = Path.Combine(directory.FullName, "runtime.sh");

        // JSON-RPC 2.0 over Content-Length framing, one request per frame; the reply's id is
        // copied from the request because the client matches replies by id.
        const string Source = """
                              #!/bin/sh
                              reply() {
                                body=$(printf '{"jsonrpc":"2.0","id":%s,"result":%s}' "$1" "$2")
                                printf 'Content-Length: %s\r\n\r\n%s' "${#body}" "$body"
                              }
                              while IFS= read -r line; do
                                case "$line" in
                                  *'"method":"connect"'*)
                                    reply "$(printf '%s' "$line" | sed 's/.*"id":\([0-9]*\).*/\1/')" '{"ok":true}' ;;
                                  *'"method":"session.create"'*)
                                    reply "$(printf '%s' "$line" | sed 's/.*"id":\([0-9]*\).*/\1/')" '{"sessionId":"session-scripted-1"}' ;;
                                  *'"method":"session.send"'*)
                                    reply "$(printf '%s' "$line" | sed 's/.*"id":\([0-9]*\).*/\1/')" '{"messageId":"msg-0001"}' ;;
                                esac
                              done
                              """;

        File.WriteAllText(script, Source);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static string ReplayRuntimePath()
    {
        var name = OperatingSystem.IsWindows() ? "ReplayRuntime.exe" : "ReplayRuntime";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"the replay runtime was not built next to the tests: {path}");
        return path;
    }
}
