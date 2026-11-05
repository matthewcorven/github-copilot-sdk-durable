// Ralph Loop, Durable variant (Band B, issue #150).
//
// ../baseline/ralph-loop.cs keeps the loop position in a local variable. This variant keeps it
// in the Durable store: one durable task whose checkpoint is the loop position, so a process that
// stops after iteration N leaves a record and the next process continues at N+1 instead of
// re-reading the markdown and starting over.
//
// Same job, same prompt, same verify.sh. Only the progress model changes.
//
//   ./run.sh        first process: create the run, iterate, stop at STOP_AFTER_ITERATION or the cap
//   ./resume.sh     every later process: reopen the store and continue
//
// ── What is durable, and what the memo key is ───────────────────────────────
//
// One durable task (`band-b.ralph-loop`) holds the whole run:
//
//   input       Target, PlanFile, PromptFile, MaxIterations, ItemsPerPass — written once
//   checkpoint  Iteration, Stage, PlanDigest, Steps
//
// The iteration cap is part of the input, so it belongs to the run rather than to the process. A
// resumed run cannot get a fresh cap: it reads the record the first process wrote.
//
// One iteration is one checkpoint, advanced around the step that mutates files:
//
//   before   { Iteration: n-1, Stage: "turn-started",       PlanDigest: <digest> }
//   after    { Iteration: n-1, Stage: "turn-complete",      Steps: [agent-turn] }
//   settled  { Iteration: n,   Stage: "iteration-complete", Steps: [agent-turn, reconcile] }
//
// The step whose side effects must not repeat is the agent turn, and the guard is a MemoAsync
// keyed by exactly where the step sits in the run:
//
//     step/{iteration}/agent-turn          step/{iteration}/reconcile
//
// Neither component is a timestamp, a conversation id, or a file digest — all three differ
// between two attempts at the same step, which is exactly how a memo key silently duplicates
// side effects on resume. `iteration` is monotonic and `step` is a fixed label, so the key names
// the same work however many processes try it.
//
// The limit, stated plainly: the memo boundary is the turn, not the tool call inside it, and the
// difference is *visibility*, not memory. The layer records every completed tool call
// (`RecordToolCheckpoint` in lib/Durable.cs) into `durable.checkpoint` after the fact, so a turn
// that finished can be read back from the store. What it cannot do is *continue* a half-finished
// turn — and it does not commit those tool records at the call either, but at the turn's
// completion barrier, so a turn that died mid-way may have left none of them. The SDK will not
// resume such a turn and this variant will not re-issue one, because re-running it could repeat the
// writes it already made: on re-entry with `Stage = "turn-started"` it reports what the store holds
// and fails the run. So a dead turn's work is observable, not replayable, and only as observable as
// the records that had committed. See ../evidence/durable-kill-and-resume.md §3a–§3c.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001;NU1903;CS1591;IL2026;IL3050;CA2266
#:include ../../../lib/Durable.cs

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Durable;
using GitHub.Copilot;

var exitCode = await DurableLoopAsync(args);
return exitCode;

