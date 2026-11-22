// D3 — a local Durable orchestrator that treats Foundry agents as tools.
//
// Band D's other position. D2 asks whether Durable can live *inside* the platform.
// This asks the more common question: what if the Durable store is the thing that
// survives, and the agents are replaceable steps in a workflow the orchestrator owns?
//
// The unit of work is one durable task. Its phases are the run:
//
//   coverage  → call the coverage agent        (memoised, one service invocation)
//   gate      → wait for a human decision      (memoised, no service invocation)
//   letter    → call the letter agent          (memoised, one service invocation)
//
// ── What the store is the authority for ─────────────────────────────────────
//
// The store holds the task: its input (the request), its checkpoint (the phase and
// the step outcomes), and its memos (the result of every step that has completed).
// Foundry holds the transcript of each individual response. Neither is consulted to
// reconstruct the other: the orchestrator reads the *store* to decide what to do
// next, and sends an agent only the transcript it is given as input.
//
// ── The memo key, which is the whole judgement call ─────────────────────────
//
// A step's memo key is
//
//     step/{step}/{inputDigest}/{agentIdentity}
//
// where `agentIdentity` is `{name}@{version}:{definitionDigest}` — the agent's
// registered version and a SHA-256 over the definition the platform says it is
// running. The name and the input are the obvious half. The identity is the half a
// naive key leaves out, and leaving it out is a silent correctness bug rather than a
// visible one: edit the agent's instructions and a key made only of
// name+input still matches, so the orchestrator reuses a result the *old* agent
// produced and reports a run the current agent never performed. `--memo-key naive`
// is that bug, kept in the file so the evidence can show it failing.
//
// The identity is read at **call time**, from the platform, not frozen into the task
// input when the run is created. Freezing it would make requirement 3 vacuous: a run
// created before an edit would keep calling with the old identity and a changed
// definition would never be noticed. The agent is a dependency that can move under a
// run, so it is resolved when the step runs.
//
// ── A live trap in this layer, avoided deliberately ─────────────────────────
//
// `TaskRuntime.MemoAsync` takes the store's commit gate (`DurableStore._gate`), and so
// does `CommitAsync`. Calling `MemoAsync` *inside* a `CommitAsync` callback therefore
// waits for a semaphore the caller already holds and hangs forever — no exception, no
// timeout, just a stuck phase. This file is written so that never happens: every memo
// read and write sits between commits, never within one.
//
// ── The order of the two writes, which is a decision ────────────────────────
//
// A completed agent call leaves two things to record: its result (a memo) and the run's
// position (a checkpoint that moves the phase on). Band B commits the checkpoint first
// and the memo second, on the argument that a checkpoint can rebuild a missing memo.
// That is right for a step whose result the checkpoint *carries*, and it is what this
// file does too — but it leaves "the phase has not moved yet" and "the memo is written"
// as two independent facts, so a later re-entry into the same phase is a memo **hit**
// rather than a re-call, which is the thing requirement 2 asks to see.
//
// Concretely: after the agent returns, the memo is written, then the checkpoint commits.
// A process that dies in the gap resumes the same phase, finds the memo, and does not
// call the agent again. `STOP_AFTER_MEMO=1` stops exactly there, which is how the
// evidence shows the guard working rather than merely present. The window this does not
// close is the one before the memo: an agent call that returns and then loses its
// process before the memo write is re-issued, and nothing in the store can tell that
// from a call that never started. That is the honest limit, and the demonstration
// releases the orchestrator only at the seams the store already knows about.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property NoWarn=GHCP001;NU1903;CS1591;IL2026;IL3050;CA2266
#:include ../../lib/Durable.cs

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Durable;

return await Orchestrator.MainAsync(args);

// ─── The run's shape ────────────────────────────────────────────────────────

/// <summary>What the orchestrator needs to know about the run, written once.</summary>
public sealed record RunInput(string RequestId, string Request, string Reviewer, int GateTimeoutSeconds);

/// <summary>Where the run is, and what each settled step produced.</summary>
public sealed record RunCheckpoint
{
    public string Phase { get; init; } = "coverage";
    public StepResult? Coverage { get; init; }
    public StepResult? Gate { get; init; }
    public StepResult? Letter { get; init; }
    public string? Failure { get; init; }
}

