using System.Text;
using System.Text.Json.Nodes;

namespace ReplayRuntime.Tests;

/// <summary>
/// Covers the tool-dispatch state machine: a recorded turn that carries tool requests parks
/// the session until the client posts the result it actually produced, and a result that never
/// arrives fails the process instead of hanging.
/// </summary>
public sealed class ToolDispatchTests
{
    private const string ToolFixtureJson = """
        {
          "protocolVersion": 3,
          "runtimeVersion": "0.0.0-replay",
          "models": [ { "id": "m1", "name": "Model One", "vendor": "replay" } ],
          "sessions": [
            {
              "id": "s1",
              "model": "m1",
              "turns": [
                {
                  "messageId": "msg-1",
                  "content": "calling the tool",
                  "toolRequests": [
                    { "name": "read_file", "toolCallId": "call-1", "arguments": { "path": "notes.txt" } }
                  ]
                },
                { "messageId": "msg-2", "content": "tool said hello" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task RunAsync_TurnWithoutToolRequestsStaysReady()
    {
        const string plainFixtureJson = """
            {
              "protocolVersion": 3,
              "sessions": [
                {
                  "id": "s1",
                  "turns": [
                    { "messageId": "msg-1", "content": "first" },
                    { "messageId": "msg-2", "content": "second" }
                  ]
                }
              ]
            }
            """;

        var transcript = await RunAsync(
            plainFixtureJson,
            Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "session.tools.listPendingRequests", new JsonObject { ["sessionId"] = "s1" }),
            Request(4, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(5, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Equal(string.Empty, transcript.Stderr);
        Assert.Empty(transcript.Responses[2]["result"]!["items"]!.AsArray());
        Assert.Equal("msg-2", transcript.Responses[3]["result"]!["messageIds"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_ToolRoundTripEmitsRequestThenResumesOnTheClientResult()
    {
        var transcript = await RunAsync(
            ToolFixtureJson,
            Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "session.tools.listPendingRequests", new JsonObject { ["sessionId"] = "s1" }),
            Request(4, "session.tools.handlePendingToolCall", new JsonObject
            {
                ["sessionId"] = "s1",
                ["requestId"] = "req-s1-0-0",
                ["result"] = new JsonObject
                {
                    ["resultType"] = "success",
                    ["textResultForLlm"] = "hello from the real handler",
                },
            }),
            Request(5, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Equal(string.Empty, transcript.Stderr);

        // The recorded turn is emitted, then the tool request the client must answer.
        var events = transcript.Notifications
            .Select(n => n["params"]!["event"]!)
            .ToList();
        Assert.Equal(4, events.Count);
        Assert.Equal("assistant.message", events[0]["type"]!.GetValue<string>());
        Assert.Equal("call-1", events[0]["data"]!["toolRequests"]![0]!["toolCallId"]!.GetValue<string>());
        Assert.Equal("external_tool.requested", events[1]["type"]!.GetValue<string>());
        Assert.Equal("read_file", events[1]["data"]!["toolName"]!.GetValue<string>());
        Assert.Equal("notes.txt", events[1]["data"]!["arguments"]!["path"]!.GetValue<string>());

        // The pending request is visible to the client while the session is parked.
        var pending = transcript.Responses[2]["result"]!["items"]!.AsArray();
        Assert.Single(pending);
        Assert.Equal("req-s1-0-0", pending[0]!["requestId"]!.GetValue<string>());

        // The client's own result is what the runtime records, and the next turn follows it.
        Assert.Equal("tool.execution_complete", events[2]["type"]!.GetValue<string>());
        Assert.Equal("call-1", events[2]["data"]!["toolCallId"]!.GetValue<string>());
        Assert.True(events[2]["data"]!["success"]!.GetValue<bool>());
        Assert.Equal(
            "hello from the real handler",
            events[2]["data"]!["result"]!["content"]!.GetValue<string>());
        Assert.Equal("assistant.message", events[3]["type"]!.GetValue<string>());
        Assert.Equal("msg-2", events[3]["data"]!["messageId"]!.GetValue<string>());

        Assert.True(transcript.Responses[3]["result"]!["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RunAsync_ToolErrorFromTheClientIsRecordedVerbatim()
    {
        var transcript = await RunAsync(
            ToolFixtureJson,
            Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "session.tools.handlePendingToolCall", new JsonObject
            {
                ["sessionId"] = "s1",
                ["requestId"] = "req-s1-0-0",
                ["error"] = "handler blew up",
            }),
            Request(4, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);

        var complete = transcript.Notifications
            .Select(n => n["params"]!["event"]!)
            .Single(e => e["type"]!.GetValue<string>() == "tool.execution_complete");
        Assert.False(complete["data"]!["success"]!.GetValue<bool>());
        Assert.Equal("handler blew up", complete["data"]!["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_SendWhileAwaitingToolResultExitsNonZero()
    {
        var transcript = await RunAsync(
            ToolFixtureJson,
            Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }));

        Assert.NotEqual(0, transcript.ExitCode);
        Assert.Contains("call-1", transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("handlePendingToolCall", transcript.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingToolCallExitsNonZeroAndNamesSessionTurnAndCall()
    {
        var transcript = await RunAsync(
            ToolFixtureJson,
            Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }));

        Assert.NotEqual(0, transcript.ExitCode);
        Assert.Contains("session 's1'", transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("turn 0", transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("call-1", transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("never sent session.tools.handlePendingToolCall", transcript.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingToolCallTimesOutInsteadOfHanging()
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), $"replay-fixture-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(fixturePath, ToolFixtureJson);

        using var gate = new ManualResetEventSlim(false);
        try
        {
            var input = new StringBuilder();
            foreach (var request in new[]
                     {
                         Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
                         Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
                     })
            {
                var buffer = new StringWriter();
                JsonRpcFraming.WriteFrame(buffer, request);
                input.Append(buffer);
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            // The reader blocks after the recorded requests, so only the explicit timeout can
            // end the run. A default framework timeout would make this test hang.
            var exitCode = await ReplayHost.RunAsync(
                ["--fixture", fixturePath, "--tool-timeout", "0.25"],
                new BlockingReader(input.ToString(), gate),
                stdout,
                stderr);

            Assert.Equal(1, exitCode);
            Assert.Contains("session 's1'", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("turn 0", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("call-1", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("timed out after 0.25s", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            gate.Set();
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public async Task RunAsync_UnknownRequestIdExitsNonZero()
    {
        var transcript = await RunAsync(
            ToolFixtureJson,
            Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "session.tools.handlePendingToolCall", new JsonObject
            {
                ["sessionId"] = "s1",
                ["requestId"] = "req-nope",
                ["result"] = new JsonObject { ["textResultForLlm"] = "x" },
            }));

        Assert.NotEqual(0, transcript.ExitCode);
        Assert.Contains("req-nope", transcript.Stderr, StringComparison.Ordinal);
    }

    private static string Request(int id, string method, JsonObject parameters)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        return request.ToJsonString();
    }

    private static async Task<Transcript> RunAsync(string fixtureJson, params string[] requests)
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), $"replay-fixture-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(fixturePath, fixtureJson);

        try
        {
            var input = new StringBuilder();
            foreach (var request in requests)
            {
                var buffer = new StringWriter();
                JsonRpcFraming.WriteFrame(buffer, request);
                input.Append(buffer);
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await ReplayHost.RunAsync(
                ["--fixture", fixturePath],
                new StringReader(input.ToString()),
                stdout,
                stderr);

            return new Transcript(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    /// <summary>Delivers a fixed prefix, then blocks until the gate is released.</summary>
    private sealed class BlockingReader(string prefix, ManualResetEventSlim gate) : TextReader
    {
        private readonly StringReader _inner = new(prefix);

        public override string? ReadLine()
        {
            var line = _inner.ReadLine();
            if (line is not null)
            {
                return line;
            }

            gate.Wait();
            return null;
        }

        public override int Read(char[] buffer, int index, int count)
        {
            var read = _inner.Read(buffer, index, count);
            if (read > 0)
            {
                return read;
            }

            gate.Wait();
            return 0;
        }
    }

    private sealed record Transcript(int ExitCode, string Stdout, string Stderr)
    {
        public IReadOnlyList<JsonObject> Responses => Frames()
            .Where(frame => frame["id"] is not null)
            .ToList();

        public IReadOnlyList<JsonObject> Notifications => Frames()
            .Where(frame => frame["id"] is null && frame["method"] is not null)
            .ToList();

        private List<JsonObject> Frames()
        {
            var frames = new List<JsonObject>();
            var reader = new StringReader(Stdout);
            while (JsonRpcFraming.ReadFrame(reader) is { } frame)
            {
                frames.Add(JsonNode.Parse(frame)!.AsObject());
            }

            return frames;
        }
    }
}