static async Task<int> DurableLoopAsync(string[] args)
{
    var target = Directory.GetCurrentDirectory();
    var planPath = Path.Combine(target, Job.PlanFile);
    if (!File.Exists(planPath))
    {
        Console.Error.WriteLine($"durable: no {Job.PlanFile} in {target}");
        return 2;
    }

    var maxIterations = Job.DefaultMaxIterations;
    if (args.Length > 0 && int.TryParse(args[0], out var requested) && requested > 0)
    {
        maxIterations = requested;
    }

    // STOP_AFTER_ITERATION is the deterministic seam: a process that commits iteration N stops
    // there, at a boundary the store already knows about. An environment variable rather than a
    // signal, because a SIGKILL in the middle of a commit would demonstrate the store's crash
    // safety, not the loop's resume behaviour.
    var stopAfter = StopAfterIteration();

    var fromEnvironment = Environment.GetEnvironmentVariable("RALPH_MODEL") is { Length: > 0 };
    var model = fromEnvironment ? Environment.GetEnvironmentVariable("RALPH_MODEL")! : Job.PinnedModel;

    var storePath = Environment.GetEnvironmentVariable("DURABLE_STORE") is { Length: > 0 } storeOverride
        ? Path.GetFullPath(storeOverride)
        : Path.Combine(target, Job.StoreDirectory, "store.jsonl");

    Console.WriteLine($"target:    {target}");
    Console.WriteLine($"store:     {storePath}");
    Console.WriteLine($"model:     {model}{(fromEnvironment ? " (RALPH_MODEL)" : " (pinned)")}");
    Console.WriteLine($"plan:      {planPath}");
    if (stopAfter > 0)
    {
        Console.WriteLine($"stopping: after iteration {stopAfter} commits (STOP_AFTER_ITERATION)");
    }

    Console.WriteLine(new string('━', 60));

    // A stop the loop asks for itself, so the process unwinds instead of being torn down: the
    // phase throws on a cancelled token, the runner leaves the task Running at its last
    // checkpoint, and this process exits normally.
    using var stop = new CancellationTokenSource();

    var store = DurableStore.Create(new DurableStoreOptions
    {
        Path = storePath,
        Format = DurableStoreFormat.Jsonl,
    });

    try
    {
        // The harness is opened over the registry, then the task definition is installed: the
        // definition has to close over the harness a turn uses to create its conversation, and
        // the harness has to exist before the task can be created.
        var registry = new ExtensionRegistry();
        await using var harness = await DurableHarness.OpenAsync(store, registry, CancellationToken.None);
        var definition = Definition(harness, model, stop, stopAfter);
        registry.Install(new Extension("band-b", tasks: [definition]));

        var prior = store.Tasks.FirstOrDefault(task => task.Name == Job.TaskName);
        if (prior is null)
        {
            Console.WriteLine($"max:       {maxIterations} iterations");
            var conversation = await harness.CreateConversationAsync(Choices(target, model));
            var record = await harness.CreateTaskAsync(
                definition,
                DurableJson.ToElement(new LoopInput
                {
                    Target = target,
                    PlanFile = Job.PlanFile,
                    PromptFile = Job.PromptFile,
                    MaxIterations = maxIterations,
                    ItemsPerPass = Job.ItemsPerPass,
                }),
                ownerConversationId: conversation.Id,
                cancellationToken: stop.Token);

            Console.WriteLine($"run:       new durable task {record.Id} (cap {maxIterations}, written to the store)");
            Console.WriteLine(new string('━', 60));

            return await AwaitRunAsync(harness, record.Id, stop);
        }

        // A run's budget is its own. The resume process may be given a different number on its
        // command line — resume.sh passes none — but the store's record wins, so a resumed run
        // cannot get a fresh cap.
        var input = prior.Input.Deserialize<LoopInput>(DurableJson.Options) ?? new LoopInput { Target = target };
        Console.WriteLine($"max:       {input.MaxIterations} iterations (from the run's stored input)");
        if (maxIterations != Job.DefaultMaxIterations && maxIterations != input.MaxIterations)
        {
            Console.WriteLine($"ignored:   -- {maxIterations} is not this run's cap; the store says {input.MaxIterations}.");
        }

        var checkpoint = prior.Checkpoint.Deserialize<LoopCheckpoint>(DurableJson.Options) ?? new();
        if (prior.Status == Durable.TaskStatus.Terminal)
        {
            var existing = prior.Outcome is { } stored
                ? stored.Deserialize<Durable.TaskOutcome>(DurableJson.Options)!
                : new Durable.TaskOutcome("completed");
            Console.WriteLine($"run:       task {prior.Id} is already finished; nothing to resume.");
            return Report(existing);
        }

        // The headline line. A second process says where it is picking up, and it read that from
        // the store — not from the markdown, and not from the command line.
        Console.WriteLine($"run:       resuming durable task {prior.Id} (restarts before this one: {prior.Restarts})");
        Console.WriteLine($"resuming at iteration {checkpoint.Iteration + 1}");
        Console.WriteLine($"last stage: {checkpoint.Stage}, plan digest {checkpoint.PlanDigest ?? "(none recorded)"}");
        foreach (var step in checkpoint.Steps)
        {
            Console.WriteLine($"  recorded step: {step.Step} — {step.Outcome}");
        }

        Console.WriteLine($"memos on record: {prior.Memos.Count}");
        Console.WriteLine(new string('━', 60));

        return await AwaitRunAsync(harness, prior.Id, stop);
    }
    finally
    {
        store.Dispose();
    }
}

static async Task<int> AwaitRunAsync(DurableHarness harness, string taskId, CancellationTokenSource stop)
{
    try
    {
        var outcome = await harness.WaitForTaskAsync(taskId, stop.Token);
        return Report(outcome);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        Console.WriteLine();
        Console.WriteLine("run:       stopped at a committed iteration boundary.");
        Console.WriteLine("           the store holds the checkpoint; the next process resumes from it.");
        return 0;
    }
}

