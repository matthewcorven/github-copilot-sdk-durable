using System.Diagnostics;
using System.Text.Json.Nodes;
using Durable;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// End-to-end member lifecycle runs. Each test starts the host as its own process over a real
/// store file, because the lifecycle's contract is a process contract: a transition committed by
/// one invocation is the state the next invocation reads and validates against.
/// </summary>
/// <remarks>
/// No turn is run and no model is called: the member command is store state, so each invocation
/// costs one process start. The store is JSONL so the assertions can read the same bytes the next
/// process reads.
/// </remarks>
public sealed class MemberCliTests
{
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(60);
    private const string Now = "2026-11-21T12:00:00Z";

    [Fact]
    public async Task Member_HireActivatePauseResumeEachCommitsAndPrintsTheNewState()
    {
        using var scratch = new Scratch();
        var store = scratch.PathIn("store.jsonl");

        var hire = await RunMemberAsync("hire", "--member", "alice", "--store", store, "--now", Now);
        Assert.Equal(0, hire.ExitCode);
        Assert.Equal("succeeded", (string?)JsonNode.Parse(hire.Stdout)!["outcome"]);
        Assert.Equal("benched", (string?)JsonNode.Parse(hire.Stdout)!["state"]!["state"]);
        Assert.Equal(1, (long?)JsonNode.Parse(hire.Stdout)!["state"]!["epoch"]);

        var activate = await RunMemberAsync("activate", "--member", "alice", "--store", store, "--now", Now);
        Assert.Equal(0, activate.ExitCode);
        Assert.Equal("active", (string?)JsonNode.Parse(activate.Stdout)!["state"]!["state"]);
        Assert.Equal(2, (long?)JsonNode.Parse(activate.Stdout)!["state"]!["epoch"]);

        var pause = await RunMemberAsync("pause", "--member", "alice", "--store", store, "--now", Now);
        Assert.Equal(0, pause.ExitCode);
        Assert.Equal("paused", (string?)JsonNode.Parse(pause.Stdout)!["state"]!["state"]);

        var resume = await RunMemberAsync("resume", "--member", "alice", "--store", store, "--now", Now);
        Assert.Equal(0, resume.ExitCode);
        Assert.Equal("active", (string?)JsonNode.Parse(resume.Stdout)!["state"]!["state"]);
        Assert.Equal(4, (long?)JsonNode.Parse(resume.Stdout)!["state"]!["epoch"]);

        var show = await RunMemberAsync("show", "--member", "alice", "--store", store);
        Assert.Equal(0, show.ExitCode);
        Assert.Equal("active", (string?)JsonNode.Parse(show.Stdout)!["state"]!["state"]);
    }

    [Fact]
    public async Task Member_AnInvalidTransitionExitsNonZeroWithAnActionableMessage()
    {
        using var scratch = new Scratch();
        var store = scratch.PathIn("store.jsonl");
        Assert.Equal(0, (await RunMemberAsync("hire", "--member", "alice", "--store", store, "--now", Now)).ExitCode);

        // benched -> paused is valid, so the interesting invalid case is pausing a benched member
        // twice: the second pause has no valid transition and must say so.
        Assert.Equal(0, (await RunMemberAsync("pause", "--member", "alice", "--store", store, "--now", Now)).ExitCode);
        var refused = await RunMemberAsync("pause", "--member", "alice", "--store", store, "--now", Now);

        Assert.NotEqual(0, refused.ExitCode);
        var result = JsonNode.Parse(refused.Stdout)!.AsObject();
        Assert.Equal("refused", (string?)result["outcome"]);
        Assert.Contains("paused", (string?)result["message"], StringComparison.Ordinal);
        Assert.Contains("active", (string?)result["message"], StringComparison.Ordinal);

        // Nothing changed: the state is still the first pause.
        Assert.Equal("paused", (string?)result["state"]!["state"]);
    }

    [Fact]
    public async Task Member_ReplaceCommitsTheSuccessorAndPrintsTheHandoff()
    {
        using var scratch = new Scratch();
        var store = scratch.PathIn("store.jsonl");
        await RunMemberAsync("hire", "--member", "alice", "--store", store, "--now", Now);

        var replace = await RunMemberAsync(
            "replace", "--member", "alice", "--successor", "bob",
            "--context", "checklist is in the transcript", "--store", store, "--now", Now);

        Assert.Equal(0, replace.ExitCode);
        var result = JsonNode.Parse(replace.Stdout)!.AsObject();
        Assert.Equal("replaced", (string?)result["state"]!["state"]);
        Assert.Equal("bob", (string?)result["state"]!["replacedBy"]);
        Assert.Equal("bob", (string?)result["handoff"]!["successor"]);
        Assert.Equal("checklist is in the transcript", (string?)result["handoff"]!["context"]);

        var successor = await RunMemberAsync("show", "--member", "bob", "--store", store);
        Assert.Equal(0, successor.ExitCode);
        Assert.Equal("benched", (string?)JsonNode.Parse(successor.Stdout)!["state"]!["state"]);

        // The predecessor is terminal: a further transition is refused rather than resurrecting it.
        var again = await RunMemberAsync("activate", "--member", "alice", "--store", store, "--now", Now);
        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains("terminal", (string?)JsonNode.Parse(again.Stdout)!["message"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Member_AnUnknownActionIsAUsageError()
    {
        using var scratch = new Scratch();

        var result = await RunMemberAsync("levitate", "--member", "alice", "--store", scratch.PathIn("store.jsonl"));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unknown member action", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("hire", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Member_AnExpectedEpochMismatchIsRefused()
    {
        using var scratch = new Scratch();
        var store = scratch.PathIn("store.jsonl");
        await RunMemberAsync("hire", "--member", "alice", "--store", store, "--now", Now);
        await RunMemberAsync("activate", "--member", "alice", "--store", store, "--now", Now);

        var stale = await RunMemberAsync(
            "pause", "--member", "alice", "--expected-epoch", "1", "--store", store, "--now", Now);

        Assert.NotEqual(0, stale.ExitCode);
        Assert.Contains("epoch", (string?)JsonNode.Parse(stale.Stdout)!["message"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("active", (string?)JsonNode.Parse(stale.Stdout)!["state"]!["state"]);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunMemberAsync(params string[] args)
    {
        using var process = StartMember(args);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(HostTimeout);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static Process StartMember(string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HostPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("member");
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("the runtime host did not start.");
    }

    private static string HostPath() => Existing(
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Durable.Runtime.exe" : "Durable.Runtime"));

    private static string Existing(string path) =>
        File.Exists(path) ? path : throw new FileNotFoundException($"the test asset is missing: '{path}'.", path);

    private sealed class Scratch : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"durable-member-host-{Guid.NewGuid():N}");

        public Scratch() => Directory.CreateDirectory(_directory);

        public string PathIn(string name) => Path.Combine(_directory, name);

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
