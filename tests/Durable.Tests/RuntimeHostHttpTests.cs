using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Durable.Tests;

/// <summary>
/// End-to-end tests for <c>serve --http</c>. Every test starts the host as its own process and
/// drives it over a real socket, because the transport's contract is a wire contract: a status
/// code, a stream of newline-delimited JSON, and a turn that survives the server going away.
/// </summary>
/// <remarks>
/// The host binds port 0, so the operating system picks the port and two runs of the suite cannot
/// collide. The port is read from the host's own startup line rather than assumed.
/// </remarks>
public sealed class RuntimeHostHttpTests
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
    public async Task Serve_AnswersATurnOverHttp()
    {
        using var scratch = new Scratch();
        await using var host = await ServeHost.StartAsync(scratch, "basic.json");

        var (status, lines) = await host.PostAsync("""{"input":"hello"}""");

        Assert.Equal(HttpStatusCode.OK, status);

        var result = Assert.Single(lines, line => (string?)line["type"] == "result")["result"]!.AsObject();
        Assert.Equal("reviewer", (string?)result["agent"]);
        Assert.Equal("succeeded", (string?)result["outcome"]);
        Assert.NotNull((string?)result["conversationId"]);
        Assert.NotNull((string?)result["admissionId"]);
        Assert.NotNull((string?)result["checkpointId"]);
        Assert.Equal("replay-model", (string?)result["effectiveModel"]!["model"]);
    }

    [Fact]
    public async Task Serve_StreamsTheTurnsEventsBeforeItsResult()
    {
        using var scratch = new Scratch();
        await using var host = await ServeHost.StartAsync(scratch, "basic.json", idleDelaySeconds: 2);

        var clock = Stopwatch.StartNew();
        var (status, lines, firstEventAt, resultAt) = await host.PostTimedAsync("""{"input":"hello"}""");

        Assert.Equal(HttpStatusCode.OK, status);

        // The turn's events arrive while the turn is still running, not in one burst at the end.
        // The recording holds the turn open for two seconds, so a transport that buffered the
        // whole response would deliver the first event and the result at the same instant.
        Assert.True(
            resultAt - firstEventAt >= TimeSpan.FromSeconds(1),
            $"the first event arrived {resultAt - firstEventAt} before the result, which means the "
            + "response was buffered rather than streamed.");

        Assert.Equal("event", (string?)lines[0]["type"]);
        Assert.Equal("result", (string?)lines[^1]["type"]);
        Assert.Contains(lines, line => (string?)line["type"] == "event"
            && (string?)line["event"]!["type"] == "assistant.message");
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Serve_RefusesMalformedJsonWithoutStopping()
    {
        using var scratch = new Scratch();
        await using var host = await ServeHost.StartAsync(scratch, "basic.json");

        var (status, body) = await host.PostRawAsync("""{"input":""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("not a valid turn request", body, StringComparison.Ordinal);

        // The refusal is a response, not a crash: the same server still answers a good request.
        var (nextStatus, lines) = await host.PostAsync("""{"input":"hello"}""");
        Assert.Equal(HttpStatusCode.OK, nextStatus);
        Assert.Contains(lines, line => (string?)line["type"] == "result");
    }

    [Fact]
    public async Task Serve_RefusesAnUnknownAgentWithoutStopping()
    {
        using var scratch = new Scratch();
        await using var host = await ServeHost.StartAsync(scratch, "basic.json");

        var (status, body) = await host.PostRawAsync("""{"agent":"nobody","input":"hello"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("nobody", body, StringComparison.Ordinal);
        Assert.Contains("reviewer", body, StringComparison.Ordinal);

        var (nextStatus, lines) = await host.PostAsync("""{"input":"hello"}""");
        Assert.Equal(HttpStatusCode.OK, nextStatus);
        Assert.Contains(lines, line => (string?)line["type"] == "result");
    }

    [Fact]
    public async Task Serve_RefusesARequestWithNoInput()
    {
        using var scratch = new Scratch();
        await using var host = await ServeHost.StartAsync(scratch, "basic.json");

        var (status, body) = await host.PostRawAsync("""{"agent":"reviewer"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("input", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_FinishesTheTurnInFlightWhenAskedToStop()
    {
        if (OperatingSystem.IsWindows())
        {
            // The graceful path is a POSIX signal; Windows has no equivalent the host installs.
            return;
        }

        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        await using var host = await ServeHost.StartAsync(scratch, "basic.json", idleDelaySeconds: 5);

        // Start a turn, wait until it is demonstrably in flight, then ask the server to stop.
        var inFlight = host.PostAsync("""{"input":"hello"}""");
        await host.WaitForFirstEventAsync();

        await host.TerminateAsync();

        var (status, lines) = await inFlight;
        Assert.Equal(HttpStatusCode.OK, status);

        var result = Assert.Single(lines, line => (string?)line["type"] == "result")["result"]!.AsObject();
        Assert.Equal("succeeded", (string?)result["outcome"]);
        var conversationId = (string?)result["conversationId"];
        var checkpointId = (string?)result["checkpointId"];
        Assert.NotNull(conversationId);
        Assert.NotNull(checkpointId);

        // The turn reached its checkpoint, so a later run resumes the same conversation rather
        // than starting over or finding a half-written admission.
        var (exitCode, stdout, stderr) = await RunHostAsync(
            "run", "--agent", "reviewer", "--input", "after the stop",
            "--store", storePath, "--format", "jsonl");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("uncertain", stderr, StringComparison.Ordinal);

        var resumed = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal(conversationId, (string?)resumed["conversationId"]);
        Assert.NotNull((string?)resumed["checkpointId"]);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHostAsync(
        params string[] args)
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

        startInfo.Environment["DURABLE_RUNTIME_PATH"] = ReplayRuntimePath();
        startInfo.ArgumentList.Add("--runtime-arg");
        startInfo.ArgumentList.Add("--fixture");
        startInfo.ArgumentList.Add("--runtime-arg");
        startInfo.ArgumentList.Add(FixturePath("basic.json"));

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

    /// <summary>
    /// A running <c>serve --http</c> host, with the port it bound and a client pointed at it.
    /// </summary>
    private sealed class ServeHost : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stderr = new();
        private readonly Task _stderrPump;
        private readonly TaskCompletionSource _firstEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HttpClient _client;

        private ServeHost(Process process, int port)
        {
            _process = process;
            Port = port;
            _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            _stderrPump = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    lock (_stderr)
                    {
                        _stderr.AppendLine(line);
                    }
                }
            });
        }

        public int Port { get; }

        public static async Task<ServeHost> StartAsync(
            Scratch scratch,
            string fixture,
            double idleDelaySeconds = 0)
        {
            var storePath = scratch.PathIn("store.jsonl");
            var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

            var startInfo = new ProcessStartInfo
            {
                FileName = HostPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };

            foreach (var argument in new[]
            {
                "serve", "--agent", "reviewer",
                "--definition", definitionPath, "--store", storePath, "--format", "jsonl",
                "--http", "--port", "0",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment["DURABLE_RUNTIME_PATH"] = ReplayRuntimePath();
            foreach (var argument in new[] { "--fixture", FixturePath(fixture) })
            {
                startInfo.ArgumentList.Add("--runtime-arg");
                startInfo.ArgumentList.Add(argument);
            }

            if (idleDelaySeconds > 0)
            {
                startInfo.ArgumentList.Add("--runtime-arg");
                startInfo.ArgumentList.Add("--idle-delay");
                startInfo.ArgumentList.Add("--runtime-arg");
                startInfo.ArgumentList.Add(idleDelaySeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("the runtime host did not start.");

            process.StandardInput.Close();

            // The host reports the port it bound, so the test never guesses one.
            var port = await ReadPortAsync(process);
            return new ServeHost(process, port);
        }

        private static async Task<int> ReadPortAsync(Process process)
        {
            var deadline = DateTime.UtcNow + HostTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var line = await process.StandardError.ReadLineAsync().WaitAsync(HostTimeout);
                if (line is null)
                {
                    throw new InvalidOperationException(
                        "the runtime host exited before it reported a port.");
                }

                const string marker = "listening on http://127.0.0.1:";
                if (line.StartsWith(marker, StringComparison.Ordinal))
                {
                    return int.Parse(line[marker.Length..], System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            throw new TimeoutException("the runtime host did not report a port.");
        }

        public async Task<(HttpStatusCode Status, List<JsonObject> Lines)> PostAsync(string body)
        {
            var (status, lines, _, _) = await PostTimedAsync(body);
            return (status, lines);
        }

        /// <summary>
        /// Posts a turn and reads the response line by line, recording when the first event and
        /// the result were actually observed on the socket.
        /// </summary>
        public async Task<(HttpStatusCode Status, List<JsonObject> Lines, TimeSpan FirstEventAt, TimeSpan ResultAt)>
            PostTimedAsync(string body)
        {
            var clock = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead);

            var lines = new List<JsonObject>();
            var firstEventAt = TimeSpan.Zero;
            var resultAt = TimeSpan.Zero;

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var parsed = JsonNode.Parse(line)!.AsObject();
                lines.Add(parsed);

                if ((string?)parsed["type"] == "event")
                {
                    if (firstEventAt == TimeSpan.Zero)
                    {
                        firstEventAt = clock.Elapsed;
                        _firstEvent.TrySetResult();
                    }
                }
                else if ((string?)parsed["type"] == "result")
                {
                    resultAt = clock.Elapsed;
                }
            }

            return (response.StatusCode, lines, firstEventAt, resultAt);
        }

        /// <summary>Posts a body and returns the refusal as text, for a request expected to fail.</summary>
        public async Task<(HttpStatusCode Status, string Body)> PostRawAsync(string body)
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync("/", content);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        /// <summary>Waits until a turn has produced its first event, so a stop lands mid-turn.</summary>
        public Task WaitForFirstEventAsync() => _firstEvent.Task.WaitAsync(HostTimeout);

        /// <summary>Asks the host to stop the way a service manager would.</summary>
        public async Task TerminateAsync()
        {
            if (!_process.HasExited)
            {
                using var kill = Process.Start(new ProcessStartInfo("kill", $"-TERM {_process.Id}")
                {
                    UseShellExecute = false,
                });
                if (kill is not null)
                {
                    await kill.WaitForExitAsync().WaitAsync(HostTimeout);
                }
            }

            await _process.WaitForExitAsync().WaitAsync(HostTimeout);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(HostTimeout);
            }

            await _stderrPump;
            _client.Dispose();
            _process.Dispose();
        }
    }

    /// <summary>A scratch directory that is removed when the test finishes.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"durable-http-{Guid.NewGuid():N}");

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
