using Durable;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Xunit;

namespace Durable.Tests;

public class ExampleBoundaryTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);
    private static readonly DocumentDefinition<string[]> PermissionReview =
        new("example.permission-review", DocumentHistory.Rewindable, ForkPolicy.AsOf);

    [Fact]
    public async Task CompactionExample_SdkAnswersAndContextAreNotPublicAppEntries()
    {
        using var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry(), ReplayOptions());
        var conversation = await harness.RootAsync(new AgentChoices
        {
            InfiniteSessions = new InfiniteSessionConfig
            {
                Enabled = true,
                BackgroundCompactionThreshold = 0.80,
                BufferExhaustionThreshold = 0.95,
            },
        });

        var answers = new List<AssistantMessageEvent?>();
        foreach (var question in new[]
        {
            "Where should we stay in Lisbon?", "What should we eat?",
        })
        {
            answers.Add(await conversation.SubmitAsync(question).WaitAsync(EventTimeout));
        }

        // basic.json records two turns; the LIVE example must still answer all five.
        Assert.Equal(2, answers.Count);
        Assert.All(answers, answer => Assert.False(string.IsNullOrWhiteSpace(answer?.Data.Content)));
        Assert.NotEmpty(await conversation.ContextAsync().WaitAsync(EventTimeout));
        Assert.Empty(conversation.Entries(100));
        Assert.NotEmpty(store.Entries(conversation.Id));
        Assert.Equal("completed", conversation.Admission()!.State);
        Assert.Equal("turn", conversation.Checkpoint()!.Kind);

        // Protect the actual reference assertion as well as the facade contract.
        var source = ExampleSource("24-compaction.cs");
        Assert.Contains("Check.Equal(5, answers.Count,", source);
        Assert.Contains("answers.All(answer => answer is not null && answer.Data.Content.Trim().Length > 0)", source);
        Assert.Contains("Check.That(context.Count > 0,", source);
        Assert.Contains("Check.Equal(0, stored.Count,", source);
    }

    [Fact]
    public async Task PermissionsExample_AppDocumentProvidesAnAsOfForkBoundaryAndDenialSurvivesReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"durable-example-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.json");
        var effects = 0;
        var tool = CopilotTool.DefineTool(
            () => { effects++; return "counter only; no files deleted"; },
            factoryOptions: new AIFunctionFactoryOptions { Name = "delete_everything" });
        var request = new PermissionRequestCustomTool
        {
            ToolName = "delete_everything",
            ToolDescription = "Counter only; no files deleted",
        };
        var registry = new ExtensionRegistry();
        registry.Install(new Extension("dangerous", tools: [tool]));

        try
        {
            string deniedId;
            string forkId;
            string boundary;
            using (var store = new DurableStore(path))
            {
                await using var harness = await DurableHarness.OpenAsync(store, registry, ReplayOptions());
                var denied = await harness.RootAsync(new AgentChoices { Extensions = ["dangerous"] });
                deniedId = denied.Id;
                Assert.NotNull(await denied.SubmitAsync("permission review").WaitAsync(EventTimeout));
                Assert.IsType<PermissionDecisionReject>(await PermissionPolicy.Handler(denied.Choices()?.Permissions)(
                    request, new PermissionInvocation()));
                Assert.Equal(0, effects);
            }

            // The recording has no sessions.fork. Like ForkTests, use the bundled runtime
            // for session creation/fork only; no model turn is requested in this phase.
            using (var store = new DurableStore(path))
            {
                await using var harness = await DurableHarness.OpenAsync(store, registry);
                var restored = Assert.IsType<DurableConversation>(harness.FindConversation(deniedId));
                Assert.True(restored.Choices()?.Permissions is null or []);
                Assert.IsType<PermissionDecisionReject>(await PermissionPolicy.Handler(restored.Choices()?.Permissions)(
                    request, new PermissionInvocation()));
                Assert.Equal(0, effects);
                var allowed = await harness.CreateConversationAsync(new AgentChoices
                {
                    Extensions = ["dangerous"],
                    Permissions = ["custom-tool:delete_everything"],
                });
                Assert.Empty(allowed.Entries(1));

                // Replay has no permission requests. Exercise the real policy and tool directly,
                // without claiming that its recorded answer proves a native permission decision.
                Assert.IsType<PermissionDecisionApproveOnce>(await PermissionPolicy.Handler(allowed.Choices()?.Permissions)(
                    request, new PermissionInvocation()));
                await tool.InvokeAsync(new AIFunctionArguments());
                Assert.Equal(1, effects);

                boundary = await allowed.CommitAsync(tx =>
                {
                    var entry = tx.AppendEntry(allowed.Id, "permission-review", "reviewed grants");
                    var review = tx.Doc(PermissionReview, allowed.Id);
                    review.Value = allowed.Choices()!.Permissions!.ToArray();
                    review.Save(entry);
                    return Task.FromResult(entry);
                });
                Assert.Equal(boundary, Assert.Single(allowed.Entries(1)));
                Assert.Equal(["custom-tool:delete_everything"], Assert.IsType<string[]>(allowed.Snapshot(PermissionReview)));

                await allowed.ConfigureAsync(new AgentChoices
                {
                    Extensions = ["dangerous"],
                    Permissions = ["custom-tool:delete_everything", "custom-tool:unreviewed_tool"],
                });
                var fork = await allowed.ForkAsync(boundary).WaitAsync(EventTimeout);
                forkId = fork.Id;
                Assert.Equal(["custom-tool:delete_everything", "custom-tool:unreviewed_tool"], allowed.Choices()!.Permissions);
                Assert.Equal(["custom-tool:delete_everything"], fork.Choices()!.Permissions);
                Assert.Equal(["custom-tool:delete_everything"], Assert.IsType<string[]>(fork.Snapshot(PermissionReview)));
                Assert.Equal(boundary, Assert.Single(fork.Entries(10)));
                Assert.False(PermissionPolicy.IsGranted(fork.Choices()!.Permissions, "custom-tool", "unreviewed_tool"));
            }

            using (var store = new DurableStore(path))
            {
                await using var harness = await DurableHarness.OpenAsync(store, registry, ReplayOptions());
                var restored = Assert.IsType<DurableConversation>(harness.FindConversation(deniedId));
                Assert.True(restored.Choices()?.Permissions is null or []);
                Assert.IsType<PermissionDecisionReject>(await PermissionPolicy.Handler(restored.Choices()?.Permissions)(
                    request, new PermissionInvocation()));
                Assert.Equal(1, effects);
                var fork = Assert.IsType<DurableConversation>(harness.FindConversation(forkId));
                Assert.Equal(["custom-tool:delete_everything"], fork.Choices()!.Permissions);
                Assert.Equal(["custom-tool:delete_everything"], Assert.IsType<string[]>(fork.Snapshot(PermissionReview)));
                Assert.Equal(boundary, Assert.Single(fork.Entries(10)));
            }

            Assert.True(PermissionPolicy.IsGranted(["read:/tmp/work"], "read", "/tmp/work/file.txt"));
            Assert.False(PermissionPolicy.IsGranted(["read:/tmp/work"], "read", "/tmp/workshop/file.txt"));
            var source = ExampleSource("31-permissions.cs");
            Assert.DoesNotContain("ForkAsync(allowed.Entries(1)[0])", source);
            Assert.Contains("tx.Doc(permissionReview, allowed.Id)", source);
            Assert.Contains("fork inherited the grants at the reviewed boundary", source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DurableHarnessOptions ReplayOptions()
    {
        var name = OperatingSystem.IsWindows() ? "ReplayRuntime.exe" : "ReplayRuntime";
        var runtime = Path.Combine(AppContext.BaseDirectory, name);
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "basic.json");
        Assert.True(File.Exists(runtime), $"replay runtime not built: {runtime}");
        Assert.True(File.Exists(fixture), $"recorded fixture not copied: {fixture}");
        return new DurableHarnessOptions { RuntimePath = runtime, RuntimeArguments = ["--fixture", fixture] };
    }

    private static string ExampleSource(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "examples", name);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new InvalidOperationException($"reference example not found: {name}");
    }
}