static int Report(Durable.TaskOutcome outcome)
{
    var result = outcome.Result is { ValueKind: JsonValueKind.Object } element
        ? element.Deserialize<LoopResult>(DurableJson.Options)
        : null;

    Console.WriteLine(new string('━', 60));
    if (outcome.IsCompleted)
    {
        if (result?.Reason == "cap")
        {
            Console.WriteLine($"iteration cap reached: {result.Iterations} iterations and the plan is not complete.");
            Console.WriteLine($"first open item: {result.FirstOpenItem ?? "(none found)"}");
            return 3;
        }

        Console.WriteLine($"plan reports complete after {result?.Iterations ?? 0} iteration(s).");
        return 0;
    }

    Console.Error.WriteLine($"durable: run did not complete: {outcome.Status} {outcome.Error}");
    return 1;
}

// ─── The durable task definition ────────────────────────────────────────────
//
// Built inside the run so the phase handler closes over the live harness: a turn needs the
// harness's store to create its conversation, and the task must run on that same store.

static DurableTaskDefinition Definition(
    DurableHarness harness,
    string model,
    CancellationTokenSource stop,
    int stopAfter) =>
    DurableTask.DefinePhases<LoopInput, LoopCheckpoint>(
        Job.TaskName,
        () => new LoopCheckpoint(),
        new Dictionary<string, Func<LoopInput, LoopCheckpoint, TaskRuntime, Task<TaskCommit>>>
        {
            ["iteration"] = (input, checkpoint, runtime) =>
                RunIterationAsync(harness, model, stop, stopAfter, input, checkpoint, runtime),
        });

