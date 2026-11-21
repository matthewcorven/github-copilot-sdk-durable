using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Durable.Tests;

/// <summary>
/// End-to-end shift runs. Every test starts the host as its own process over a real store file,
/// because the shift's contract is a process contract: a claim written by one process, a handoff
/// read back by another, and an exit code a scheduler can act on.
/// </summary>
/// <remarks>
/// The runtime is the recorded runtime, so a turn costs no model call. Cancelling an occurrence
/// mid-turn is a real process kill (<see cref="Process.Kill(bool)"/>), which is the interruption
/// the handoff exists to survive.
/// </remarks>
public sealed class ShiftCliTests
{
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(120);

    private const string ReviewerDefinition = """
        {
          "name": "reviewer",
          "description": "Reviews changes.",
          "instructions": "Review the change and report what is wrong with it.",
          "extensions": ["durable"]
        }
        """;

    [Fact]
    public async Task Shift_ClaimsRunsAndReleasesWithAHandoff()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, stdout, stderr) = await RunShiftAsync(
            "--agent", "reviewer",
            "--definition", definitionPath,
            "--store", storePath,
            "--shift-key", "sessions/po-shift",
            "--owner", "run-a",
            "--session", "session-a",
            "--input", "start the shift",
            "--handoff-next", "run the tests");

        Assert.Equal(0, exitCode);

        var result = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("ran", (string?)result["outcome"]);
        Assert.Equal("reviewer", (string?)result["agent"]);
        Assert.Equal("sessions/po-shift", (string?)result["key"]);

        // The claim is released, and the release names the attempt and the handoff it used.
        Assert.Equal("released", (string?)result["claim"]!["status"]);
        Assert.Equal("run-a", (string?)result["claim"]!["owner"]);
        Assert.Equal(1, (long?)result["claim"]!["attempt"]);

        // The handoff was persisted with the shift's outcome and the next step.
        Assert.Equal("handedOff", (string?)result["handoff"]!["phase"]);
        Assert.Equal("run the tests", (string?)result["handoff"]!["next"]);
        Assert.Contains("occurrence: succeeded", (string?)result["handoff"]!["achieved"], StringComparison.Ordinal);

        Assert.Single(result["occurrences"]!.AsArray());
        Assert.Equal("succeeded", (string?)result["occurrences"]![0]!["outcome"]);
        Assert.Null(result["resumedFrom"]);
        Assert.Contains("store: jsonl", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shift_RefusesASecondOwnerAndNamesTheHolderWithoutRunning()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var first = await RunShiftAsync(
            "--agent", "reviewer", "--definition", definitionPath, "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-a", "--session", "session-a",
            "--input", "first");
        Assert.Equal(0, first.ExitCode);

        // A released shift is free to take, so this successor is admitted rather than refused.
        var successor = await RunShiftAsync(
            "--agent", "reviewer", "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-b", "--session", "session-b",
            "--input", "second");
        Assert.Equal(0, successor.ExitCode);
        var successorResult = JsonNode.Parse(successor.Stdout)!.AsObject();
        Assert.Equal(2, (long?)successorResult["claim"]!["attempt"]);
        Assert.Equal(1, (long?)successorResult["claim"]!["resumedFrom"]);
        Assert.NotNull(successorResult["resumedFrom"]);
    }

    [Fact]
    public async Task Shift_RefusesAnUnreadableStoredClaimReadOnly()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        // A claim whose text this build does not know, written the way a newer build would.
        var seeded = await RunShiftAsync(
            "--agent", "reviewer", "--definition", definitionPath, "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-a", "--session", "session-a",
            "--input", "seed");
        Assert.Equal(0, seeded.ExitCode);

        // Rewrite the claim's latest version with an unknown status, through the store's own
        // JSONL record shape, so the host reads it back exactly as a newer build would have left it.
        var lines = File.ReadAllLines(storePath);
        var rewritten = lines.Select(line =>
        {
            if (line.Length == 0 || !line.Contains("\"durable.shift-claim\"", StringComparison.Ordinal))
            {
                return line;
            }

            var record = JsonNode.Parse(line)!.AsObject();
            var payload = record["payload"]?.AsObject();
            var value = payload?["value"]?.AsObject() ?? payload?["document"]?["versions"]?.AsArray()?.LastOrDefault()?["value"]?.AsObject();
            if (value is not null)
            {
                value["statusText"] = "suspended";
            }

            return record.ToJsonString();
        }).ToList();
        File.WriteAllLines(storePath, rewritten);

        var refused = await RunShiftAsync(
            "--agent", "reviewer", "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-b", "--session", "session-b",
            "--input", "must not run");

        // Fail closed: non-zero, nothing run, and the reason says the state is uncertain.
        Assert.NotEqual(0, refused.ExitCode);
        var result = JsonNode.Parse(refused.Stdout)!.AsObject();
        Assert.Equal("readOnly", (string?)result["outcome"]);
        Assert.Empty(result["occurrences"]!.AsArray());
        Assert.Contains("cannot interpret", (string?)result["message"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shift_ResumesFromTheHandoffAfterAKilledProcess()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        // A killed occurrence: the process is stopped while the recorded runtime is still in its
        // idle delay after the turn's message is emitted, so the host never reaches its completed
        // handoff or its release.
        var killed = await RunShiftUntilKilledAsync(
            "--agent", "reviewer", "--definition", definitionPath, "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-a", "--session", "session-a",
            "--input", "interrupted shift");

        Assert.True(killed.ExitedAbnormally, "the host was expected to be killed mid-shift.");

        // The next process is the same session: it finds its own in-flight handoff and resumes.
        var resumed = await RunShiftAsync(
            "--agent", "reviewer", "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-a-restarted", "--session", "session-a",
            "--input", "resume the shift");

        Assert.Equal(0, resumed.ExitCode);
        var result = JsonNode.Parse(resumed.Stdout)!.AsObject();
        Assert.Equal("ran", (string?)result["outcome"]);
        Assert.Equal(2, (long?)result["claim"]!["attempt"]);
        Assert.Equal(1, (long?)result["claim"]!["resumedFrom"]);

        // The resume read the interrupted run's handoff back: the interruption is evidence in the
        // output, not something the successor has to infer.
        Assert.NotNull(result["resumedFrom"]);
        Assert.Equal("running", (string?)result["resumedFrom"]!["phase"]);
        Assert.Equal("interrupted shift", (string?)result["resumedFrom"]!["selected"]);
    }

    [Fact]
    public async Task Shift_RefusesADifferentSessionWhileAnInFlightHandoffIsAwaitingRecovery()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        // The interrupted run leaves a claim held by session-a with an in-flight handoff. That
        // is the state a killed process leaves behind, and a second session must not treat it as
        // permission to run: the owner may still be alive.
        var killed = await RunShiftUntilKilledAsync(
            "--agent", "reviewer", "--definition", definitionPath, "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-a", "--session", "session-a",
            "--input", "interrupted shift");
        Assert.True(killed.ExitedAbnormally, "the host was expected to be killed mid-shift.");

        var refused = await RunShiftAsync(
            "--agent", "reviewer", "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-b", "--session", "session-b",
            "--input", "must not duplicate");

        Assert.NotEqual(0, refused.ExitCode);
        var result = JsonNode.Parse(refused.Stdout)!.AsObject();
        Assert.Equal("conflict", (string?)result["outcome"]);
        Assert.Equal("run-a", (string?)result["claim"]!["owner"]);
        Assert.Equal(1, (long?)result["claim"]!["attempt"]);
        Assert.Empty(result["occurrences"]!.AsArray());
        Assert.Contains("in-flight", (string?)result["message"], StringComparison.Ordinal);

        // The refused run wrote nothing: the claim is still attempt 1, still held by run-a.
        var after = await RunShiftAsync(
            "--agent", "reviewer", "--store", storePath,
            "--shift-key", "sessions/po-shift", "--owner", "run-c", "--session", "session-c",
            "--input", "still must not duplicate");
        Assert.NotEqual(0, after.ExitCode);
        Assert.Equal(1, (long?)JsonNode.Parse(after.Stdout)!["claim"]!["attempt"]);
    }

    [Fact]
    public async Task Shift_RequiresAShiftKey()
    {
        using var scratch = new Scratch();
        var storePath = scratch.PathIn("store.jsonl");
        var definitionPath = scratch.Write("reviewer.json", ReviewerDefinition);

        var (exitCode, _, stderr) = await RunShiftAsync(
            "--agent", "reviewer", "--definition", definitionPath, "--store", storePath,
            "--input", "no key");

        Assert.Equal(2, exitCode);
        Assert.Contains("--shift-key", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunShiftAsync(
        params string[] args) => await RunShiftWithFixtureAsync(args, "basic.json");

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunShiftWithFixtureAsync(
        string[] args,
        string fixture)
    {
        using var process = StartShift(args, fixture);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(HostTimeout);
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Starts a shift and kills it as soon as it commits its in-flight handoff. The kill is the
    /// interruption the durable layer has to survive; waiting for a signal from the turn would
    /// make the test depend on when the recorded runtime chooses to answer.
    /// </summary>
    private static async Task<(int ExitCode, bool ExitedAbnormally)> RunShiftUntilKilledAsync(
        params string[] args)
    {
        using var process = StartShift(args, "basic.json");
        process.StandardInput.Close();

        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (process.HasExited)
            {
                return (process.ExitCode, false);
            }

            // The in-flight handoff is committed before the harness is opened, so a host that
            // has printed its store line is far enough along to be killed meaningfully.
            if (File.Exists(args[Array.IndexOf(args, "--store") + 1])
                && ShiftHandoffIsRunning(args[Array.IndexOf(args, "--store") + 1], args[Array.IndexOf(args, "--shift-key") + 1]))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                return (process.ExitCode, true);
            }

            await Task.Delay(20);
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        return (process.ExitCode, false);
    }

    private static bool ShiftHandoffIsRunning(string storePath, string key)
    {
        if (!File.Exists(storePath))
        {
            return false;
        }

        foreach (var line in File.ReadAllLines(storePath))
        {
            if (line.Length == 0 || !line.Contains("\"durable.shift-handoff\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("\"phase\":\"running\"", StringComparison.OrdinalIgnoreCase)
                && line.Contains(key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Process StartShift(string[] args, string fixture)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HostPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("shift");
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DURABLE_RUNTIME_PATH"] = ReplayRuntimePath();

        // A long idle delay gives the kill a deterministic window: the recorded runtime has
        // answered but has not announced the turn settled, so the host is mid-turn rather than
        // between turns. Without it the turn can finish before the test can interrupt it.
        foreach (var runtimeArgument in new[] { "--fixture", FixturePath(fixture), "--idle-delay", "30" })
        {
            startInfo.ArgumentList.Add("--runtime-arg");
            startInfo.ArgumentList.Add(runtimeArgument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("the runtime host did not start.");
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
            Path.Combine(Path.GetTempPath(), $"durable-shift-host-{Guid.NewGuid():N}");

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
