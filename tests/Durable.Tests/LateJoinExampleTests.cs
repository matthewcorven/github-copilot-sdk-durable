using System.Diagnostics;
using System.Security;
using System.Text.Json.Nodes;

namespace Durable.Tests;

/// <summary>
/// Deterministic SDK-transport evidence, not LIVE model evidence. Runs the actual example
/// against a minimal wire peer: the real SDK dispatches the async tool and the real durable
/// facade admits the prompt, reads history, observes deltas, and settles the run.
/// </summary>
public class LateJoinExampleTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ActualExample_BarrierReadinessFailuresCleanupAndSourceMutations(string sourceLineEnding)
    {
        var repository = FindRepository();
        var directory = Directory.CreateTempSubdirectory("durable-late-join-test-");
        try
        {
            var runtime = Path.Combine(directory.FullName, "runtime");
            Directory.CreateDirectory(runtime);
            var runtimeSource = MockRuntimeSource.ReplaceLineEndings("\n");
            await File.WriteAllTextAsync(Path.Combine(runtime, "Program.cs"), runtimeSource);
            await WriteProjectAsync(runtime, null);
            AssertSucceeded(await ExecuteAsync("dotnet", ["build", "--nologo", "-v:q"], runtime));
            var executable = Path.Combine(runtime, "bin", "Debug", "net10.0",
                OperatingSystem.IsWindows() ? "Program.exe" : "Program");

            var original = (await File.ReadAllTextAsync(Path.Combine(repository, "examples", "20-late-join.cs")))
                .ReplaceLineEndings(sourceLineEnding);
            // Only budgets and transport-readiness instrumentation differ from the real source.
            var source = string.Join("\n", original.Split('\n').Where(line => !line.StartsWith("#:")))
                .Replace("TimeSpan.FromSeconds(90)", "TimeSpan.FromSeconds(5)", StringComparison.Ordinal)
                .Replace("TimeSpan.FromSeconds(15)", "TimeSpan.FromSeconds(0.5)", StringComparison.Ordinal);
            const string modelWait = "// The model the runtime reported for this session, not the one the host asked for.";
            Assert.Contains(modelWait, source);
            source = source.Replace(modelWait, """
                // RootAsync has returned: the real DurableConversation.Attach observer is installed.
                using (var observerReady = new System.IO.Pipes.NamedPipeClientStream(
                    ".", Environment.GetEnvironmentVariable("LATE_JOIN_READY_PIPE")!,
                    System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
                {
                    await observerReady.ConnectAsync(deadline.Token);
                    var probe = new byte[1];
                    await observerReady.ReadExactlyAsync(probe, deadline.Token);
                    if (probe[0] != 1)
                        throw new InvalidOperationException("Invalid model readiness probe.");
                    // The peer probes us before we release readiness, so this is a deliberately
                    // delayed signal, not elapsed-time evidence that attachment has finished.
                    var wire = File.ReadAllText("wire-" + Environment.GetEnvironmentVariable("LATE_JOIN_MOCK_SCENARIO"));
                    if (wire.Contains("\"type\":\"session.start\"", StringComparison.Ordinal))
                        throw new InvalidOperationException("Model report preceded observer readiness.");
                    await observerReady.WriteAsync(new byte[] { 1 }, deadline.Token);
                    await observerReady.FlushAsync(deadline.Token);
                }

                """ + modelWait, StringComparison.Ordinal);
            var example = Path.Combine(directory.FullName, "example");
            Directory.CreateDirectory(example);
            await WriteProjectAsync(example, Path.Combine(repository, "lib", "Copilot.Durable.csproj"));
            await BuildExampleAsync(example, source);

            var success = await RunExampleAsync(example, executable, "delayed-user");
            AssertSucceeded(success);
            var startEvent = (await File.ReadAllLinesAsync(Path.Combine(example, "wire-delayed-user")))
                .Select(line => JsonNode.Parse(line)!)
                .Single(message => message["params"]?["event"]?["type"]?.GetValue<string>() == "session.start");
            Assert.IsType<GitHub.Copilot.SessionStartEvent>(
                GitHub.Copilot.SessionEvent.FromJson(startEvent["params"]!["event"]!.ToJsonString()));
            AssertSucceeded(success with { Output = success.Output + "\nWire trace:\n" +
                await File.ReadAllTextAsync(Path.Combine(example, "wire-delayed-user")) });
            Assert.Contains("effective model: mock-reported-model", success.Output);
            Assert.Contains("  user.message", success.Output);
            Assert.Contains("event text delta: One. Two. Three. Four. Five. DONE", success.Output);
            Assert.Contains("answer: One. Two. Three. Four. Five. DONE", success.Output);
            Assert.Contains("deltas received after joining: 1", success.Output);
            AssertJoinOutputOrder(success);
            var trace = await File.ReadAllTextAsync(Path.Combine(example, "trace-delayed-user"));
            Assert.Contains("model-observer-ready", trace);
            Assert.Contains("getMessages:3", trace);
            Assert.Contains("tool-result-after-snapshot:True", trace);
            Assert.Contains("The client has joined.", trace);
            var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(example, "config-delayed-user")))!;
            Assert.True(config["streaming"]!.GetValue<bool>());
            Assert.Equal("wait_for_join", Assert.Single(config["tools"]!.AsArray())!["name"]!.GetValue<string>());
            // The real durable configuration preserves exact selection and fail-closed grants.
            Assert.Contains("Tools = [\"wait_for_join\"]", original);
            Assert.Contains("Permissions = [\"custom-tool:wait_for_join\"]", original);
            Assert.DoesNotContain("EnableFileHooks", original);
            Assert.DoesNotContain("\n    Model =", original);

            // Force the released tool to deliver a delta before the releasing continuation
            // proceeds. The old release-before-log ordering deterministically fails the same
            // output assertion used above, even though all five example assertions pass.
            var adversarial = source.Replace(
                "var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);",
                """
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var firstDelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                """, StringComparison.Ordinal).Replace(
                "Console.WriteLine($\"event text delta: {delta.Data.DeltaContent}\");",
                """
                Console.WriteLine($"event text delta: {delta.Data.DeltaContent}");
                firstDelta.TrySetResult();
                """, StringComparison.Ordinal);
            var releaseIndex = adversarial.IndexOf("    release.TrySetResult();", StringComparison.Ordinal);
            Assert.True(releaseIndex >= 0);
            adversarial = adversarial.Insert(releaseIndex + "    release.TrySetResult();".Length,
                "\n    await firstDelta.Task.WaitAsync(deadline.Token);").ReplaceLineEndings("\n");
            await BuildExampleAsync(example, adversarial);
            var concurrentRelease = await RunExampleAsync(example, executable, "concurrent-release");
            AssertSucceeded(concurrentRelease);
            AssertJoinOutputOrder(concurrentRelease);
            var oldLogOrder = adversarial.Replace(
                """
                    Console.WriteLine("live subscription installed; synchronization tool released");
                    release.TrySetResult();
                    await firstDelta.Task.WaitAsync(deadline.Token);
                """.ReplaceLineEndings("\n"),
                """
                    release.TrySetResult();
                    await firstDelta.Task.WaitAsync(deadline.Token);
                    Console.WriteLine("live subscription installed; synchronization tool released");
                """.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.NotEqual(adversarial, oldLogOrder);
            await BuildExampleAsync(example, oldLogOrder);
            var logMutation = await RunExampleAsync(example, executable, "old-log-order");
            AssertSucceeded(logMutation);
            Assert.False(HasJoinOutputOrder(logMutation), logMutation.Output);

            // Restore the old time-based model report while deliberately withholding the
            // generated example's ready signal until the peer probes it. The wire artifact
            // must reject that premature one-shot event, independent of thread scheduling.
            const string readinessProbe = "await observerReady.WriteAsync(new byte[] { 1 });";
            var oldModelTiming = runtimeSource.Replace(readinessProbe, """
                await Task.Delay(100);
                await ReportModelAsync();
                """ + "\n" + readinessProbe, StringComparison.Ordinal);
            Assert.NotEqual(runtimeSource, oldModelTiming);
            await File.WriteAllTextAsync(Path.Combine(runtime, "Program.cs"), oldModelTiming);
            AssertSucceeded(await ExecuteAsync("dotnet", ["build", "--nologo", "-v:q"], runtime));
            await BuildExampleAsync(example, source);
            var readinessMutation = await RunExampleAsync(example, executable, "old-model-timing");
            AssertFailed(readinessMutation, "Model report preceded observer readiness.");
            await File.WriteAllTextAsync(Path.Combine(runtime, "Program.cs"), runtimeSource);
            AssertSucceeded(await ExecuteAsync("dotnet", ["build", "--nologo", "-v:q"], runtime));

            var noTool = await RunExampleAsync(example, executable, "no-tool");
            AssertFailed(noTool, "Late join timed out while waiting for wait_for_join");

            var early = await RunExampleAsync(example, executable, "early");
            AssertFailed(early, "The run ended before joining while waiting for wait_for_join");

            var error = await RunExampleAsync(example, executable, "error");
            AssertFailed(error, "mock original model failure");
            Assert.DoesNotContain("The run ended before joining", error.Output);

            var missingUser = await RunExampleAsync(example, executable, "missing-user");
            AssertFailed(missingUser, "actual UserMessageEvent");
            Assert.DoesNotContain("live subscription installed", missingUser.Output);
            Assert.DoesNotContain("event text delta:", missingUser.Output);

            var stalledRead = await RunExampleAsync(example, executable, "stalled-read");
            AssertFailed(stalledRead, "Late join timed out while reading the SDK snapshot");

            // Hold the real store's admission settlement until the successful snapshot has
            // installed its subscription. This makes the old peer's race causal, not a sleep.
            var heldSettlement = source.Replace("var run = root.SubmitAsync(", """
                var settlementRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task settlement = Task.CompletedTask;
                var run = root.SubmitAsync(
                """, StringComparison.Ordinal).Replace(
                "Console.WriteLine(\"synchronization tool started; joining while the run is pending\");", """
                Console.WriteLine("synchronization tool started; joining while the run is pending");
                var settlementHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                settlement = store.CommitAsync(async _ =>
                {
                    settlementHeld.TrySetResult();
                    await settlementRelease.Task.WaitAsync(deadline.Token);
                });
                await settlementHeld.Task.WaitAsync(deadline.Token);
                """, StringComparison.Ordinal).Replace(
                "Console.WriteLine(\"live subscription installed; synchronization tool released\");", """
                Console.WriteLine("live subscription installed; synchronization tool released");
                settlementRelease.TrySetResult();
                await settlement;
                """, StringComparison.Ordinal).Replace("    deadline.Cancel();", """
                    settlementRelease.TrySetResult();
                    await settlement;
                    deadline.Cancel();
                """, StringComparison.Ordinal);
            Assert.NotEqual(source, heldSettlement);
            const string midJoinFailure = """
                    if (scenario == "mid-join-error")
                    {
                        await EventAsync("session.error", new JsonObject { ["message"] = "mock original model failure", ["errorType"] = "model" });
                        // A successful snapshot would race the asynchronous admission settlement.
                        continue;
                    }
        """;
            var racingSnapshot = runtimeSource.Replace(midJoinFailure.ReplaceLineEndings("\n"),
                """
                    if (scenario == "mid-join-error")
                        await EventAsync("session.error", new JsonObject { ["message"] = "mock original model failure", ["errorType"] = "model" });
        """.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.NotEqual(runtimeSource, racingSnapshot);
            await File.WriteAllTextAsync(Path.Combine(runtime, "Program.cs"), racingSnapshot);
            AssertSucceeded(await ExecuteAsync("dotnet", ["build", "--nologo", "-v:q"], runtime));
            await BuildExampleAsync(example, heldSettlement);
            var snapshotRace = await RunExampleAsync(example, executable, "mid-join-error");
            AssertFailed(snapshotRace, "mock original model failure");
            Assert.Contains("live subscription installed", snapshotRace.Output);
            await File.WriteAllTextAsync(Path.Combine(runtime, "Program.cs"), runtimeSource);
            AssertSucceeded(await ExecuteAsync("dotnet", ["build", "--nologo", "-v:q"], runtime));
            await BuildExampleAsync(example, source);

            var midJoinError = await RunExampleAsync(example, executable, "mid-join-error");
            AssertFailed(midJoinError, "mock original model failure");
            Assert.DoesNotContain("live subscription installed", midJoinError.Output);
            var midJoinWire = (await File.ReadAllLinesAsync(Path.Combine(example, "wire-mid-join-error")))
                .Select(line => JsonNode.Parse(line)!).ToArray();
            var snapshotRequest = Assert.Single(midJoinWire, message =>
                message["method"]?.GetValue<string>() == "session.getMessages");
            Assert.DoesNotContain(midJoinWire, message =>
                message["result"] is not null &&
                JsonNode.DeepEquals(message["id"], snapshotRequest["id"]));

            var noDelta = await RunExampleAsync(example, executable, "no-delta");
            AssertFailed(noDelta, "the late joiner received live deltas");
            Assert.Contains("deltas received after joining: 0", noDelta.Output);
            var emptyAnswer = await RunExampleAsync(example, executable, "empty-answer");
            AssertFailed(emptyAnswer, "the answer is not empty");
            var noIdle = await RunExampleAsync(example, executable, "no-idle");
            AssertFailed(noIdle, "Late join timed out while awaiting the completed counting answer");

            // Instrument the actual handler's finally, not a second implementation of the gate.
            var instrumented = source.Replace(
                "await release.Task.WaitAsync(linked.Token);",
                """
                try { await release.Task.WaitAsync(linked.Token); }
                finally { Console.Error.WriteLine("actual SDK handler exited"); }
                """, StringComparison.Ordinal);
            Assert.NotEqual(source, instrumented);
            await BuildExampleAsync(example, instrumented);
            var cleanup = await RunExampleAsync(example, executable, "missing-user");
            AssertFailed(cleanup, "actual UserMessageEvent");
            Assert.Contains("actual SDK handler exited", cleanup.Output);
            var errorCleanup = await RunExampleAsync(example, executable, "mid-join-error");
            AssertFailed(errorCleanup, "mock original model failure");
            Assert.Contains("actual SDK handler exited", errorCleanup.Output);

            // Real-source mutation: restoring an uncoordinated delay lets a short turn finish
            // before joining. This must be red, never a replay-shaped success.
            var oldDelay = source.Replace(
                "await RequirePendingRunAsync(run, started.Task, deadline.Token, \"waiting for wait_for_join\");",
                "await Task.Delay(1500);", StringComparison.Ordinal);
            Assert.NotEqual(source, oldDelay);
            await BuildExampleAsync(example, oldDelay);
            var delayMutation = await RunExampleAsync(example, executable, "early");
            AssertFailed(delayMutation, "The run ended before joining");

            // Real-source mutation: releasing before the snapshot creates the old gap.
            var gap = source.Replace(
                "var read = root.ContextAsync(snapshotDeadline.Token);",
                "release.TrySetResult();\nvar read = root.ContextAsync(snapshotDeadline.Token);",
                StringComparison.Ordinal);
            Assert.NotEqual(source, gap);
            await BuildExampleAsync(example, gap);
            var gapMutation = await RunExampleAsync(example, executable, "gap");
            Assert.NotEqual(0, gapMutation.ExitCode);
            // The facade may still be committing the turn checkpoint when history arrives.
            // Either the early-completion guard or the actual live-delta assertion must reject it.
            Assert.True(gapMutation.Output.Contains("The run ended before joining", StringComparison.Ordinal) ||
                gapMutation.Output.Contains("the late joiner received live deltas", StringComparison.Ordinal),
                gapMutation.Output);
            Assert.DoesNotContain("event text delta:", gapMutation.Output);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task WriteProjectAsync(string directory, string? reference)
    {
        var item = reference is null ? "" :
            $"<ItemGroup><ProjectReference Include=\"{SecurityElement.Escape(reference)}\" /></ItemGroup>";
        await File.WriteAllTextAsync(Path.Combine(directory, "Program.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NoWarn>GHCP001</NoWarn>
              </PropertyGroup>
              {item}
            </Project>
            """);
    }

    private static async Task BuildExampleAsync(string directory, string source)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "Program.cs"), source);
        AssertSucceeded(await ExecuteAsync("dotnet", ["build", "--nologo", "-v:q"], directory));
    }

    private static Task<ProcessResult> RunExampleAsync(string directory, string runtime, string scenario) =>
        ExecuteAsync("dotnet", [Path.Combine(directory, "bin", "Debug", "net10.0", "Program.dll")],
            directory, runtime, scenario);

    private static async Task<ProcessResult> ExecuteAsync(
        string command, string[] arguments, string directory, string? runtime = null, string? scenario = null)
    {
        var start = new ProcessStartInfo(command)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Child-only environment: no mutation of the test runner's runtime/auth configuration.
        if (runtime is not null)
        {
            start.Environment["DURABLE_RUNTIME_PATH"] = runtime;
            start.Environment["LATE_JOIN_MOCK_SCENARIO"] = scenario!;
            start.Environment["LATE_JOIN_READY_PIPE"] = "lj-" + Guid.NewGuid().ToString("N");
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
            return new ProcessResult(process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void AssertSucceeded(ProcessResult result) =>
        Assert.True(result.ExitCode == 0, result.Output);

    private static bool HasJoinOutputOrder(ProcessResult result)
    {
        var snapshot = result.Output.IndexOf("snapshot events:", StringComparison.Ordinal);
        var installed = result.Output.IndexOf("live subscription installed", StringComparison.Ordinal);
        var delta = result.Output.IndexOf("event text delta:", StringComparison.Ordinal);
        return snapshot >= 0 && installed > snapshot && delta > installed;
    }

    private static void AssertJoinOutputOrder(ProcessResult result) =>
        Assert.True(HasJoinOutputOrder(result), result.Output);

    private static void AssertFailed(ProcessResult result, string diagnostic)
    {
        Assert.True(result.ExitCode != 0, $"Expected failure containing '{diagnostic}':\n{result.Output}");
        Assert.True(result.Output.Contains(diagnostic, StringComparison.Ordinal), result.Output);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "examples", "20-late-join.cs")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Cannot locate the actual examples/20-late-join.cs source.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    // Only a protocol peer; no durable state machine, app tool handler, or synthetic app history.
    // ReplayRuntime's current fixture schema cannot encode user.message or streaming deltas.
    private const string MockRuntimeSource = """"
        using System.Text;
        using System.Text.Json.Nodes;

        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var scenario = Environment.GetEnvironmentVariable("LATE_JOIN_MOCK_SCENARIO")!;
        File.WriteAllText("wire-" + scenario, "");
        using var observerReady = new System.IO.Pipes.NamedPipeServerStream(
            Environment.GetEnvironmentVariable("LATE_JOIN_READY_PIPE")!,
            System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);
        var history = new JsonArray();
        var session = "";
        var reads = 0;
        var resultReceived = false;
        var userAdded = false;
        JsonNode? gapRead = null;
        var request = new JsonObject
        {
            ["requestId"] = "join-request", ["toolCallId"] = "join-call",
            ["toolName"] = "wait_for_join", ["arguments"] = new JsonObject(),
        };
        while (await ReadAsync() is { } message)
        {
            var id = message["id"];
            if (id is null) continue;
            var method = message["method"]!.GetValue<string>();
            var parameters = message["params"];
            File.AppendAllText("wire-" + scenario, message.ToJsonString() + "\n");
            JsonNode result;
            switch (method)
            {
                case "connect":
                    result = new JsonObject { ["ok"] = true, ["protocolVersion"] = 3, ["version"] = "mock" };
                    break;
                case "ping":
                    result = new JsonObject { ["message"] = "pong", ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") };
                    break;
                case "models.list":
                    result = new JsonObject { ["models"] = new JsonArray() };
                    break;
                case "session.create":
                    session = parameters!["sessionId"]?.GetValue<string>() ?? "mock-session";
                    File.WriteAllText("config-" + scenario, parameters!.ToJsonString());
                    result = new JsonObject { ["sessionId"] = session };
                    break;
                case "session.send":
                    if (scenario is not "delayed-user" and not "missing-user") await AddUserAsync();
                    result = new JsonObject { ["messageId"] = "user-1" };
                    break;
                case "session.getMessages":
                    reads++;
                    File.AppendAllText("trace-" + scenario, $"getMessages:{reads}\n");
                    if (scenario == "stalled-read") continue;
                    if (scenario == "delayed-user" && reads >= 3) await AddUserAsync();
                    if (scenario == "mid-join-error")
                    {
                        await EventAsync("session.error", new JsonObject { ["message"] = "mock original model failure", ["errorType"] = "model" });
                        // A successful snapshot would race the asynchronous admission settlement.
                        continue;
                    }
                    // Force generation into the snapshot/subscription gap if the source releases early.
                    if (scenario == "gap" && !resultReceived)
                    {
                        gapRead = id.DeepClone();
                        continue;
                    }
                    result = new JsonObject { ["events"] = history.DeepClone() };
                    break;
                case "session.tools.listPendingRequests":
                    result = new JsonObject { ["items"] = resultReceived ? new JsonArray() : new JsonArray(request.DeepClone()) };
                    break;
                case "session.tools.handlePendingToolCall":
                    resultReceived = true;
                    File.AppendAllText("trace-" + scenario,
                        $"tool-result-after-snapshot:{reads >= 1}\n{parameters!.ToJsonString()}\n");
                    result = new JsonObject { ["success"] = true };
                    break;
                case "session.abort":
                    result = new JsonObject { ["aborted"] = true };
                    break;
                case "session.detach":
                case "session.delete":
                case "session.tools.set":
                case "runtime.shutdown":
                    result = new JsonObject { ["success"] = true };
                    break;
                default:
                    throw new InvalidOperationException("Unimplemented mock method: " + method);
            }
            await ReplyAsync(id, result);
            if (method == "session.create")
            {
                // Only the generated example can signal that RootAsync (including Attach)
                // returned. SDK registration or an elapsed delay cannot prove that.
                using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await observerReady.WaitForConnectionAsync(readyDeadline.Token);
                await observerReady.WriteAsync(new byte[] { 1 });
                await observerReady.FlushAsync(readyDeadline.Token);
                var ready = new byte[1];
                await observerReady.ReadExactlyAsync(ready, readyDeadline.Token);
                if (ready[0] != 1) throw new InvalidOperationException("Invalid observer readiness signal.");
                File.AppendAllText("trace-" + scenario, "model-observer-ready\n");
                await ReportModelAsync();
            }
            if (method == "session.send")
            {
                if (scenario == "early") await AnswerAsync();
                else if (scenario == "error")
                    await EventAsync("session.error", new JsonObject { ["message"] = "mock original model failure", ["errorType"] = "model" });
                else if (scenario != "no-tool")
                {
                    var call = request.DeepClone().AsObject();
                    call["sessionId"] = session;
                    await EventAsync("external_tool.requested", call);
                }
            }
            if (method == "session.tools.handlePendingToolCall")
            {
                await AnswerAsync();
                if (gapRead is not null)
                    await ReplyAsync(gapRead, new JsonObject { ["events"] = history.DeepClone() });
            }
        }

        Task ReportModelAsync() => EventAsync("session.start", new JsonObject
        {
            ["sessionId"] = session, ["selectedModel"] = "mock-reported-model",
            ["copilotVersion"] = "mock", ["producer"] = "test",
            ["startTime"] = DateTimeOffset.UtcNow.ToString("O"), ["version"] = 1,
        });

        async Task AddUserAsync()
        {
            if (userAdded) return;
            userAdded = true;
            await EventAsync("user.message", new JsonObject { ["content"] = "Count from one to five, then DONE." });
        }

        async Task AnswerAsync()
        {
            const string answer = "One. Two. Three. Four. Five. DONE";
            if (scenario != "no-delta")
                await EventAsync("assistant.message_delta", new JsonObject { ["messageId"] = "answer-1", ["deltaContent"] = answer });
            await EventAsync("assistant.message", new JsonObject { ["messageId"] = "answer-1", ["content"] = scenario == "empty-answer" ? "" : answer });
            if (scenario != "no-idle")
                await EventAsync("session.idle", new JsonObject { ["aborted"] = false });
        }

        async Task EventAsync(string type, JsonObject data)
        {
            var evt = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(), ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["parentId"] = null, ["type"] = type, ["data"] = data,
            };
            history.Add(evt.DeepClone());
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["method"] = "session.event",
                ["params"] = new JsonObject { ["sessionId"] = session, ["event"] = evt },
            });
        }

        Task ReplyAsync(JsonNode id, JsonNode result) => WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result,
        });

        async Task WriteAsync(JsonObject message)
        {
            File.AppendAllText("wire-" + scenario, message.ToJsonString() + "\n");
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await output.WriteAsync(body);
            await output.FlushAsync();
        }

        async Task<JsonObject?> ReadAsync()
        {
            var header = new List<byte>();
            var one = new byte[1];
            while (await input.ReadAsync(one) != 0)
            {
                header.Add(one[0]);
                if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
            }
            if (header.Count == 0) return null;
            var text = Encoding.ASCII.GetString(header.ToArray());
            var length = int.Parse(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
            var body = new byte[length];
            await input.ReadExactlyAsync(body);
            return JsonNode.Parse(body)!.AsObject();
        }
        """";
}