static async Task<TaskCommit> RunIterationAsync(
    DurableHarness harness,
    string model,
    CancellationTokenSource stop,
    int stopAfter,
    LoopInput input,
    LoopCheckpoint checkpoint,
    TaskRuntime runtime)
{
    var planPath = Path.Combine(input.Target, input.PlanFile);
    var plan = await File.ReadAllTextAsync(planPath);

    // Completion belongs to the run, not to the process: it is checked against the plan before
    // the budget, so a run that already finished stays finished when it is re-entered.
    if (IsComplete(plan))
    {
        return await runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(
            new LoopResult("plan-complete", checkpoint.Iteration, null))));
    }

    // The cap comes from the task's input, which the first process wrote once.
    if (checkpoint.Iteration >= input.MaxIterations)
    {
        return await runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(
            new LoopResult("cap", checkpoint.Iteration, FirstOpenItem(plan)))));
    }

    var iteration = checkpoint.Iteration + 1;
    var digest = Digest(plan);

    // A turn that was issued and whose outcome was never recorded. The store knows the turn
    // started and, for the tool calls whose records had committed, what it did — but not what it
    // would have done next, and the SDK will not continue it. So this does not guess: it reports
    // what the store holds and fails the run with the iteration named. Re-running the turn here
    // would duplicate whatever it did before the process died. See the limit at the top of the
    // file, and ../evidence/durable-kill-and-resume.md §3c.
    if (checkpoint.Stage == "turn-started")
    {
        var unrecorded = checkpoint.Iteration + 1;
        ReportInFlightTurn(harness.Store, unrecorded);
        return await runtime.CommitAsync(() => TaskCommit.Failed(
            $"iteration {unrecorded} was in flight: the turn was issued (plan digest {checkpoint.PlanDigest}) "
            + "and its outcome was never recorded, so it may have mutated files. Re-running it would "
            + "duplicate those mutations. Recreate the target from baseline/target/ and re-run."));
    }

    Console.WriteLine(new string('━', 60));
    Console.WriteLine($"=== Iteration {iteration}/{input.MaxIterations} ===");
    Console.WriteLine($"  plan digest: {digest}");

    // Checkpoint before the step that mutates files.
    await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(new LoopCheckpoint
    {
        Phase = "iteration",
        Iteration = checkpoint.Iteration,
        Stage = "turn-started",
        PlanDigest = digest,
        Steps = checkpoint.Steps,
    })));

    // ── Step 1: the agent turn. The only step whose side effects must not repeat. ──
    var turnKey = StepKey(iteration, "agent-turn");
    var steps = new List<StepOutcome>();
    var turnMemo = await runtime.MemoAsync(turnKey);
    if (turnMemo is not null)
    {
        var recorded = turnMemo.Value.Deserialize<StepOutcome>(DurableJson.Options)
            ?? new StepOutcome("agent-turn", "recorded");
        Console.WriteLine($"  agent-turn: memo {turnKey} is present — not run again");
        steps.Add(recorded);
    }
    else if (checkpoint.Stage == "turn-complete"
        && checkpoint.Steps.FirstOrDefault(step => step.Step == "agent-turn") is { } recordedTurn)
    {
        // The checkpoint proves the turn finished, but its memo was never written: the process
        // stopped between the commit and the memo. The outcome is already in the checkpoint, so
        // the memo is reconstructed from it and the turn is not repeated. This is the case the
        // memo key exists for, and it is why the checkpoint is written before the memo.
        Console.WriteLine($"  agent-turn: memo {turnKey} is missing but the checkpoint records the turn");
        Console.WriteLine($"  agent-turn: restoring the memo from the checkpoint — not run again");
        steps.Add(recordedTurn);
        await runtime.MemoAsync(turnKey, DurableJson.ToElement(recordedTurn));
    }
    else
    {
        Console.WriteLine($"  agent-turn: no memo {turnKey} — running the turn");
        var answer = await RunAgentTurnAsync(
            harness, input, model, BuildTurnPrompt(input, plan), iteration, stopAfter, stop);
        if (answer.Interrupted)
        {
            // Leave the checkpoint where it is: `turn-started`, with the completed tool calls in
            // the store and no outcome. The next process is the one that has to decide what that
            // means.
            throw new OperationCanceledException("STOP_MID_TURN");
        }

        var outcome = new StepOutcome("agent-turn", $"{answer.Text} ({answer.ToolCalls} tool calls)");
        steps.Add(outcome);

        // Checkpoint after the step that mutates files, before the memo that marks it done. A
        // process that stops here leaves the store saying "this turn finished", and the memo
        // is what the next process reads to decide whether to run it again.
        await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(new LoopCheckpoint
        {
            Phase = "iteration",
            Iteration = checkpoint.Iteration,
            Stage = "turn-complete",
            PlanDigest = digest,
            Steps = steps,
        })));

        // The mid-iteration seam: the turn has mutated files and committed that fact, but its
        // step memo has not been written. A resume from here must still not repeat the turn.
        if (stopAfter > 0 && iteration >= stopAfter && StopBeforeMemo())
        {
            Console.WriteLine($"stopping: iteration {iteration}'s turn is complete, the step memo is not written.");
            Console.Out.Flush();
            stop.Cancel();
            runtime.CancellationToken.ThrowIfCancellationRequested();
        }

        await runtime.MemoAsync(turnKey, DurableJson.ToElement(outcome));
        Console.WriteLine($"  agent-turn: done; memo {turnKey} written");

        // The other mid-iteration seam: the turn has finished and its step memo is written, but
        // the reconcile step and the settle checkpoint have not happened. A resume from here
        // reaches the memo-hit branch above, which is the only way to show the guard *working*
        // rather than merely present.
        if (stopAfter > 0 && iteration >= stopAfter && StopAfterMemo())
        {
            Console.WriteLine($"stopping: iteration {iteration}'s turn memo is written, the iteration is not settled.");
            Console.Out.Flush();
            stop.Cancel();
            runtime.CancellationToken.ThrowIfCancellationRequested();
        }
    }

    // ── Step 2: reconcile the plan. Verification, plan bookkeeping, and the formatting
    //    normalizer — not the job's content. It writes PLAN.md and can rewrite a section file's
    //    heading block, but it never invents the missing H1, demotes a heading, or adds a
    //    marker, so it cannot hide a turn that did nothing. ──
    var reconcileKey = StepKey(iteration, "reconcile");
    var reconcileMemo = await runtime.MemoAsync(reconcileKey);
    if (reconcileMemo is not null)
    {
        Console.WriteLine($"  reconcile: memo {reconcileKey} is present — not run again");
        steps.Add(reconcileMemo.Value.Deserialize<StepOutcome>(DurableJson.Options)
            ?? new StepOutcome("reconcile", "recorded"));
    }
    else
    {
        var reconciled = Reconcile(input.Target);
        var outcome = new StepOutcome("reconcile", reconciled);
        steps.Add(outcome);
        await runtime.MemoAsync(reconcileKey, DurableJson.ToElement(outcome));
        Console.WriteLine($"  reconcile: {reconciled}");
    }

    var afterPlan = await File.ReadAllTextAsync(planPath);
    var commit = await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(new LoopCheckpoint
    {
        Phase = "iteration",
        Iteration = iteration,
        Stage = "iteration-complete",
        PlanDigest = Digest(afterPlan),
        Steps = steps,
    })));

    Console.WriteLine($"iteration {iteration} complete.");

    // The seam: iteration N is committed, so this process stops exactly at the boundary the
    // issue's demonstration names.
    if (stopAfter > 0 && iteration >= stopAfter)
    {
        Console.WriteLine($"stopping: iteration {iteration} is committed (STOP_AFTER_ITERATION={stopAfter}).");
        Console.Out.Flush();
        stop.Cancel();
        runtime.CancellationToken.ThrowIfCancellationRequested();
    }

    return commit;
}