/// <summary>One settled step: what it produced and which agent identity produced it.</summary>
public sealed record StepResult(string Step, string MemoKey, string AgentIdentity, string Output);

/// <summary>An agent as the platform reports it right now.</summary>
public sealed record AgentIdentity(string Name, string Version, string DefinitionDigest, string DefinitionJson)
{
    /// <summary>
    /// The identity string a memo key carries. The digest is truncated because 64 hex
    /// characters in every key makes the store unreadable for no gain: 16 hex is 64 bits,
    /// and a collision between two definitions of the same agent would have to be found
    /// deliberately rather than happen.
    /// </summary>
    public string Key => $"{Name}@{Version}:{ShortDigest(DefinitionDigest)}";

    /// <summary>
    /// The gate is not an agent, but it is a step with a result that must not be
    /// recomputed, so it gets an identity too: a fixed name and no definition to digest.
    /// </summary>
    public static AgentIdentity Human => new("human", "n/a", Orchestrator.Digest("human"), "{}");

    private static string ShortDigest(string digest) =>
        digest.Length >= 16 ? digest[..16] : digest;
}

/// <summary>The run's final answer.</summary>
public sealed record RunResult(
    string RequestId,
    string Recommendation,
    string Decision,
    string Determination,
    string Letter,
    IReadOnlyList<StepResult> Steps);

public static class Orchestrator
{
    private const string TaskName = "band-d3.prior-auth";

    /// <summary>
    /// The run's own cancellation, owned by <see cref="MainAsync"/> and reachable from a
    /// phase. A phase that has committed what it wants to keep cancels this to stop the
    /// process cleanly: the runner catches the cancellation, leaves the task `Running` at
    /// its last checkpoint, and the next process re-enters that phase. Nothing is
    /// re-created, which is why the seam is a cancellation and not an exit.
    /// </summary>
    private static CancellationTokenSource? RunStop;

