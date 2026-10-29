using System.Text;
using System.Text.Json.Nodes;

namespace ReplayRuntime.Tests;

public sealed class ReplayHostTests
{
    private const string FixtureJson = """
        {
          "protocolVersion": 3,
          "runtimeVersion": "0.0.0-replay",
          "models": [ { "id": "m1", "name": "Model One", "vendor": "replay" } ],
          "sessions": [
            {
              "id": "s1",
              "model": "m1",
              "turns": [
                { "messageId": "msg-1", "content": "first" },
                { "messageId": "msg-2", "content": "second" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task RunAsync_DrivesStartCreateSendStopToCompletion()
    {
        var transcript = await RunAsync(
            Request(1, "connect", new JsonObject { ["clientInfo"] = new JsonObject { ["editorName"] = "replay" } }),
            Request(2, "models.list", new JsonObject()),
            Request(3, "session.create", new JsonObject()),
            Request(4, "session.sendMessages", new JsonObject
            {
                ["sessionId"] = "s1",
                ["messages"] = new JsonArray(new JsonObject { ["prompt"] = "hi" }),
            }),
            Request(5, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Equal(string.Empty, transcript.Stderr);

        var responses = transcript.Responses;
        Assert.Equal(5, responses.Count);
        Assert.True(responses[0]["result"]!["ok"]!.GetValue<bool>());
        Assert.Equal(3, responses[0]["result"]!["protocolVersion"]!.GetValue<int>());
        Assert.Equal("m1", responses[1]["result"]!["list"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("s1", responses[2]["result"]!["sessionId"]!.GetValue<string>());
        Assert.Equal("msg-1", responses[3]["result"]!["messageIds"]![0]!.GetValue<string>());
        Assert.True(responses[4]["result"]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RunAsync_EmitsAssistantMessageNotificationBeforeSendResult()
    {
        var transcript = await RunAsync(
            Request(1, "session.create", new JsonObject()),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "runtime.shutdown", new JsonObject()));

        var notification = Assert.Single(transcript.Notifications);
        Assert.Equal("session.event", notification["method"]!.GetValue<string>());
        var envelope = notification["params"]!;
        Assert.Equal("s1", envelope["sessionId"]!.GetValue<string>());
        Assert.Equal("assistant.message", envelope["event"]!["type"]!.GetValue<string>());
        Assert.Equal("first", envelope["event"]!["data"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_ConsumesRecordedTurnsInOrder()
    {
        var transcript = await RunAsync(
            Request(1, "session.create", new JsonObject()),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(4, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Equal("msg-1", transcript.Responses[1]["result"]!["messageIds"]![0]!.GetValue<string>());
        Assert.Equal("msg-2", transcript.Responses[2]["result"]!["messageIds"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_IsByteIdenticalAcrossRuns()
    {
        var requests = new[]
        {
            Request(1, "connect", new JsonObject()),
            Request(2, "session.create", new JsonObject()),
            Request(3, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(4, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(5, "runtime.shutdown", new JsonObject()),
        };

        var first = await RunAsync(requests);
        var second = await RunAsync(requests);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.Stdout, second.Stdout, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UnrecordedRequestExitsNonZeroAndNamesTheMethod()
    {
        var transcript = await RunAsync(
            Request(1, "connect", new JsonObject()),
            Request(2, "session.teleport", new JsonObject()));

        Assert.NotEqual(0, transcript.ExitCode);
        Assert.Contains("session.teleport", transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("unrecorded request", transcript.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExhaustedFixtureExitsNonZero()
    {
        var transcript = await RunAsync(
            Request(1, "session.create", new JsonObject()),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(4, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }));

        Assert.NotEqual(0, transcript.ExitCode);
        Assert.Contains("exhausted", transcript.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UnknownSessionExitsNonZero()
    {
        var transcript = await RunAsync(
            Request(1, "session.sendMessages", new JsonObject { ["sessionId"] = "nope" }));

        Assert.NotEqual(0, transcript.ExitCode);
        Assert.Contains("nope", transcript.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingFixtureExitsWithUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await ReplayHost.RunAsync(
            ["--fixture", Path.Combine(Path.GetTempPath(), "does-not-exist-replay.json")],
            new StringReader(string.Empty),
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("failed to load fixture", stderr.ToString(), StringComparison.Ordinal);
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

    private static async Task<Transcript> RunAsync(params string[] requests)
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), $"replay-fixture-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(fixturePath, FixtureJson);

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