// ─── The agent turn ─────────────────────────────────────────────────────────

// `interrupted` is set when STOP_MID_TURN leaves the turn in flight. It is a flag on the caller's
// frame rather than an exception out of the event callback: an exception there would surface as an
// SDK failure, which is not what the state under test is.
static async Task<(string Text, int ToolCalls, bool Interrupted)> RunAgentTurnAsync(
    DurableHarness harness,
    LoopInput input,
    string model,
    string prompt,
    int iteration,
    int stopAfter,
    CancellationTokenSource stop)
{
    // A fresh durable conversation per iteration: the baseline's context isolation, kept, with
    // the transcript in the store instead of only in the runtime.
    var conversation = await harness.CreateConversationAsync(Choices(input.Target, model));

    var toolCalls = 0;
    var completed = 0;
    var interrupted = false;
    using var subscription = conversation.Session.On<SessionEvent>(evt =>
    {
        if (evt is ToolExecutionStartEvent tool)
        {
            toolCalls++;
            Console.WriteLine($"  ⚙ {tool.Data.ToolName}");
        }

        // The in-flight seam: the process leaves with the turn unfinished, which is the one state
        // a memo cannot recover. It is anchored on a tool call *completing* — not on the next one
        // starting, which would race the runtime's own dispatch — so the call count is fixed:
        // MID_TURN_COMPLETED calls are in the store, the one in flight is not. The store is closed
        // properly rather than torn down, so the record is the one a crash would leave:
        // `turn-started`, no turn checkpoint, and the completed tool calls.
        if (evt is ToolExecutionCompleteEvent && ++completed == Job.MidTurnCompleted
            && stopAfter > 0 && iteration >= stopAfter && StopMidTurn() && !interrupted)
        {
            interrupted = true;
            Console.WriteLine($"stopping: iteration {iteration}'s turn is in flight, after {completed} completed tool call(s).");
            Console.Out.Flush();
            stop.Cancel();
        }
    });

    AssistantMessageEvent? answer = null;
    try
    {
        answer = await conversation.SubmitAsync(prompt, stop.Token);
    }
    catch (OperationCanceledException) when (interrupted)
    {
        // The send was cancelled on purpose; the turn is left as an unrecorded, in-flight turn.
        // The layer queues a tool checkpoint on every completed call and awaits the queue at the
        // turn's completion barrier, so an abandoned turn has its records still in flight. The
        // store then holds the turn and not its calls, which is exactly what the resume reports.
        return (string.Empty, toolCalls, true);
    }

    var text = answer?.Data.Content ?? "(no answer)";
    var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "(empty)";
    return (Truncate(firstLine, 120), toolCalls, false);
}

static AgentChoices Choices(string target, string model) => new()
{
    Model = model,
    WorkingDirectory = target,
    // Unattended: the job edits the target and its plan. The grants are stored with the
    // conversation's choices, so the authority is in the store rather than in this process.
    Permissions = ["*"],
};

static string BuildTurnPrompt(LoopInput input, string plan)
{
    var prompt = File.ReadAllText(Path.Combine(input.Target, input.PromptFile));

    // The pass is scoped to the first open items, in order. Without a bound, one pass can tick
    // every item and an iteration is no longer a unit a resumed run can be compared against.
    var scope = OpenItems(plan).Take(input.ItemsPerPass).ToList();
    if (scope.Count == 0)
    {
        return prompt;
    }

    var brief = new StringBuilder();
    brief.AppendLine();
    brief.AppendLine("## This pass");
    brief.AppendLine();
    brief.AppendLine($"Work only these {scope.Count} item(s) from `{input.PlanFile}`, in this order:");
    foreach (var item in scope)
    {
        brief.AppendLine(item);
    }

    brief.AppendLine();
    brief.AppendLine("Do not start any other item. Do not tick an item you did not finish.");
    return prompt + brief;
}

// ─── Reading back what a dead turn did ─────────────────────────────────────