    public static async Task<int> MainAsync(string[] args)
    {
        var runDir = Require("RUN_DIR");
        var storePath = Path.Combine(runDir, "store", "orchestrator.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        Directory.CreateDirectory(Path.Combine(runDir, "gate"));

        // `naive` is the mutation the evidence runs as a control: it drops the agent
        // identity from the key, which is the bug this variant exists to avoid.
        var keyMode = Environment.GetEnvironmentVariable("MEMO_KEY_MODE") is { Length: > 0 } mode
            ? mode
            : "identity";

        var resume = File.Exists(storePath);
        Console.WriteLine($"run dir:    {runDir}");
        Console.WriteLine($"store:      {storePath}");
        Console.WriteLine($"mode:       {(resume ? "resume the store's run" : "new run")}");
        Console.WriteLine($"memo key:   {(keyMode == "naive" ? "NAIVE (name+input only — the control)" : "identity (name+input+agent identity)")}");
        Console.WriteLine(new string('-', 78));

        var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = storePath,
            Format = DurableStoreFormat.Jsonl,
        });

        // `named` is the only thing the registry needs here: this orchestrator drives no
        // Copilot conversation, so the definition's phases are the whole program.
        var registry = new ExtensionRegistry();
        var definition = Definition(keyMode);
        registry.Install(new Extension("band-d3", tasks: [definition]));

        try
        {
            using var stop = new CancellationTokenSource();
            RunStop = stop;

            await using var harness = await DurableHarness.OpenAsync(store, registry, stop.Token);

            var prior = store.Tasks.FirstOrDefault(task => task.Name == TaskName);
            DurableTaskRecord record;
            if (prior is null)
            {
                var request = Require("RUN_REQUEST");
                var input = new RunInput(
                    RequestId: Environment.GetEnvironmentVariable("RUN_ID") is { Length: > 0 } id
                        ? id
                        : DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"),
                    Request: request,
                    Reviewer: Environment.GetEnvironmentVariable("REVIEWER") is { Length: > 0 } reviewer
                        ? reviewer
                        : "(unset)",
                    GateTimeoutSeconds: int.TryParse(
                        Environment.GetEnvironmentVariable("GATE_TIMEOUT_SECONDS"), out var timeout)
                        ? timeout
                        : 600);

                record = await harness.CreateTaskAsync(
                    definition,
                    DurableJson.ToElement(input),
                    cancellationToken: stop.Token);
                Console.WriteLine($"task:       new durable task {record.Id} (request {input.RequestId})");
            }
            else
            {
                var checkpoint = prior.Checkpoint.Deserialize<RunCheckpoint>(DurableJson.Options) ?? new();
                Console.WriteLine($"task:       resuming durable task {prior.Id}"
                    + $" (restarts before this process: {prior.Restarts})");
                Console.WriteLine($"phase:      {checkpoint.Phase}");
                Console.WriteLine($"memos:      {prior.Memos.Count} on record");
                foreach (var (key, value) in prior.Memos.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var settled = value.Deserialize<StepResult>(DurableJson.Options);
                    Console.WriteLine($"  {key}");
                    Console.WriteLine($"    → {settled?.Step}: {(settled?.Output.Length > 96 ? settled.Output[..96] + "…" : settled?.Output)}");
                }

                Console.WriteLine($"memos:      {prior.Memos.Count} on record — a phase that re-enters looks these up before it calls anything");

                if (prior.Status == Durable.TaskStatus.Terminal)
                {
                    Console.WriteLine("task:       already finished; nothing to do.");
                    return Report(prior.Outcome);
                }

                record = prior;
            }

            Console.WriteLine(new string('-', 78));
            try
            {
                var outcome = await harness.WaitForTaskAsync(record.Id, stop.Token);
                return Report(DurableJson.ToElement(outcome));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The seams cancel this token after committing what they wanted to keep.
                // The task is still `Running` at its last checkpoint, which is a state the
                // next process resumes from — so this is a normal stop, not a failure.
                Console.WriteLine(new string('-', 78));
                Console.WriteLine("stopped at a committed boundary.");
                Console.WriteLine("  the store holds the checkpoint; the next process re-enters the phase.");
                return 0;
            }
        }
        finally
        {
            store.Dispose();
        }
    }

