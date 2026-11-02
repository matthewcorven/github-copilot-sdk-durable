using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Durable.Tests;

/// <summary>
/// End-to-end tests for the runtime host's command line. Every test starts the host as its own
/// process, because the host's contract is a process contract: an exit code, a run result on
/// standard output, and diagnostics on standard error.
/// </summary>
/// <remarks>
/// The runtime the host talks to is the recorded runtime, selected through
/// <c>DURABLE_RUNTIME_PATH</c> and pointed at a fixture with <c>--runtime-arg</c>. Nothing here
/// stands in for the runtime: the host starts it, speaks the protocol to it, and reports what it
/// said.
/// </remarks>
public sealed class RuntimeHostCliTests
{
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(120);

    private const string ReviewerDefinition = """
        {
          "name": "reviewer",
          "description": "Reviews changes.",
          "instructions": "Review the change and report what is wrong with it.",
          "extensions": ["durable", "git", "tests"]
        }
        """;

    [Fact]
    public async Task Help_NamesEveryCommandAndEveryFlag()
    {
        var (exitCode, stdout, _) = await RunHostAsync(["--help"]);

        Assert.Equal(0, exitCode);

        foreach (var command in new[] { "run", "serve", "schedule" })
        {
            Assert.Contains(command, stdout, StringComparison.Ordinal);
        }

        foreach (var flag in new[]
        {
            "--agent",
            "--input",
            "--definition",
            "--store",
            "--format",
            "--new-conversation",
            "--permission-handler",
            "--require-effective-model",
            "--allow-uncontained",
            "--http",
            "--rpc",
            "--port",
            "--dev-tunnel",
            "--runtime-arg",
            "--every",
            "--missed-run",
            "--remove",
            "--now",
            "--passes",
            "--help",
        })
        {
            Assert.Contains(flag, stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Run_ExecutesOneTurnAndPrintsTheModelTheRuntimeReported()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, stdout, stderr) = await RunHostAsync(
            "run",
            "--agent", "reviewer",
            "--input", "review this change",
            "--definition", definitionPath,
            "--store", storePath,
            "--format", "jsonl");

        Assert.Equal(0, exitCode);

        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("reviewer", (string?)result["agent"]);
        Assert.Equal("succeeded", (string?)result["outcome"]);
        Assert.Null(result["error"]);
        Assert.NotNull(result["conversationId"]);
        Assert.NotNull(result["admissionId"]);
        Assert.NotNull(result["checkpointId"]);

        // The model comes from the runtime's own report, which for this recording is
        // "replay-model" — not the harness default, and not anything the definition asked for.
        Assert.True((bool?)result["modelReported"]);
        Assert.Equal("replay-model", (string?)result["effectiveModel"]!["model"]);
        Assert.Equal("session", (string?)result["effectiveModel"]!["source"]);
        Assert.NotEqual(DurableHarness.DefaultModel, (string?)result["effectiveModel"]!["model"]);

        Assert.Contains("effective model: replay-model", stderr, StringComparison.Ordinal);
        Assert.Contains("answer: Hello from the recorded transcript.", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("schedule")]
    [InlineData("serve")]
    public async Task DevTunnel_RequiresHttpServe(string command)
    {
        var (exitCode, _, stderr) = await RunHostAsync(
            command, "--agent", "reviewer", "--dev-tunnel", "--rpc");
        Assert.Equal(2, exitCode);
        Assert.Contains("--dev-tunnel requires serve --http.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_LeavesTheStoreUsableSoTheSecondRunObservesTheFirstRunsState()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var first = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "first",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");
        Assert.Equal(0, first.ExitCode);

        var second = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "second",
            "--store", storePath, "--format", "jsonl");
        Assert.Equal(0, second.ExitCode);

        var firstResult = JsonNode.Parse(first.Stdout)!.AsObject();
        var secondResult = JsonNode.Parse(second.Stdout)!.AsObject();

        // One conversation per agent: the second occurrence joins the first one.
        Assert.Equal((string?)firstResult["conversationId"], (string?)secondResult["conversationId"]);

        // Each occurrence is its own admission.
        Assert.NotEqual((string?)firstResult["admissionId"], (string?)secondResult["admissionId"]);

        // The second run reports the checkpoint the first run wrote, which is what "observes the
        // first run's durable state" means: it read the stored checkpoint rather than starting
        // from nothing.
        Assert.NotNull(firstResult["checkpointId"]);
        Assert.Equal((string?)firstResult["checkpointId"], (string?)secondResult["checkpointId"]);

        // And the store still holds exactly one conversation for the agent.
        using var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = storePath,
            Format = DurableStoreFormat.Jsonl,
        });
        Assert.Single(store.Conversations);
    }

    [Fact]
    public async Task Run_NewConversationStartsASecondConversation()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var first = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "first",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");
        Assert.Equal(0, first.ExitCode);

        var second = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "second", "--new-conversation",
            "--store", storePath, "--format", "jsonl");
        Assert.Equal(0, second.ExitCode);

        var firstResult = JsonNode.Parse(first.Stdout)!.AsObject();
        var secondResult = JsonNode.Parse(second.Stdout)!.AsObject();
        Assert.NotEqual((string?)firstResult["conversationId"], (string?)secondResult["conversationId"]);

        using var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = storePath,
            Format = DurableStoreFormat.Jsonl,
        });
        Assert.Equal(2, store.Conversations.Count);
    }

    [Fact]
    public async Task Run_UnknownAgentExitsNonZeroNamingTheAgentAndListingWhatIsAvailable()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var import = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "first",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");
        Assert.Equal(0, import.ExitCode);

        var (exitCode, stdout, stderr) = await RunHostAsync(
            "run", "--agent", "nobody", "--input", "hello",
            "--store", storePath, "--format", "jsonl");

        Assert.NotEqual(0, exitCode);
        Assert.Equal(string.Empty, stdout.Trim());
        Assert.Contains("nobody", stderr, StringComparison.Ordinal);
        Assert.Contains("reviewer", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReportsPermissionGatedWhenTheDurablePolicyDecides()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, stdout, stderr) = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "hello",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("PermissionGated", (string?)result["containment"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)result["containmentReason"]));
        Assert.Contains("containment: PermissionGated", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReportsSandboxedWhenTheDefinitionEnablesTheSandbox()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", """
            {
              "name": "reviewer",
              "sandbox": { "enabled": true }
            }
            """);

        var (exitCode, stdout, _) = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "hello",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("Sandboxed", (string?)result["containment"]);
    }

    [Fact]
    public async Task Run_ReportsNoneWhenTheHostSuppliesItsOwnPermissionHandler()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, stdout, stderr) = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "hello",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--permission-handler", "approve-all");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("None", (string?)result["containment"]);
        Assert.Contains("warning: containment: None", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_RefusesToStartUncontainedUnlessAllowed()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var refused = await RunHostAsync(
            "serve", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--http", "--permission-handler", "approve-all");

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("refusing to serve", refused.Stderr, StringComparison.Ordinal);
        Assert.Contains("--allow-uncontained", refused.Stderr, StringComparison.Ordinal);

        var withoutTransport = await RunHostAsync(
            "serve", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--permission-handler", "approve-all", "--allow-uncontained");

        Assert.Equal(2, withoutTransport.ExitCode);
        Assert.Contains("--http", withoutTransport.Stderr, StringComparison.Ordinal);
        Assert.Contains("--rpc", withoutTransport.Stderr, StringComparison.Ordinal);

        // Two transports is as ambiguous as none: the host refuses rather than picking one.
        var bothTransports = await RunHostAsync(
            "serve", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--permission-handler", "approve-all", "--allow-uncontained",
            "--http", "--rpc");

        Assert.Equal(2, bothTransports.ExitCode);
        Assert.Contains("exactly one transport", bothTransports.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schedule_RefusesToStartUncontainedUnlessAllowed()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var refused = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--permission-handler", "deny-all");

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("refusing to schedule", refused.Stderr, StringComparison.Ordinal);

        var allowed = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--permission-handler", "deny-all", "--allow-uncontained",
            "--every", "5m", "--passes", "0");

        Assert.Equal(0, allowed.ExitCode);
        Assert.Contains("warning: containment: None", allowed.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReportsNoModelWhenTheRecordingNamesNone()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", """
            {
              "name": "reviewer",
              "model": "a-model-the-recording-never-names"
            }
            """);

        var (exitCode, stdout, stderr) = await RunHostWithFixtureAsync(
            [
                "run", "--agent", "reviewer", "--input", "hello",
                "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            ],
            "no-model.json");

        Assert.Equal(0, exitCode);

        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.False((bool?)result["modelReported"]);
        Assert.Null(result["effectiveModel"]);

        // Neither the requested model nor the harness default may stand in for a report the
        // runtime never made.
        Assert.DoesNotContain("a-model-the-recording-never-names", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(DurableHarness.DefaultModel, stdout, StringComparison.Ordinal);
        Assert.Contains("(no model reported)", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RequireEffectiveModelFailsWhenTheRuntimeReportsNone()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, stdout, stderr) = await RunHostWithFixtureAsync(
            [
                "run", "--agent", "reviewer", "--input", "hello",
                "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
                "--require-effective-model",
            ],
            "no-model.json");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("reported no model", stderr, StringComparison.Ordinal);

        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("agentFailed", (string?)result["outcome"]);
        Assert.False((bool?)result["modelReported"]);
    }

    [Fact]
    public async Task Run_RefusesAnUnknownExtensionNamingTheAgentAndTheExtension()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", """
            {
              "name": "reviewer",
              "extensions": ["durable", "code-review"]
            }
            """);

        var (exitCode, stdout, stderr) = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "hello",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");

        Assert.NotEqual(0, exitCode);
        Assert.Equal(string.Empty, stdout.Trim());
        Assert.Contains("reviewer", stderr, StringComparison.Ordinal);
        Assert.Contains("code-review", stderr, StringComparison.Ordinal);
        Assert.Contains("Installed extensions:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RefusesAStoredDefinitionThatNamesAnExtensionTheHostNoLongerHas()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");

        // The definition reaches the store without passing through the import path, which is the
        // situation a definition imported by an earlier build leaves behind. Validation on every
        // invocation is what turns it into a refusal instead of a silently narrowed run.
        var definition = new AgentDefinition
        {
            Name = "reviewer",
            Extensions = ["code-review"],
        };

        using (var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = storePath,
            Format = DurableStoreFormat.Jsonl,
        }))
        {
            var definitions = new DocumentDefinition<AgentDefinition>(
                AgentDefinition.DocumentKind,
                DocumentHistory.Rewindable,
                ForkPolicy.Initial);
            var ownerId = AgentDefinition.OwnerId(definition.Name);
            await store.CommitAsync(async tx =>
            {
                var draft = tx.Doc(definitions, ownerId);
                draft.Value = definition;
                draft.Save(tx.AppendEntry(ownerId, "agent-definition"));
                await Task.CompletedTask;
            });
        }

        var (exitCode, _, stderr) = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "hello",
            "--store", storePath, "--format", "jsonl");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("reviewer", stderr, StringComparison.Ordinal);
        Assert.Contains("code-review", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RefusesAMalformedPermissionGrant()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", """
            {
              "name": "reviewer",
              "permissions": ["read", "   "]
            }
            """);

        var (exitCode, _, stderr) = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "hello",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("reviewer", stderr, StringComparison.Ordinal);
        Assert.Contains("permission grant", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RefusesASandboxPolicyThatCannotBeApplied()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", """
            {
              "name": "reviewer",
              "sandbox": {
                "enabled": false,
                "network": { "allowOutbound": true }
              }
            }
            """);

        var (exitCode, _, stderr) = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "hello",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("sandbox", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_RefusesAnAgentNameThatDoesNotMatchThePattern()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", """
            { "name": "Reviewer Bot" }
            """);

        var (exitCode, _, stderr) = await RunHostAsync(
            "run", "--agent", "Reviewer Bot", "--input", "hello",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Reviewer Bot", stderr, StringComparison.Ordinal);
        Assert.Contains("^[a-z0-9][a-z0-9._-]*$", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schedule_RegistersARecurringTaskThatSurvivesAHostRestart()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        // First process: register the schedule and stop. Nothing runs, because the first
        // occurrence is an interval away.
        var registered = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--every", "5m", "--missed-run", "coalesce", "--passes", "0",
            "--now", "2026-01-01T00:00:00Z");

        Assert.Equal(0, registered.ExitCode);
        var registeredSchedule = JsonNode.Parse(registered.Stdout)!["schedule"]!;
        Assert.Equal("5m", (string?)registeredSchedule["every"]);
        Assert.Equal("2026-01-01T00:05:00+00:00", (string?)registeredSchedule["nextDue"]);
        Assert.Equal(0, (long?)registeredSchedule["runs"]);

        // Second process: a different host, with no --every and no --definition. It discovers
        // the schedule in the store and drives it, which is the whole point of the record being
        // durable state rather than a timer.
        var driven = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:05:00Z");

        Assert.Equal(0, driven.ExitCode);
        var drivenResult = JsonNode.Parse(driven.Stdout)!.AsObject();
        Assert.Equal(1, (long?)drivenResult["schedule"]!["runs"]);
        Assert.Equal("2026-01-01T00:10:00+00:00", (string?)drivenResult["schedule"]!["nextDue"]);
        Assert.Single(drivenResult["occurrences"]!.AsArray());
    }

    [Fact]
    public async Task Schedule_SkipAbandonsTheMissedWindowAndRunsNothing()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        await RegisterAsync(scratch, storePath, definitionPath, "skip");

        // The host was down from 00:00 to 00:17, so three instants (00:05, 00:10, 00:15) passed
        // unwatched. skip abandons all three: nothing runs, and the cadence resumes at 00:20.
        var (exitCode, stdout, _) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:17:00Z");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Empty(result["occurrences"]!.AsArray());
        Assert.Equal(0, (long?)result["schedule"]!["runs"]);
        Assert.Null(result["schedule"]!["lastRun"]);
        Assert.Equal("2026-01-01T00:20:00+00:00", (string?)result["schedule"]!["nextDue"]);
    }

    [Fact]
    public async Task Schedule_CoalesceCollapsesTheMissedWindowIntoOneRunNow()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        await RegisterAsync(scratch, storePath, definitionPath, "coalesce");

        var (exitCode, stdout, _) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:17:00Z");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();

        // One occurrence, not three, and it stands for the whole missed window: lastRun is the
        // instant the host came back, and the cadence resumes at 00:20.
        Assert.Single(result["occurrences"]!.AsArray());
        Assert.Equal(1, (long?)result["schedule"]!["runs"]);
        Assert.Equal("2026-01-01T00:17:00+00:00", (string?)result["schedule"]!["lastRun"]);
        Assert.Equal("2026-01-01T00:20:00+00:00", (string?)result["schedule"]!["nextDue"]);
    }

    [Fact]
    public async Task Schedule_RunOnceRunsTheOldestMissedInstantAndKeepsTheBacklog()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        await RegisterAsync(scratch, storePath, definitionPath, "run-once");

        var (exitCode, stdout, _) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:17:00Z");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();

        // One occurrence, and it stands for the *oldest* missed instant rather than for now:
        // lastRun is 00:05 and the cadence advances by exactly one interval, so 00:10 and 00:15
        // are still owed to the next pass. That is what distinguishes run-once from coalesce.
        Assert.Single(result["occurrences"]!.AsArray());
        Assert.Equal(1, (long?)result["schedule"]!["runs"]);
        Assert.Equal("2026-01-01T00:05:00+00:00", (string?)result["schedule"]!["lastRun"]);
        Assert.Equal("2026-01-01T00:10:00+00:00", (string?)result["schedule"]!["nextDue"]);
    }

    [Fact]
    public async Task Schedule_RunOnceDrainsTheBacklogOneOccurrencePerPass()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        await RegisterAsync(scratch, storePath, definitionPath, "run-once");

        // The host was down from 00:00 to 00:12, so exactly two instants (00:05 and 00:10) are
        // owed. run-once pays them back one per pass rather than collapsing them, so two passes
        // run two occurrences and leave the cadence at 00:15 with nothing outstanding.
        var (exitCode, stdout, _) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:12:00Z", "--passes", "2");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal(2, result["occurrences"]!.AsArray().Count);
        Assert.Equal(2, (long?)result["schedule"]!["runs"]);
        Assert.Equal("2026-01-01T00:10:00+00:00", (string?)result["schedule"]!["lastRun"]);
        Assert.Equal("2026-01-01T00:15:00+00:00", (string?)result["schedule"]!["nextDue"]);
    }

    [Fact]
    public async Task Schedule_ResumedSleepDoesNotDoubleRunTheSameInstant()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        await RegisterAsync(scratch, storePath, definitionPath, "coalesce");

        var first = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:05:00Z");

        Assert.Equal(0, first.ExitCode);
        var firstResult = JsonNode.Parse(first.Stdout)!.AsObject();
        Assert.Single(firstResult["occurrences"]!.AsArray());
        Assert.Equal(1, (long?)firstResult["schedule"]!["runs"]);

        // A second host starts at the same instant the first one already served. The occurrence
        // was claimed durably before it ran, so the restart finds nothing due and runs nothing.
        var second = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:05:00Z");

        Assert.Equal(0, second.ExitCode);
        var secondResult = JsonNode.Parse(second.Stdout)!.AsObject();
        Assert.Empty(secondResult["occurrences"]!.AsArray());
        Assert.Equal(1, (long?)secondResult["schedule"]!["runs"]);
        Assert.Equal("2026-01-01T00:10:00+00:00", (string?)secondResult["schedule"]!["nextDue"]);
    }

    [Fact]
    public async Task Schedule_RemovingTheStoredRecordStopsTheRuns()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        await RegisterAsync(scratch, storePath, definitionPath, "coalesce");

        var removed = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl", "--remove");

        Assert.Equal(0, removed.ExitCode);
        Assert.False((bool?)JsonNode.Parse(removed.Stdout)!["schedule"]!["enabled"]);

        // The record is still in the store, but disabled, and a disabled schedule is driven
        // exactly like an absent one: the instant that would have been due runs nothing.
        var afterRemoval = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:17:00Z");

        Assert.Equal(0, afterRemoval.ExitCode);
        var result = JsonNode.Parse(afterRemoval.Stdout)!.AsObject();
        Assert.Empty(result["occurrences"]!.AsArray());
        Assert.Equal(0, (long?)result["schedule"]!["runs"]);
        Assert.False((bool?)result["schedule"]!["enabled"]);
    }

    [Fact]
    public async Task Schedule_DeletingTheStoredRecordStopsTheRuns()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        await RegisterAsync(scratch, storePath, definitionPath, "coalesce");

        // Delete the schedule's records from the store outright, rather than disabling them.
        // The scheduler discovers schedules by reading the store, so a store without the record
        // has nothing to drive and the instant that would have been due runs nothing.
        var kept = File.ReadAllLines(storePath)
            .Where(line => !line.Contains("durable.schedule", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(kept);
        File.WriteAllLines(storePath, kept);

        var (exitCode, stdout, _) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:17:00Z");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Null(result["schedule"]);
        Assert.Empty(result["occurrences"]!.AsArray());
    }

    [Fact]
    public async Task Schedule_WithNoPersistedScheduleRunsNothing()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        // Import the definition, then drive a store that holds no schedule at all. The scheduler
        // has nothing to discover, so it runs nothing and reports no schedule.
        var (exitCode, stdout, _) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--now", "2026-01-01T00:17:00Z");

        Assert.Equal(0, exitCode);
        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Null(result["schedule"]);
        Assert.Empty(result["occurrences"]!.AsArray());
    }

    [Fact]
    public async Task Schedule_RejectsRemoveCombinedWithEvery()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, _, stderr) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--remove", "--every", "5m");

        Assert.Equal(2, exitCode);
        Assert.Contains("--remove and --every cannot be combined", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schedule_RejectsAnUnknownMissedRunPolicy()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, _, stderr) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--every", "5m", "--missed-run", "catch-up");

        Assert.Equal(2, exitCode);
        Assert.Contains("catch-up", stderr, StringComparison.Ordinal);
        Assert.Contains("skip, coalesce, and run-once", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schedule_RejectsAnIntervalWithAnUnknownUnit()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, _, stderr) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--every", "5d");

        Assert.Equal(2, exitCode);
        Assert.Contains("unknown unit", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registers a schedule in its own process, so the test that follows is a restart rather
    /// than a continuation.
    /// </summary>
    private static async Task RegisterAsync(
        Scratch scratch,
        string storePath,
        string definitionPath,
        string policy)
    {
        var (exitCode, _, _) = await RunHostAsync(
            "schedule", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
            "--every", "5m", "--missed-run", policy, "--passes", "0",
            "--now", "2026-01-01T00:00:00Z");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Run_RefusesADefinitionForAnotherAgent()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, _, stderr) = await RunHostAsync(
            "run", "--agent", "someone-else", "--input", "hello",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("reviewer", stderr, StringComparison.Ordinal);
        Assert.Contains("someone-else", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RequiresAnInput()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, _, stderr) = await RunHostAsync(
            "run", "--agent", "reviewer",
            "--definition", definitionPath, "--store", storePath, "--format", "jsonl");

        Assert.Equal(2, exitCode);
        Assert.Contains("--input", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommandExitsWithAUsageError()
    {
        var (exitCode, _, stderr) = await RunHostAsync("deploy");

        Assert.Equal(2, exitCode);
        Assert.Contains("deploy", stderr, StringComparison.Ordinal);
        Assert.Contains("run, serve, and schedule", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHostAsync(
        params string[] args) => await RunHostWithFixtureAsync(args, "basic.json");

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHostWithFixtureAsync(
        string[] args,
        string fixture)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HostPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The host resolves its runtime from the environment, and the fixture is a runtime
        // argument, so the recording is chosen the same way a real deployment would choose it.
        startInfo.Environment["DURABLE_RUNTIME_PATH"] = ReplayRuntimePath();
        startInfo.ArgumentList.Add("--runtime-arg");
        startInfo.ArgumentList.Add("--fixture");
        startInfo.ArgumentList.Add("--runtime-arg");
        startInfo.ArgumentList.Add(FixturePath(fixture));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("the runtime host did not start.");

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(HostTimeout);

        return (process.ExitCode, await stdout, await stderr);
    }

    private static string HostPath() => Existing(
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Durable.Runtime.exe" : "Durable.Runtime"));

    private static string ReplayRuntimePath() => Existing(
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ReplayRuntime.exe" : "ReplayRuntime"));

    private static string FixturePath(string name) =>
        Existing(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static string Existing(string path) =>
        File.Exists(path) ? path : throw new FileNotFoundException($"the test asset is missing: '{path}'.", path);

    /// <summary>A scratch directory that is removed when the test finishes.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"durable-host-{Guid.NewGuid():N}");

        public Scratch() => Directory.CreateDirectory(_directory);

        public string PathIn(string name) => Path.Combine(_directory, name);

        public string Write(string name, string content)
        {
            var path = PathIn(name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover scratch directory is not a test failure.
            }
        }
    }
}