// The in-flight case is where the layer's boundary shows, so it is worth stating exactly what the
// store knows. The layer writes a `tool` checkpoint into the conversation's `durable.checkpoint`
// document for every completed tool call (see `RecordToolCheckpoint` in lib/Durable.cs), and the
// document is `DocumentHistory.Rewindable`, so a finished turn's calls stay readable. What does
// not follow is that a *dead* turn's calls are readable: the write is queued per conversation and
// awaited by the completion barrier only when the turn reports success, so a process that leaves
// mid-turn can exit with its last calls uncommitted. So the honest report is per conversation:
// what was committed, and whether the turn as a whole was recorded.
static void ReportInFlightTurn(DurableStore store, int iteration)
{
    // The unfinished turn is the conversation whose prompt was admitted and never settled: the
    // admission is committed before the prompt is sent, so it exists even when nothing else about
    // the turn does.
    var admitted = store.Documents
        .Where(document => document.Kind == "durable.admission")
        .Select(document => new
        {
            document.OwnerId,
            Latest = document.Versions
                .Select(version => version.Value.Deserialize<InputAdmission>(DurableJson.Options))
                .OfType<InputAdmission>()
                .LastOrDefault(),
        })
        .Where(candidate => candidate.Latest is { State: "admitted" })
        .Select(candidate => candidate.OwnerId)
        .FirstOrDefault();

    var checkpoint = admitted is null
        ? null
        : store.Documents.FirstOrDefault(document =>
            document.Kind == "durable.checkpoint" && document.OwnerId == admitted);
    var recorded = checkpoint?.Versions
        .Select(version => version.Value.Deserialize<ConversationCheckpoint>(DurableJson.Options))
        .OfType<ConversationCheckpoint>()
        .ToList() ?? [];
    var calls = recorded.Where(entry => entry.Kind == "tool").ToList();

    if (admitted is null)
    {
        Console.WriteLine($"  no turn was left in flight; iteration {iteration} never started a prompt.");
        return;
    }

    if (!recorded.Any(entry => entry.Kind == "turn"))
    {
        Console.WriteLine($"  iteration {iteration}'s turn is recorded as issued and never finished.");
        Console.WriteLine($"  the store holds {(calls.Count == 0 ? "no" : calls.Count.ToString())} completed tool call(s) for it:");
        foreach (var call in calls)
        {
            var args = call.ToolArgs is { Length: > 0 } raw
                ? " " + Truncate(raw.ReplaceLineEndings(" "), 60)
                : string.Empty;
            Console.WriteLine($"    {call.ToolName ?? "(unknown tool)"}{args} — {(call.Success == false ? "failed" : "ok")}");
        }

        if (calls.Count == 0)
        {
            Console.WriteLine("  the turn's tool writes were queued and not committed: the process left first.");
        }

        Console.WriteLine("  that is the whole of what the store can say: the turn is observable, not replayable.");
        return;
    }

    Console.WriteLine($"  iteration {iteration}'s turn is already recorded as finished; nothing was left in flight.");
}

// ─── Reconcile: verification and plan bookkeeping ───────────────────────────
//
// Each condition restates one line of the target's own contract — the same one
// ../baseline/verify.sh checks. Ticking an item is allowed only when its condition holds on disk
// right now. This is the step that makes a pass which did nothing visible, instead of letting
// the plan claim work that never happened.