    private static int Report(JsonElement? stored)
    {
        var outcome = stored is { } element
            ? element.Deserialize<Durable.TaskOutcome>(DurableJson.Options)
            : null;

        Console.WriteLine(new string('-', 78));
        if (outcome is null)
        {
            Console.Error.WriteLine("orchestrator: the store holds no outcome.");
            return 1;
        }

        if (!outcome.IsCompleted)
        {
            Console.Error.WriteLine($"orchestrator: run did not complete: {outcome.Status} {outcome.Error}");
            return 1;
        }

        var result = outcome.Result is { ValueKind: JsonValueKind.Object } value
            ? value.Deserialize<RunResult>(DurableJson.Options)
            : null;

        Console.WriteLine("run complete.");
        Console.WriteLine($"  recommendation : {result?.Recommendation}");
        Console.WriteLine($"  decision       : {result?.Decision}");
        Console.WriteLine($"  determination  : {result?.Determination}");
        foreach (var step in result?.Steps ?? [])
        {
            Console.WriteLine($"  step {step.Step,-9} ← {step.AgentIdentity}  key {step.MemoKey}");
        }

        return 0;
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set");

    // ─── The durable task definition ────────────────────────────────────────

    private static DurableTaskDefinition Definition(string keyMode) =>
        DurableTask.DefinePhases<RunInput, RunCheckpoint>(
            TaskName,
            () => new RunCheckpoint(),
            new Dictionary<string, Func<RunInput, RunCheckpoint, TaskRuntime, Task<TaskCommit>>>
            {
                ["coverage"] = (input, checkpoint, runtime) => CoverageAsync(input, checkpoint, runtime, keyMode),
                ["gate"] = (input, checkpoint, runtime) => GateAsync(input, checkpoint, runtime, keyMode),
                ["letter"] = (input, checkpoint, runtime) => LetterAsync(input, checkpoint, runtime, keyMode),
            });

    // ─── Phase 1: the coverage agent ────────────────────────────────────────

    private static async Task<TaskCommit> CoverageAsync(
        RunInput input,
        RunCheckpoint checkpoint,
        TaskRuntime runtime,
        string keyMode)
    {
        Console.WriteLine("[coverage] resolving the agent the platform is running now");
        var agent = await Foundry.ResolveAgentAsync("d3-coverage-assessor");
        Console.WriteLine($"[coverage] identity {agent.Key}");

        var requestDigest = Digest(input.Request);
        var key = MemoKey("coverage", requestDigest, agent, keyMode);

        // Read the memo *outside* any transaction: see the note at the top of the file.
        var settled = await ReadMemoAsync(runtime, key);
        if (settled is not null)
        {
            Console.WriteLine($"[coverage] memo hit {key} — the service is not called");
            Console.WriteLine($"[coverage] reusing {settled.AgentIdentity}");

            // The stop seam has to hold on this branch too, or a memo hit would walk on to
            // the gate and the measurement would be waiting for a person instead of counting.
            // StopAtCoverageAsync never returns: it either completes the run or cancels the
            // process. The `return` after it is what tells the compiler so.
            if (StopAfterCoverage() is "1" or "stuck")
            {
                await StopAtCoverageAsync(runtime, input, checkpoint, settled);
                return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(checkpoint)));
            }

            return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(checkpoint with
            {
                Phase = "gate",
                Coverage = settled,
            })));
        }

        Console.WriteLine($"[coverage] memo miss {key} — calling the agent");
        var output = await Foundry.InvokeAsync(agent, input.Request);
        var step = new StepResult("coverage", key, agent.Key, output);

        // The memo is the record of the effect, so it is written before the phase moves.
        // Nothing takes the commit gate here: MemoAsync takes it itself.
        await runtime.MemoAsync(key, DurableJson.ToElement(step));
        Console.WriteLine($"[coverage] settled; memo {key} written");

        // A demonstration that only measures the coverage step stops here. Two shapes, and
        // the difference is the point of the memo-key evidence:
        //
        //   STOP_AFTER_COVERAGE=1        the run finishes with the step recorded
        //   STOP_AFTER_COVERAGE=stuck    the run stays at `coverage`, settled but not advanced
        //
        // "stuck" is the store a killed process leaves: the phase has not moved, so a later
        // process re-enters it. That is what makes a memo hit meaningful rather than a
        // resume that had nothing left to do.
        if (StopAfterCoverage() is "1" or "stuck")
        {
            await StopAtCoverageAsync(runtime, input, checkpoint, step);
            return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(checkpoint)));
        }

        // The seam between "the effect is recorded" and "the run has moved on". A process
        // stopped here leaves the checkpoint still at `coverage`, so the next process
        // re-enters this phase and must find the memo rather than call the agent.
        if (Environment.GetEnvironmentVariable("STOP_AFTER_MEMO") == "1")
        {
            Console.WriteLine("[coverage] STOP_AFTER_MEMO: the memo is written, the checkpoint is not advanced.");
            Console.Out.Flush();
            runtime.CancellationToken.ThrowIfCancellationRequested();
        }

        return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(checkpoint with
        {
            Phase = "gate",
            Coverage = step,
        })));
    }

    private static string StopAfterCoverage() =>
        Environment.GetEnvironmentVariable("STOP_AFTER_COVERAGE") ?? "";

    /// <summary>
    /// Ends the process at the coverage step, in one of the two shapes the evidence needs.
    /// </summary>
    /// <remarks>
    /// <c>1</c> finishes the run with the step recorded. <c>stuck</c> commits the phase's
    /// unchanged checkpoint and cancels the process, so the task stays at <c>coverage</c>
    /// and the next process re-enters the phase — which is the state a killed process
    /// leaves, and the only state in which "no call" can mean "the memo was consulted"
    /// rather than "there was nothing left to do".
    /// </remarks>
    private static async Task StopAtCoverageAsync(
        TaskRuntime runtime,
        RunInput input,
        RunCheckpoint checkpoint,
        StepResult step)
    {
        if (StopAfterCoverage() == "1")
        {
            Console.WriteLine("[coverage] STOP_AFTER_COVERAGE: the step is recorded, the run stops here.");
            await runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(
                new RunResult(input.RequestId, "(not synthesized)", "(not gated)", "(not gated)", "", [step]))));
            return;
        }

        Console.WriteLine("[coverage] STOP_AFTER_COVERAGE=stuck: the phase is not advanced.");
        await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(checkpoint)));
        Console.WriteLine("[coverage] cancelling this process; the next one re-enters coverage.");
        Console.Out.Flush();
        RunStop?.Cancel();
        // Unreachable in practice: the runner observes the cancellation and unwinds.
        throw new OperationCanceledException(RunStop?.Token ?? CancellationToken.None);
    }

    // ─── Phase 2: the human approval gate ───────────────────────────────────
    //
    // This phase makes no service call at all. It writes what a person needs and waits
    // for them to answer. The wait is the seam the demonstration kills at: the process
    // is alive, a call has completed, and the next call has not started.

    private static async Task<TaskCommit> GateAsync(
        RunInput input,
        RunCheckpoint checkpoint,
        TaskRuntime runtime,
        string keyMode)
    {
        var coverage = checkpoint.Coverage
            ?? throw new InvalidOperationException("gate reached with no coverage step recorded");

        var runDir = Require("RUN_DIR");
        var requestPath = Path.Combine(runDir, "gate", "request.json");
        var decisionPath = Path.Combine(runDir, "gate", "decision.json");

        if (!File.Exists(requestPath))
        {
            var request = new
            {
                request_id = input.RequestId,
                reviewer = input.Reviewer,
                question = "Approve this prior-authorization recommendation?",
                produced_by = coverage.AgentIdentity,
                recommendation = coverage.Output,
                decided_by = "a person, writing gate/decision.json",
            };
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[gate] request written to {requestPath}");
        }

        // The gate is keyed by what is being approved, so resuming after a decision does
        // not ask twice, and a *different* recommendation does get asked about again.
        var gateKey = MemoKey("gate", Digest(coverage.Output), AgentIdentity.Human, keyMode);
        var settledGate = await ReadMemoAsync(runtime, gateKey);

        if (settledGate is null)
        {
            Console.WriteLine($"[gate] WAITING for a human decision at {decisionPath}");
            Console.WriteLine($"[gate] process id {Environment.ProcessId} — this is the process to kill");
            Console.Out.Flush();

            var deadline = DateTime.UtcNow.AddSeconds(input.GateTimeoutSeconds);
            while (!File.Exists(decisionPath))
            {
                if (DateTime.UtcNow > deadline)
                {
                    return await runtime.CommitAsync(() => TaskCommit.Failed(
                        $"gate timed out after {input.GateTimeoutSeconds}s with no decision"));
                }

                await System.Threading.Tasks.Task.Delay(500);
            }

            var decisionJson = File.ReadAllText(decisionPath);
            var decision = JsonNode.Parse(decisionJson) ?? throw new InvalidOperationException("decision is not JSON");
            var action = decision["action"]?.GetValue<string>()
                ?? throw new InvalidOperationException("decision has no action");
            if (action is not ("approve" or "pend_for_review"))
            {
                return await runtime.CommitAsync(() => TaskCommit.Failed(
                    $"decision action '{action}' is not approve or pend_for_review"));
            }

            var reviewer = decision["reviewer"]?.GetValue<string>() ?? input.Reviewer;
            settledGate = new StepResult("gate", gateKey, "human", $"{action}|{reviewer}|{decision["note"]?.GetValue<string>()}");
            Console.WriteLine($"[gate] decision: {action} by {reviewer}");

            // The same order as every other step: record the decision, then move on.
            await runtime.MemoAsync(gateKey, DurableJson.ToElement(settledGate));
            Console.WriteLine($"[gate] settled; memo {gateKey} written");
            return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(checkpoint with
            {
                Phase = "letter",
                Coverage = coverage,
                Gate = settledGate,
            })));
        }

        Console.WriteLine($"[gate] memo hit {gateKey} — the decision is already recorded ({settledGate.Output})");
        return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(checkpoint with
        {
            Phase = "letter",
            Coverage = coverage,
            Gate = settledGate,
        })));
    }

    // ─── Phase 3: the letter agent ──────────────────────────────────────────

    private static async Task<TaskCommit> LetterAsync(
        RunInput input,
        RunCheckpoint checkpoint,
        TaskRuntime runtime,
        string keyMode)
    {
        var coverage = checkpoint.Coverage
            ?? throw new InvalidOperationException("letter reached with no coverage step recorded");
        var gate = checkpoint.Gate
            ?? throw new InvalidOperationException("letter reached with no gate step recorded");

        Console.WriteLine("[letter] resolving the agent the platform is running now");
        var agent = await Foundry.ResolveAgentAsync("d3-decision-letter");
        Console.WriteLine($"[letter] identity {agent.Key}");

        var decisionDigest = Digest(gate.Output);
        var key = MemoKey("letter", decisionDigest, agent, keyMode);

        var settled = await ReadMemoAsync(runtime, key);
        if (settled is not null)
        {
            Console.WriteLine($"[letter] memo hit {key} — the service is not called");
            return await FinishAsync(runtime, input, coverage, gate, settled);
        }

        Console.WriteLine($"[letter] memo miss {key} — calling the agent");
        var prompt = $"""
            COVERAGE ASSESSMENT
            {coverage.Output}

            REVIEWER DECISION
            {gate.Output}

            Write the determination summary for request {input.RequestId}.
            """;
        var output = await Foundry.InvokeAsync(agent, prompt);
        var step = new StepResult("letter", key, agent.Key, output);

        await runtime.MemoAsync(key, DurableJson.ToElement(step));
        Console.WriteLine($"[letter] settled; memo {key} written");
        return await FinishAsync(runtime, input, coverage, gate, step);
    }

    private static Task<TaskCommit> FinishAsync(
        TaskRuntime runtime,
        RunInput input,
        StepResult coverage,
        StepResult gate,
        StepResult letter)
    {
        var (action, reviewer, _) = SplitGate(gate.Output);
        var result = new RunResult(
            RequestId: input.RequestId,
            Recommendation: ExtractRecommendation(coverage.Output),
            Decision: $"{action} by {reviewer}",
            Determination: ExtractDetermination(letter.Output),
            Letter: letter.Output,
            Steps: [coverage, gate, letter]);
        return runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(result)));
    }

    private static (string Action, string Reviewer, string Note) SplitGate(string output)
    {
        var parts = output.Split('|');
        return (parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
    }

    private static string ExtractRecommendation(string text)
    {
        foreach (var line in text.Split('\n').Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("RECOMMENDATION:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["RECOMMENDATION:".Length..].Trim();
            }
        }

        return "(no RECOMMENDATION line found)";
    }

    private static string ExtractDetermination(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DETERMINATION:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["DETERMINATION:".Length..].Trim();
            }
        }

        return "(no DETERMINATION line found)";
    }

    // ─── The memo key ───────────────────────────────────────────────────────

    private static string MemoKey(string step, string inputDigest, AgentIdentity agent, string keyMode) =>
        keyMode == "naive"
            // The control. Everything a run knows at the moment it is written, and nothing
            // about the agent: an edited definition still matches.
            ? $"step/{step}/{inputDigest[..16]}"
            : $"step/{step}/{inputDigest[..16]}/{agent.Key}";

    private static async Task<StepResult?> ReadMemoAsync(TaskRuntime runtime, string key)
    {
        var memo = await runtime.MemoAsync(key);
        return memo is null ? null : memo.Value.Deserialize<StepResult>(DurableJson.Options);
    }

    internal static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

