using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using ReplayRuntime;

namespace Durable.Tests;

/// <summary>
/// The recorded runtime driving a real tool handler end to end: the SDK client executes the
/// handler, sends the result back over <c>session.tools.handlePendingToolCall</c>, and the
/// runtime continues with the next recorded turn.
/// </summary>
/// <remarks>
/// These tests spawn the built <c>ReplayRuntime</c> executable and talk to it through a real
/// <see cref="CopilotClient"/>, so the assertions cover the wire protocol rather than the
/// in-process host. The handler is a real <see cref="AIFunction"/>; nothing about its result
/// is recorded in the fixture.
/// </remarks>
public class ReplayToolDispatchTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ToolRoundTrip_RealHandlerResultIsWhatTheRuntimeReceives()
    {
        var invokedPaths = new List<string>();
        var toolResults = new List<ToolExecutionCompleteEvent>();
        var secondTurn = new TaskCompletionSource<AssistantMessageEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var readFile = CopilotTool.DefineTool(
            ([Description("File to read, relative to the working directory")] string path) =>
            {
                lock (invokedPaths)
                {
                    invokedPaths.Add(path);
                }

                return "hello world\n";
            },
            factoryOptions: new AIFunctionFactoryOptions { Name = "read_file", Description = "Read a file" });

        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(ReplayRuntimePath(), ["--fixture", FixturePath("tool-round-trip.json")]),
        });

        await client.StartAsync();

        var session = await client.CreateSessionAsync(new SessionConfig
        {
            SessionId = "session-tool-round-trip",
            Model = "replay-model",
            Tools = [readFile],
        });

        using var subscription = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case ToolExecutionCompleteEvent complete:
                    lock (toolResults)
                    {
                        toolResults.Add(complete);
                    }

                    break;
                case AssistantMessageEvent message when message.Data.MessageId == "msg-0002":
                    secondTurn.TrySetResult(message);
                    break;
            }
        });

        var messageId = await session.SendAsync("read the recorded file");

        Assert.Equal("msg-0001", messageId);

        var followUp = await secondTurn.Task.WaitAsync(EventTimeout);

        Assert.Equal("The recorded file says hello.", followUp.Data.Content);

        lock (invokedPaths)
        {
            Assert.Equal(["notes.txt"], invokedPaths);
        }

        ToolExecutionCompleteEvent completion;
        lock (toolResults)
        {
            completion = Assert.Single(toolResults);
        }

        Assert.True(completion.Data.Success);
        Assert.Equal("call-0001", completion.Data.ToolCallId);

        // The runtime records the handler's own return value; it never synthesises one.
        Assert.Equal("hello world\n", completion.Data.Result?.Content);
    }

    [Fact]
    public async Task MissingToolCall_ExitsNonZeroWithADiagnosticInsteadOfHanging()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var startInfo = new ProcessStartInfo(ReplayRuntimePath())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--fixture");
        startInfo.ArgumentList.Add(FixturePath("missing-tool-call.json"));
        startInfo.ArgumentList.Add("--tool-timeout");
        startInfo.ArgumentList.Add("1");

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();

        // No tool is registered on this connection, so the client never answers the recorded
        // tool call. The runtime must give up on its own budget rather than wait forever.
        await WriteRequestAsync(process.StandardInput, 1, "connect", new JsonObject());
        await WriteRequestAsync(process.StandardInput, 2, "session.create", new JsonObject
        {
            ["sessionId"] = "session-missing-tool-call",
        });
        await WriteRequestAsync(process.StandardInput, 3, "session.send", new JsonObject
        {
            ["sessionId"] = "session-missing-tool-call",
            ["prompt"] = "read the recorded file",
        });

        await process.WaitForExitAsync().WaitAsync(EventTimeout);

        var diagnostic = await stderr;
        _ = await stdout;

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("session-missing-tool-call", diagnostic);
        Assert.Contains("turn 0", diagnostic);
        Assert.Contains("call-0001", diagnostic);
        Assert.Contains("timed out", diagnostic);
    }

    private static async Task WriteRequestAsync(StreamWriter stdin, int id, string method, JsonObject parameters)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        var json = request.ToJsonString();
        await stdin.WriteAsync($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");
        await stdin.FlushAsync();
    }

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