static string Reconcile(string target)
{
    var planPath = Path.Combine(target, Job.PlanFile);

    // Normalize the one formatting detail the target's contract leaves open: exactly one blank
    // line after a document's H1. Without it, two runs that both did the job correctly can
    // differ by a blank line, and an idempotency comparison would report a difference that is
    // variance in the model's output rather than a repeated side effect.
    var normalized = NormalizeSectionHeadings(target);
    var plan = File.ReadAllText(planPath);
    var notesPath = Path.Combine(target, "release-notes.md");
    var notes = File.Exists(notesPath) ? File.ReadAllText(notesPath) : string.Empty;

    var specs = new List<(string Key, bool Holds)>
    {
        ("`release-notes.md`: demote every heading", HeadingsDemoted(notes)),
        ("`release-notes.md`: end every bullet under", NewMarkersApplied(notes)),
        ("`docs/section-01.md`", HasH1(target, 1)),
        ("`docs/section-02.md`", HasH1(target, 2)),
        ("`docs/section-03.md`", HasH1(target, 3) && Contains(target, 3, "](./section-01.md)")),
        ("`docs/section-04.md`", HasH1(target, 4)),
        ("`docs/section-05.md`", HasH1(target, 5)),
        ("`docs/section-06.md`", HasH1(target, 6)),
        ("`docs/section-07.md`", HasH1(target, 7)),
        ("`docs/section-08.md`", HasH1(target, 8)),
        ("`docs/section-09.md`", HasH1(target, 9) && Contains(target, 9, "2026-01-01")),
        ("`docs/section-10.md`", HasH1(target, 10)),
    };

    var used = new HashSet<int>();
    var ticked = new List<string>();
    var unticked = new List<string>();
    var lines = plan.Replace("\r\n", "\n").Split('\n');
    for (var i = 0; i < lines.Length; i++)
    {
        var open = lines[i].StartsWith("- [ ]", StringComparison.Ordinal);
        var done = lines[i].StartsWith("- [x]", StringComparison.Ordinal);
        if (!open && !done)
        {
            continue;
        }

        var specIndex = Enumerable.Range(0, specs.Count)
            .FirstOrDefault(index => !used.Contains(index) && lines[i].Contains(specs[index].Key, StringComparison.Ordinal), -1);
        if (specIndex < 0)
        {
            // An item this loop has no condition for is left exactly as it is: the run does not
            // claim it is finished on the strength of a plan line nothing verified.
            continue;
        }

        used.Add(specIndex);
        if (open && specs[specIndex].Holds)
        {
            lines[i] = "- [x]" + lines[i]["- [ ]".Length..];
            ticked.Add($"item-{specIndex + 1:00}");
        }
        else if (done && !specs[specIndex].Holds)
        {
            // A ticked item whose condition does not hold was ticked without the change being on
            // disk. The plan is the input, not the record, so it is corrected rather than
            // trusted.
            lines[i] = "- [ ]" + lines[i]["- [x]".Length..];
            unticked.Add($"item-{specIndex + 1:00}");
        }
    }

    var updated = string.Join('\n', lines);
    var stillOpen = updated.Split('\n').Count(line => line.StartsWith("- [ ]", StringComparison.Ordinal));

    // The status line is the run's completion condition, so the loop owns it: it is set only
    // when no item is open and every condition holds. The model's opinion is not consulted.
    if (stillOpen == 0 && specs.All(spec => spec.Holds))
    {
        updated = updated.Replace("Status: in-progress", "Status: complete", StringComparison.Ordinal);
    }

    if (!string.Equals(updated, plan, StringComparison.Ordinal))
    {
        File.WriteAllText(planPath, updated);
    }

    var detail = new List<string>();
    if (normalized > 0)
    {
        detail.Add($"formatted {normalized} section heading(s)");
    }

    if (ticked.Count > 0)
    {
        detail.Add($"ticked {string.Join(", ", ticked)}");
    }

    if (unticked.Count > 0)
    {
        detail.Add($"unticked {string.Join(", ", unticked)} (condition does not hold on disk)");
    }

    if (detail.Count == 0)
    {
        detail.Add("no plan item changed");
    }

    return stillOpen == 0
        ? $"{string.Join("; ", detail)}; plan has no open item"
        : $"{string.Join("; ", detail)}; {stillOpen} item(s) still open";
}

// Formats every section file as H1, one blank line, body. Returns how many files changed.
// The contract asks for the H1; the blank line is presentation, and pinning it is what makes
// two correct runs byte-identical.
static int NormalizeSectionHeadings(string target)
{
    var changed = 0;
    var directory = Path.Combine(target, "docs");
    if (!Directory.Exists(directory))
    {
        return 0;
    }

    foreach (var path in Directory.EnumerateFiles(directory, "section-*.md").Order(StringComparer.Ordinal))
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var lines = text.Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("# ", StringComparison.Ordinal))
        {
            continue;
        }

        var body = lines.Skip(1).ToList();
        while (body.Count > 0 && body[0].Trim().Length == 0)
        {
            body.RemoveAt(0);
        }

        var formatted = body.Count == 0
            ? lines[0] + "\n"
            : lines[0] + "\n\n" + string.Join('\n', body);
        if (formatted != text)
        {
            File.WriteAllText(path, formatted);
            changed++;
        }
    }

    return changed;
}

