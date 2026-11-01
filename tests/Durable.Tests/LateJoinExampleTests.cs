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
    [Fact]
    public async Task ActualExample_BarrierReadinessFailuresCleanupAndSourceMutations()
    {
        var repository = FindRepository();
        var directory = Directory.CreateTempSubdirectory("durable-late-join-test-");
        try
        {
            var runtime = Path.Combine(directory.FullName, "runtime");
            Directory.CreateDirectory(runtime);
            await File.WriteAllTextAsync(Path.Combine(runtime, "Program.cs"), MockRuntimeSource);
            await WriteProjectAsync(runtime, null);
            AssertSucceeded(await ExecuteAsync("dotnet", ["build", "--nologo", "-v:q"], runtime));
            var executable = Path.Combine(runtime, "bin", "Debug", "net10.0",
                OperatingSystem.IsWindows() ? "Program.exe" : "Program");

            var original = await File.ReadAllTextAsync(Path.Combine(repository, "examples", "20-late-join.cs"));
            // Only the budgets are shortened; all synchronization and assertions are real source.
            var source = string.Join("\n", original.Split('\n').Where(line => !line.StartsWith("#:")))
                .Replace("TimeSpan.FromSeconds(90)", "TimeSpan.FromSeconds(5)", StringComparison.Ordinal)
                .Replace("TimeSpan.FromSeconds(15)", "TimeSpan.FromSeconds(0.5)", StringComparison.Ordinal);
            var example = Path.Combine(directory.FullName, "example");
            Directory.CreateDirectory(example);
            await WriteProjectAsync(example, Path.Combine(repository, "lib", "Copilot.Durable.csproj"));
            await BuildExampleAsync(example, source);

            var success = await RunExampleAsync(example, executable, "delayed-user");
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
            Assert.True(success.Output.IndexOf("snapshot events:", StringComparison.Ordinal) <
                success.Output.IndexOf("live subscription installed", StringComparison.Ordinal));
            Assert.True(success.Output.IndexOf("live subscription installed", StringComparison.Ordinal) <
                success.Output.IndexOf("event text delta:", StringComparison.Ordinal));
            var trace = await File.ReadAllTextAsync(Path.Combine(example, "trace-delayed-user"));
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

            var midJoinError = await RunExampleAsync(example, executable, "mid-join-error");
            AssertFailed(midJoinError, "mock original model failure");
            Assert.DoesNotContain("live subscription installed", midJoinError.Output);

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
                        await EventAsync("session.error", new JsonObject { ["message"] = "mock original model failure", ["errorType"] = "model" });
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
                // Model reporting is asynchronous, after SDK session registration.
                await Task.Delay(100);
                await EventAsync("session.start", new JsonObject
                {
                    ["sessionId"] = session, ["selectedModel"] = "mock-reported-model",
                    ["copilotVersion"] = "mock", ["producer"] = "test",
                    ["startTime"] = DateTimeOffset.UtcNow.ToString("O"), ["version"] = 1,
                });
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