// ─── Foundry, as the orchestrator's tool surface ────────────────────────────

/// <summary>
/// The two things the orchestrator does to the platform: read the agent's current
/// identity, and call it. Both are plain HTTP against the project endpoint; the token
/// comes from `az` in the caller's environment, so this file holds no credential.
/// </summary>
public static class Foundry
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    private static Uri BaseUri =>
        new(Require("FOUNDRY_BASE_URL").TrimEnd('/') + "/");

    public static async Task<AgentIdentity> ResolveAgentAsync(string name)
    {
        // The highest *active* version is what a call without a pin would use, and pinning
        // it in the request is what makes the identity we key on the identity that ran.
        using var response = await SendAsync(HttpMethod.Get, $"agents/{name}/versions?api-version=v1");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var versions = document.RootElement.TryGetProperty("data", out var data) ? data : default;
        if (versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"agent '{name}' has no versions");
        }

        // Newest *active* version, ordered numerically: the platform returns versions in
        // its own order (v2 can precede v1), so a string or list-order pick is wrong.
        var active = versions.EnumerateArray()
            .Where(item => item.TryGetProperty("status", out var status) && status.GetString() == "active")
            .OrderByDescending(item => int.TryParse(item.GetProperty("version").GetString(), out var v) ? v : 0)
            .ThenByDescending(item => item.TryGetProperty("created_at", out var created) ? created.GetInt64() : 0)
            .FirstOrDefault();
        if (active.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"agent '{name}' has no active version");
        }

        var version = active.GetProperty("version").GetString()!;

        // The digest is over the definition with sorted properties, so key order in the
        // platform's own JSON does not change an agent's identity.
        var definition = Canonicalize(active.GetProperty("definition"));
        return new AgentIdentity(name, version, Orchestrator.Digest(definition), definition);
    }

    public static async Task<string> InvokeAsync(AgentIdentity agent, string prompt)
    {
        var body = JsonSerializer.Serialize(new { input = prompt, stream = false });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        // The version pin is what closes the gap between "the definition I hashed" and "the
        // definition that answered". Without it a version registered between the read and
        // the call would run while the memo record named the older identity.
        using var response = await SendAsync(
            HttpMethod.Post,
            $"agents/{agent.Name}/endpoint/protocols/openai/responses?api-version=v1&agent_version={agent.Version}",
            content);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (status != "completed")
        {
            throw new InvalidOperationException(
                $"agent '{agent.Name}' returned '{status}': "
                + (root.TryGetProperty("error", out var error) ? error.ToString() : "(no error)"));
        }

        if (root.TryGetProperty("output_text", out var text) && text.GetString() is { Length: > 0 } direct)
        {
            return direct;
        }

        var builder = new StringBuilder();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var value) && value.GetString() is { } chunk)
                    {
                        builder.Append(chunk);
                    }
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Sends one request, retrying a transport failure and a 429/5xx.
    /// </summary>
    /// <remarks>
    /// A busy ephemeral port on this machine produces a `Connection refused` that says
    /// nothing about the service, and the project endpoint answers 429 under load. Retrying
    /// those here rather than in the caller keeps the retry from being mistaken for a
    /// second logical call: the memo is keyed on the step, and a retried *request* is still
    /// one step. Safe for `GET` (a read) and for the responses call, which is keyed by the
    /// request body and not a mutation of the run.
    /// </remarks>
    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, HttpContent? body = null)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            HttpRequestMessage request;
            if (body is null)
            {
                request = new HttpRequestMessage(method, new Uri(BaseUri, relative));
            }
            else
            {
                // A sent request's content cannot be reused, so each attempt gets its own copy.
                var payload = await body.ReadAsStringAsync();
                request = new HttpRequestMessage(method, new Uri(BaseUri, relative))
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Require("FOUNDRY_TOKEN"));

            try
            {
                var response = await Http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var detail = await response.Content.ReadAsStringAsync();
                var status = (int)response.StatusCode;
                response.Dispose();
                if (status is not (429 or >= 500) || attempt == 4)
                {
                    throw new InvalidOperationException($"{(int)response.StatusCode} on {method} {relative}: {detail}");
                }

                Console.WriteLine($"[foundry] {status} on {method} {relative}; retrying (attempt {attempt + 1})");
                last = new InvalidOperationException($"{status}: {detail}");
            }
            catch (HttpRequestException transport) when (attempt < 4)
            {
                Console.WriteLine($"[foundry] transport failure on {method} {relative} ({transport.Message}); retrying (attempt {attempt + 1})");
                last = transport;
            }

            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(attempt * 3));
        }

        throw new InvalidOperationException($"could not reach {method} {relative}", last);
    }

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set");
}