static bool HeadingsDemoted(string notes)
{
    string[] expected =
    [
        "# Release notes",
        "### Overview",
        "### New",
        "### Fixes",
        "#### Panic on empty plan",
        "#### Timeout drift",
        "##### Retries",
        "### Upgrade notes",
    ];

    var lines = notes.Replace("\r\n", "\n").Split('\n');
    if (expected.Any(heading => !lines.Any(line => line.TrimEnd() == heading)))
    {
        return false;
    }

    if (!lines.Any(line => line == "## This heading is inside a fence and must not change."))
    {
        return false;
    }

    var fence = false;
    foreach (var line in lines)
    {
        if (line.StartsWith("```", StringComparison.Ordinal))
        {
            fence = !fence;
            continue;
        }

        if (!fence && line.StartsWith("## ", StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static bool NewMarkersApplied(string notes)
{
    var bullets = 0;
    var inNew = false;
    foreach (var line in notes.Replace("\r\n", "\n").Split('\n'))
    {
        if (line.StartsWith("### New", StringComparison.Ordinal))
        {
            inNew = true;
            continue;
        }

        if (line.StartsWith("#", StringComparison.Ordinal))
        {
            inNew = false;
            continue;
        }

        if (!inNew || !line.StartsWith("- ", StringComparison.Ordinal))
        {
            continue;
        }

        bullets++;
        if (CountOccurrences(line, "<!-- NEW -->") != 1
            || !line.EndsWith(" <!-- NEW -->", StringComparison.Ordinal))
        {
            return false;
        }
    }

    return bullets > 0;
}

static int CountOccurrences(string text, string needle)
{
    var count = 0;
    var at = 0;
    while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
    {
        count++;
        at += needle.Length;
    }

    return count;
}

static bool HasH1(string target, int section)
{
    var path = SectionPath(target, section);
    if (!File.Exists(path))
    {
        return false;
    }

    var first = File.ReadLines(path).FirstOrDefault() ?? string.Empty;
    return first.StartsWith("# ", StringComparison.Ordinal);
}

static bool Contains(string target, int section, string needle) =>
    File.Exists(SectionPath(target, section))
    && File.ReadAllText(SectionPath(target, section)).Contains(needle, StringComparison.Ordinal);

static string SectionPath(string target, int section) =>
    Path.Combine(target, "docs", $"section-{section:00}.md");

// ─── Helpers ────────────────────────────────────────────────────────────────

static string StepKey(int iteration, string step) => $"step/{iteration}/{step}";

static string Digest(string text) =>
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

static bool StopBeforeMemo() =>
    Environment.GetEnvironmentVariable("STOP_BEFORE_MEMO") == "1";

// The complementary seam: the step memo exists, the iteration is not settled. It is what puts a
// resume on the memo-hit branch.
static bool StopAfterMemo() =>
    Environment.GetEnvironmentVariable("STOP_AFTER_MEMO") == "1";

// The in-flight seam: leave while the iteration's turn is running, after this many tool calls have
// completed. The completed calls are in the store; the one in flight is not.
static bool StopMidTurn() =>
    Environment.GetEnvironmentVariable("STOP_MID_TURN") == "1";

static int StopAfterIteration() =>
    Environment.GetEnvironmentVariable("STOP_AFTER_ITERATION") is { Length: > 0 } text
    && int.TryParse(text, out var value) && value > 0
        ? value
        : 0;

static bool IsComplete(string plan) =>
    plan.Contains("Status: complete", StringComparison.OrdinalIgnoreCase);

static IReadOnlyList<string> OpenItems(string plan) =>
    plan.Replace("\r\n", "\n").Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("- [ ]", StringComparison.Ordinal))
        .ToList();

static string? FirstOpenItem(string plan) => OpenItems(plan).FirstOrDefault();

static string Truncate(string text, int max) =>
    text.Length <= max ? text : text[..max] + "…";

// ─── Constants and the shapes written to the store ──────────────────────────

static class Job
{
    public const string TaskName = "band-b.ralph-loop";
    public const string PlanFile = "PLAN.md";
    public const string PromptFile = "PROMPT.md";
    public const string StoreDirectory = ".durable";
    public const int DefaultMaxIterations = 10;

    // Two open plan items per pass. The baseline asks for "as many as you can in this pass",
    // which makes an iteration's size a property of the model's mood; a resumed run could then
    // not be compared with an uninterrupted one. A bounded pass makes one iteration a
    // reproducible unit.
    public const int ItemsPerPass = 2;

    // The model `upstream/ralph-loop.ts` pins, and the model the baseline pins.
    public const string PinnedModel = "gpt-5.1-codex-mini";

    // How many completed tool calls the in-flight seam leaves behind before stopping the turn.
    public const int MidTurnCompleted = 1;
}

sealed record LoopInput
{
    public string Target { get; init; } = ".";
    public string PlanFile { get; init; } = "PLAN.md";
    public string PromptFile { get; init; } = "PROMPT.md";
    public int MaxIterations { get; init; }
    public int ItemsPerPass { get; init; } = 2;
}

sealed record LoopCheckpoint
{
    public string Phase { get; init; } = "iteration";
    public int Iteration { get; init; }
    public string Stage { get; init; } = "ready";
    public string? PlanDigest { get; init; }
    public List<StepOutcome> Steps { get; init; } = [];
}

sealed record StepOutcome(string Step, string Outcome);

sealed record LoopResult(string Reason, int Iterations, string? FirstOpenItem);
