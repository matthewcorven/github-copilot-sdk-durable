using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Durable.Tests;

/// <summary>
/// End-to-end tests for <c>serve --rpc</c>. Every test starts the host as its own process and
/// drives it through its standard input and output, because the transport's contract is a wire
/// contract: one JSON request per line in, the turn's lines out, and a host that keeps reading
/// after a line it could not understand.
/// </summary>
/// <remarks>
/// The runtime the host talks to is the recorded runtime, selected through
/// <c>DURABLE_RUNTIME_PATH</c>. Nothing here stands in for the host or the runtime.
/// </remarks>
public sealed class RuntimeHostRpcTests
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
    public async Task Serve_SpeaksJsonLinesOverStandardInputAndOutput()
    {
        using var scratch = new Scratch();
        await using var host = await RpcHost.StartAsync(scratch, "basic.json");

        await host.SendAsync("""{"id":"one","input":"hello"}""");
        var lines = await host.ReadUntilAsync(line => (string?)line["type"] == "result");

        // The first line is the state the turn starts from, exactly as the example prints it.
        Assert.Equal("snapshot", (string?)lines[0]["type"]);
        Assert.Equal("one", (string?)lines[0]["requestId"]);
        Assert.True((int?)lines[0]["events"] >= 0);

        // Then the turn's events, then its result.
        Assert.Contains(lines, line => (string?)line["type"] == "event");
        Assert.Equal("result", (string?)lines[^1]["type"]);

        var result = lines[^1]["result"]!.AsObject();
        Assert.Equal("reviewer", (string?)result["agent"]);
        Assert.Equal("succeeded", (string?)result["outcome"]);
        Assert.NotNull((string?)result["conversationId"]);
        Assert.NotNull((string?)result["admissionId"]);
        Assert.NotNull((string?)result["checkpointId"]);
        Assert.Equal("replay-model", (string?)result["effectiveModel"]!["model"]);
    }

    [Fact]
    public async Task Serve_GivesEachPipelinedRequestItsOwnResponseInOrder()
    {
        using var scratch = new Scratch();
        await using var host = await RpcHost.StartAsync(scratch, "basic.json");

        // Both requests are written before either answer is read, which is the pipelining the
        // correlation exists for.
        await host.SendAsync("""{"id":"first","input":"hello"}""");
        await host.SendAsync("""{"id":"second","input":"again"}""");

        var lines = await host.ReadUntilAsync(line =>
            (string?)line["type"] == "result" && (string?)line["requestId"] == "second");

        var first = lines.Where(line => (string?)line["requestId"] == "first").ToList();
        var second = lines.Where(line => (string?)line["requestId"] == "second").ToList();

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);

        // Every line names the request it belongs to, and the two answers do not interleave: the
        // first request's result is the last line before the second request's snapshot.
        Assert.All(lines, line => Assert.NotNull((string?)line["requestId"]));
        Assert.Equal("result", (string?)first[^1]["type"]);
        Assert.Equal("snapshot", (string?)second[0]["type"]);
        Assert.True(
            lines.IndexOf(first[^1]) < lines.IndexOf(second[0]),
            "the second request's lines began before the first request's result.");

        // Each answer is its own turn, so each carries its own admission.
        var firstResult = first[^1]["result"]!.AsObject();
        var secondResult = second[^1]["result"]!.AsObject();
        Assert.Equal("succeeded", (string?)firstResult["outcome"]);
        Assert.Equal("succeeded", (string?)secondResult["outcome"]);
        Assert.NotEqual((string?)firstResult["admissionId"], (string?)secondResult["admissionId"]);
    }

    [Fact]
    public async Task Serve_NamesTheLineAndTheProblemAndKeepsServing()
    {
        using var scratch = new Scratch();
        await using var host = await RpcHost.StartAsync(scratch, "basic.json");

        // Line 1 is not JSON at all, line 2 is JSON but not a request.
        await host.SendAsync("this is not json");
        await host.SendAsync("""{"id":"two"}""");

        var errors = await host.ReadUntilAsync(line => (string?)line["type"] == "error" && (int?)line["line"] == 2);

        var first = errors[0];
        Assert.Equal("error", (string?)first["type"]);
        Assert.Equal(1, (int?)first["line"]);
        Assert.Equal("this is not json", (string?)first["lineText"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)first["error"]));

        var second = errors[1];
        Assert.Equal(2, (int?)second["line"]);
        Assert.Equal("two", (string?)second["requestId"]);
        Assert.Contains("input", (string?)second["error"], StringComparison.Ordinal);

        // The refusals are responses, not a crash: the same host still runs a turn.
        await host.SendAsync("""{"id":"three","input":"hello"}""");
        var lines = await host.ReadUntilAsync(line =>
            (string?)line["type"] == "result" && (string?)line["requestId"] == "three");

        Assert.Equal("succeeded", (string?)lines[^1]["result"]!["outcome"]);
    }

    [Fact]
    public async Task Serve_RefusesAnUnknownAgentWithoutStopping()
    {
        using var scratch = new Scratch();
        await using var host = await RpcHost.StartAsync(scratch, "basic.json");

        await host.SendAsync("""{"id":"one","agent":"nobody","input":"hello"}""");
        var errors = await host.ReadUntilAsync(line => (string?)line["type"] == "error");

        Assert.Equal("one", (string?)errors[0]["requestId"]);
        Assert.Contains("nobody", (string?)errors[0]["error"], StringComparison.Ordinal);
        Assert.Contains("reviewer", (string?)errors[0]["error"], StringComparison.Ordinal);

        await host.SendAsync("""{"id":"two","input":"hello"}""");
        var lines = await host.ReadUntilAsync(line =>
            (string?)line["type"] == "result" && (string?)line["requestId"] == "two");

        Assert.Equal("succeeded", (string?)lines[^1]["result"]!["outcome"]);
    }

    [Fact]
    public async Task Serve_AgreesWithTheJsonExample()
    {
        using var scratch = new Scratch();
        await using var host = await RpcHost.StartAsync(scratch, "basic.json");

        await host.SendAsync("""{"id":"one","input":"hello"}""");
        var lines = await host.ReadUntilAsync(line => (string?)line["type"] == "result");

        // The example is the protocol's definition, so the test reads it rather than restating
        // it: every property the example prints on a snapshot or event line must be on the
        // host's line too, under the same name.
        var example = await File.ReadAllTextAsync(
            Existing(Path.Combine(AppContext.BaseDirectory, "examples", "18-json.cs")));

        var snapshot = lines[0];
        foreach (var property in ExampleProperties(example, "snapshot"))
        {
            Assert.True(
                snapshot.ContainsKey(property),
                $"the snapshot line has no '{property}' property, which examples/18-json.cs prints.");
        }

        var events = lines.Where(line => (string?)line["type"] == "event").ToList();
        Assert.NotEmpty(events);
        foreach (var property in ExampleProperties(example, "event"))
        {
            Assert.All(events, line => Assert.True(
                line.ContainsKey(property),
                $"an event line has no '{property}' property, which examples/18-json.cs prints."));
        }

        // The example's own assertions, applied to the host's stream.
        Assert.All(lines, line => Assert.StartsWith("{", line.ToJsonString().TrimStart(), StringComparison.Ordinal));
        Assert.Contains(lines, line => (string?)line["type"] == "snapshot");
        Assert.Contains(lines, line => (string?)line["type"] == "event");
    }

    /// <summary>
    /// The property names the example prints on a line of the given type, read out of the
    /// example's own source.
    /// </summary>
    /// <remarks>
    /// The example builds its lines with anonymous objects, so the names are the identifiers
    /// before each <c>=</c> in the object literal that follows <c>type = "&lt;kind&gt;"</c>. Reading
    /// them from the file is what makes this an agreement test rather than a second copy of the
    /// shape that could drift from the first.
    /// </remarks>
    private static IReadOnlyList<string> ExampleProperties(string example, string kind)
    {
        var marker = $"type = \"{kind}\"";
        var start = example.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"examples/18-json.cs no longer prints a '{kind}' line.");

        var open = example.IndexOf('{', start);
        Assert.True(open >= 0, $"the '{kind}' line in examples/18-json.cs is not an object literal.");

        var properties = new List<string>();
        var depth = 0;
        for (var index = open; index < example.Length; index++)
        {
            switch (example[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return properties;
                    }

                    break;
                case '=' when depth == 1:
                    // Walk back over the identifier that names the property.
                    var end = index;
                    var begin = end;
                    while (begin > open && (char.IsLetterOrDigit(example[begin - 1]) || example[begin - 1] == '@'))
                    {
                        begin--;
                    }

                    var name = example[begin..end].TrimStart('@');
                    if (name.Length > 0)
                    {
                        properties.Add(name);
                    }

                    break;
            }
        }

        Assert.Fail($"the '{kind}' object literal in examples/18-json.cs is not closed.");
        return properties;
    }

    private static string Existing(string path) =>
        File.Exists(path) ? path : throw new FileNotFoundException($"the test asset is missing: '{path}'.", path);

    private static string HostPath() => Existing(
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Durable.Runtime.exe" : "Durable.Runtime"));

    private static string ReplayRuntimePath() => Existing(
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ReplayRuntime.exe" : "ReplayRuntime"));

    private static string FixturePath(string name) =>
        Existing(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    /// <summary>
    /// A running <c>serve --rpc</c> host, with its standard input and output as the wire.
    /// </summary>
    private sealed class RpcHost : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly List<JsonObject> _lines = [];
        private readonly SemaphoreSlim _linesAvailable = new(0);
        private readonly Task _stdoutPump;
        private readonly Task _stderrPump;

        private RpcHost(Process process)
        {
            _process = process;
            _stdoutPump = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    lock (_lines)
                    {
                        _lines.Add(JsonNode.Parse(line)!.AsObject());
                    }

                    _linesAvailable.Release();
                }
            });
            _stderrPump = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is not null)
                {
                }
            });
        }

        public static async Task<RpcHost> StartAsync(Scratch scratch, string fixture)
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
                "--rpc",
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

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("the runtime host did not start.");

            return new RpcHost(process);
        }

        /// <summary>Writes one request line, which is one request.</summary>
        public async Task SendAsync(string line)
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync();
        }

        /// <summary>
        /// Reads lines until one satisfies <paramref name="done"/>, and returns everything read.
        /// </summary>
        public async Task<List<JsonObject>> ReadUntilAsync(Func<JsonObject, bool> done)
        {
            var deadline = DateTime.UtcNow + HostTimeout;
            var read = 0;
            while (true)
            {
                while (true)
                {
                    JsonObject? line;
                    lock (_lines)
                    {
                        line = read < _lines.Count ? _lines[read] : null;
                    }

                    if (line is null)
                    {
                        break;
                    }

                    read++;
                    if (done(line))
                    {
                        lock (_lines)
                        {
                            return _lines.Take(read).ToList();
                        }
                    }
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException(
                        $"the host produced no matching line within {HostTimeout.TotalSeconds:0}s. "
                        + $"lines so far: {string.Join(" | ", Snapshot())}");
                }

                await _linesAvailable.WaitAsync(remaining);
            }
        }

        private List<string> Snapshot()
        {
            lock (_lines)
            {
                return _lines.Select(line => line.ToJsonString()).ToList();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The host may already have left.
            }

            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await _process.WaitForExitAsync().WaitAsync(HostTimeout);
            await _stdoutPump;
            await _stderrPump;
            _linesAvailable.Dispose();
            _process.Dispose();
        }
    }

    /// <summary>A scratch directory that is removed when the test finishes.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"durable-rpc-{Guid.NewGuid():N}");

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
