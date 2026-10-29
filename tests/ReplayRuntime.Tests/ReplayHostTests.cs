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
    public async Task RunAsync_UnknownSessionIsServedFromTheRecordingAndAnnouncedAsSynthesised()
    {
        var transcript = await RunAsync(
            Request(1, "session.sendMessages", new JsonObject { ["sessionId"] = "nope" }),
            Request(2, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Contains(ReplayHost.SynthesisedSessionMarker, transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("'nope'", transcript.Stderr, StringComparison.Ordinal);
        Assert.Equal("msg-1", transcript.Responses[0]["result"]!["messageIds"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_CreateWithUnrecordedIdAnnouncesSynthesisedSession()
    {
        var transcript = await RunAsync(
            Request(1, "session.create", new JsonObject { ["sessionId"] = "client-chosen" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "client-chosen" }),
            Request(3, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Equal("client-chosen", transcript.Responses[0]["result"]!["sessionId"]!.GetValue<string>());
        Assert.Contains(ReplayHost.SynthesisedSessionMarker, transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("'client-chosen'", transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("'s1'", transcript.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CreateWithRecordedIdAnnouncesNothing()
    {
        var transcript = await RunAsync(
            Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Equal(string.Empty, transcript.Stderr);
    }

    [Fact]
    public async Task RunAsync_ResumeOfUnrecordedSessionAnnouncesSynthesisedSession()
    {
        var transcript = await RunAsync(
            Request(1, "session.resume", new JsonObject { ["sessionId"] = "gone" }),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "gone" }),
            Request(3, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Contains(ReplayHost.SynthesisedSessionMarker, transcript.Stderr, StringComparison.Ordinal);
        Assert.Contains("'gone'", transcript.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesSynthesisedMarkerToTheMarkerFileWhenOneIsNamed()
    {
        var markerPath = Path.Combine(Path.GetTempPath(), $"replay-markers-{Guid.NewGuid():N}.txt");
        var previous = Environment.GetEnvironmentVariable(ReplayHost.MarkerPathEnvironmentVariable);
        Environment.SetEnvironmentVariable(ReplayHost.MarkerPathEnvironmentVariable, markerPath);

        try
        {
            var transcript = await RunAsync(
                Request(1, "session.sendMessages", new JsonObject { ["sessionId"] = "nope" }),
                Request(2, "runtime.shutdown", new JsonObject()));

            Assert.Equal(0, transcript.ExitCode);
            var written = await File.ReadAllTextAsync(markerPath);
            Assert.Contains(ReplayHost.SynthesisedSessionMarker, written, StringComparison.Ordinal);
            Assert.Contains("'nope'", written, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReplayHost.MarkerPathEnvironmentVariable, previous);
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task RunAsync_WritesNoMarkerFileWhenTheRunIsFullyRecorded()
    {
        var markerPath = Path.Combine(Path.GetTempPath(), $"replay-markers-{Guid.NewGuid():N}.txt");
        var previous = Environment.GetEnvironmentVariable(ReplayHost.MarkerPathEnvironmentVariable);
        Environment.SetEnvironmentVariable(ReplayHost.MarkerPathEnvironmentVariable, markerPath);

        try
        {
            var transcript = await RunAsync(
                Request(1, "session.create", new JsonObject { ["sessionId"] = "s1" }),
                Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
                Request(3, "runtime.shutdown", new JsonObject()));

            Assert.Equal(0, transcript.ExitCode);
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReplayHost.MarkerPathEnvironmentVariable, previous);
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task RunAsync_GetMessagesReplaysEmittedEventsInOrder()
    {
        var transcript = await RunAsync(
            Request(1, "session.create", new JsonObject()),
            Request(2, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "session.getMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(4, "session.sendMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(5, "session.getMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(6, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Equal(string.Empty, transcript.Stderr);

        var responses = transcript.Responses;
        Assert.Equal(6, responses.Count);

        var first = responses[2]["result"]!["events"]!.AsArray();
        Assert.Single(first);
        Assert.Equal("msg-1", first[0]!["data"]!["messageId"]!.GetValue<string>());

        var second = responses[4]["result"]!["events"]!.AsArray();
        Assert.Equal(2, second.Count);
        Assert.Equal("msg-1", second[0]!["data"]!["messageId"]!.GetValue<string>());
        Assert.Equal("msg-2", second[1]!["data"]!["messageId"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_GetMessagesBeforeAnySendReturnsEmptyArray()
    {
        var transcript = await RunAsync(
            Request(1, "session.create", new JsonObject()),
            Request(2, "session.getMessages", new JsonObject { ["sessionId"] = "s1" }),
            Request(3, "runtime.shutdown", new JsonObject()));

        Assert.Equal(0, transcript.ExitCode);
        Assert.Empty(transcript.Responses[1]["result"]!["events"]!.AsArray());
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
