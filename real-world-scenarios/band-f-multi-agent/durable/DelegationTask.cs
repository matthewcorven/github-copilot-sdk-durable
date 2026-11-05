using Durable;
using GitHub.Copilot;

namespace BandF.Durable;

/// <summary>
/// The durable task: one checkpointed state machine whose checkpoint is the delegation's control
/// state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every phase commits before it acts.</b> <c>route</c> commits the routing decision and the
/// checkpoint that names the specialist as <c>Holder</c> in one transaction, and only then does
/// anything else happen. A process killed immediately afterwards leaves a store that says
/// "return_agent holds turn 1", which is what the next process needs to continue. There is no window
/// in which control has moved and the store does not know.
/// </para>
/// <para>
/// <b>A resumed agent produces; it is not asked what to do.</b> When <c>specialist</c> runs, the
/// checkpoint already names the holder and the request. The specialist is not re-routed and decides
/// nothing: it is handed its own prior output (read from the store, not from a transcript) and asked
/// to finish the work it was already given. That is the difference between forwarding a task and
/// starting the conversation over.
/// </para>
/// <para>
/// <b>The output and the fact that it exists commit together.</b> One callback writes the
/// <see cref="SpecialistOutput"/> document, the control record and the checkpoint that moves the run
/// to <c>settle</c>. Either all three are in the store or none is.
/// </para>
/// <para>
/// <b>How the evidence kills it.</b> There is no seam in this file. The run asked to stop is killed
/// with <c>SIGKILL</c> from <c>evidence.sh</c>, at the moment the process itself prints that a commit
/// has landed: the delegation's commit is durable, the process is gone, and nothing unwinds. A clean
/// exit would let the layer write something on the way out; a kill leaves exactly what the commits
/// left. The evidence file names the pid and the signal, so the claim is checkable.
/// </para>
/// </remarks>
internal sealed class DelegationTask
{
    private readonly DurableHarness _harness;
    private readonly Router _router;
    private readonly SpecialistRunner _specialist;
    private readonly ConsoleLog _log;
    private readonly DelegationInput _input;

    public DelegationTask(
        DurableHarness harness,
        Router router,
        SpecialistRunner specialist,
        ConsoleLog log,
        DelegationInput input)
    {
        _harness = harness;
        _router = router;
        _specialist = specialist;
        _log = log;
        _input = input;
    }

    /// <summary>Builds the definition the registry installs.</summary>
    public DurableTaskDefinition Definition() =>
        DurableTask.DefinePhases<DelegationInput, DelegationCheckpoint>(
            Names.Task,
            () => new DelegationCheckpoint(),
            new Dictionary<string, Func<DelegationInput, DelegationCheckpoint, TaskRuntime, Task<TaskCommit>>>
            {
                [Phases.Route] = RouteAsync,
                [Phases.Specialist] = SpecialistAsync,
                [Phases.Settle] = SettleAsync,
            });

    // ── route ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The coordinator's phase: decide, then commit. The commit <i>is</i> the delegation, so nothing
    /// about the specialist's work happens before it lands.
    /// </summary>
    private async Task<TaskCommit> RouteAsync(
        DelegationInput input,
        DelegationCheckpoint checkpoint,
        TaskRuntime runtime)
    {
        var turn = checkpoint.Turn;
        if (turn >= input.Turns.Count)
        {
            return await CompleteAsync(runtime, input, "all-turns-served");
        }

        var message = input.Turns[turn];

        // The memo. The decision the first attempt committed is the decision, and a later attempt
        // never asks again: the model is not deterministic, and a second answer could name a
        // specialist the run has already produced work for. BAND_F_FORCE_REROUTE=1 skips the read and
        // exists only as the evidence's before/after control — it is how the failure the memo prevents
        // is shown happening rather than described.
        var stored = Environment.GetEnvironmentVariable("BAND_F_FORCE_REROUTE") is "1"
            ? null
            : new RunView(_harness.Store).Document(Docs.Routing, turn);

        RoutingDecision decision;
        if (stored is not null)
        {
            decision = stored;
            _log.Line($"[control] routing: turn {turn} was already routed to {decision.Specialist} "
                + $"({decision.Source}) — reusing the committed decision, not asking again");
        }
        else
        {
            _log.Step($"=== turn {turn} — {Agents.Coordinator} holds control ===");
            _log.Line($"- User: {message}");
            decision = await _router.DecideAsync(turn, message, runtime.CancellationToken);
            _log.Line($"[control] {Agents.Coordinator} routes turn {turn} to {decision.Specialist} ({decision.Source})");
        }

        // The routing decision is its own commit, and it is the memo. It lands before the
        // delegation, so a process killed between the two leaves a store that says "turn 1 goes to
        // refund_agent" without yet saying refund_agent holds it — and the next process reads that
        // rather than asking a model that could answer differently.
        var routingEntry = new RunView(_harness.Store).Store
            .Document(Docs.Routing.Kind, RunView.OwnerFor(turn))?.Versions.LastOrDefault()?.EntryId;

        if (stored is null)
        {
            routingEntry = string.Empty;
            await runtime.CommitAsync(tx =>
            {
                var owner = RunView.OwnerFor(turn);
                var entry = tx.AppendEntry(owner, "routing", decision.Specialist);

                var routing = tx.Doc(Docs.Routing, owner);
                routing.Value = decision;
                routing.Save(entry);

                var user = tx.Doc(Docs.UserTurn, owner);
                user.Value = new UserTurnRecord { Turn = turn, Message = message };
                user.Save(entry);

                routingEntry = entry;
                return Task.FromResult(TaskCommit.Running(DurableJson.ToElement(checkpoint)));
            });

            _log.Line($"[control] routing decision for turn {turn} committed at {routingEntry}: "
                + $"{decision.Specialist} ({decision.Source})");

            if (await ShouldKillAsync(runtime, "routing", turn))
            {
                KillPoint($"turn {turn}'s routing decision is committed at {routingEntry}; the delegation "
                    + "that would move control has not happened");
            }
        }

        var entry = string.Empty;

        // The delegation. The checkpoint names the specialist: the delegation is the control
        // transfer, and the store must answer "who holds this turn?" without the specialist having
        // run. When the run's input says so, this is the last thing the process does.
        var delegated = await runtime.CommitAsync(tx =>
        {
            var owner = RunView.OwnerFor(turn);

            entry = tx.AppendEntry(owner, "delegation", decision.Specialist);
            WriteWire(tx, owner, new WireRecord
            {
                Turn = turn,
                Event = stored is null ? "delegated" : "delegated-from-memo",
                Agent = decision.Specialist,
                Request = decision.Request,
                Detail = decision.Model,
                RoutingEntry = routingEntry,
                EntryId = entry,
            });

            return Task.FromResult(TaskCommit.Running(DurableJson.ToElement(new DelegationCheckpoint
            {
                Phase = Phases.Specialist,
                Holder = decision.Specialist,
                Turn = turn,
                Request = decision.Request,
                DelegationEntry = entry,
                Attempts = 0,
            })));
        });

        _log.Line($"[control] delegation committed at {entry}: {Agents.Coordinator} -> {decision.Specialist} "
            + $"holds turn {turn} (routing decision {routingEntry})");

        if (await ShouldKillAsync(runtime, "delegating", turn))
        {
            KillPoint($"turn {turn}'s delegation is committed; {decision.Specialist} holds control "
                + "and has produced nothing");
        }

        return delegated;
    }

    // ── specialist ──────────────────────────────────────────────────────────

    /// <summary>
    /// The specialist's phase. The checkpoint already says who holds the conversation and what they
    /// were asked; this phase produces, or reuses what an earlier attempt produced.
    /// </summary>
    private async Task<TaskCommit> SpecialistAsync(
        DelegationInput input,
        DelegationCheckpoint checkpoint,
        TaskRuntime runtime)
    {
        var turn = checkpoint.Turn;
        var holder = checkpoint.Holder;

        if (!Agents.IsSpecialist(holder))
        {
            return TaskCommit.Failed(
                $"turn {turn} entered the specialist phase with {holder} in control; only a specialist may "
                + "hold the conversation in this phase");
        }

        _log.Step($"=== turn {turn} — {holder} holds control ===");
        _log.Line($"[control] {holder} holds control (delegated at {checkpoint.DelegationEntry}, "
            + $"attempt {checkpoint.Attempts + 1})");
        if (checkpoint.Attempts > 0)
        {
            _log.Line($"[control] this is a resumed attempt: {holder} was named by the checkpoint, not by a "
                + "fresh routing decision");
        }

        if (new RunView(_harness.Store).Document(Docs.Output, turn) is { } previous)
        {
            _log.Line($"[control] {holder} already produced this turn's output "
                + $"({previous.ModelTurns} model turn(s), {previous.ProducedBy}); not running it again");

            return await runtime.CommitAsync(tx =>
            {
                var owner = RunView.OwnerFor(turn);
                var entry = tx.AppendEntry(owner, "reuse", holder);
                WriteWire(tx, owner, new WireRecord
                {
                    Turn = turn,
                    Event = "reused-output",
                    Agent = holder,
                    Request = checkpoint.Request,
                    Detail = "output already committed",
                    EntryId = entry,
                });

                return Task.FromResult(TaskCommit.Running(DurableJson.ToElement(checkpoint with
                {
                    Phase = Phases.Settle,
                })));
            });
        }

        // Committed before the model call: a process killed inside the turn leaves a store saying
        // this attempt was entered, which a resume reports rather than guesses at.
        await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(checkpoint with
        {
            Phase = Phases.Specialist,
            Attempts = checkpoint.Attempts + 1,
        })));

        if (await ShouldKillAsync(runtime, "in-turn", turn))
        {
            KillPoint($"turn {turn}'s specialist turn has been entered (attempt {checkpoint.Attempts + 1}) and the "
                + "model has not been asked; the store records the attempt and nothing it produced");
        }

        var produced = await _specialist.ProduceAsync(turn, holder, checkpoint.Request!, runtime.CancellationToken);

        var advanced = await runtime.CommitAsync(tx =>
        {
            var owner = RunView.OwnerFor(turn);

            // The output and the record that says it exists share one entry: a reader cannot find
            // one without the other, because one commit wrote both.
            var entry = tx.AppendEntry(owner, "produced", holder);

            var document = tx.Doc(Docs.Output, owner);
            document.Value = produced;
            document.Save(entry);

            WriteWire(tx, owner, new WireRecord
            {
                Turn = turn,
                Event = "produced",
                Agent = holder,
                Request = checkpoint.Request,
                Detail = produced.Tool,
                EntryId = entry,
            });

            // The holder does not change here: the specialist still holds the turn until it settles.
            return Task.FromResult(TaskCommit.Running(DurableJson.ToElement(checkpoint with
            {
                Phase = Phases.Settle,
            })));
        });

        _log.Line($"[control] {holder}'s output for turn {turn} committed");

        if (await ShouldKillAsync(runtime, "producing", turn))
        {
            KillPoint($"turn {turn}'s specialist output is committed; the turn is not settled");
        }

        return advanced;
    }

    // ── settle ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Closes the turn: the coordinator takes control of the next one, or the run finishes. The
    /// holder change and the terminal outcome are the same commit.
    /// </summary>
    private async Task<TaskCommit> SettleAsync(
        DelegationInput input,
        DelegationCheckpoint checkpoint,
        TaskRuntime runtime)
    {
        var turn = checkpoint.Turn;
        var final = turn + 1 >= input.Turns.Count;

        return await runtime.CommitAsync(tx =>
        {
            var owner = RunView.OwnerFor(turn);
            var entry = tx.AppendEntry(owner, "settled", checkpoint.Holder);
            WriteWire(tx, owner, new WireRecord
            {
                Turn = turn,
                Event = "settled",
                Agent = checkpoint.Holder,
                Request = checkpoint.Request,
                Detail = final ? "run complete" : $"next turn {turn + 1}",
                EntryId = entry,
            });

            if (final)
            {
                return Task.FromResult(TaskCommit.Completed(DurableJson.ToElement(new DelegationResult
                {
                    Reason = "all-turns-served",
                    Turns = input.Turns.Count,
                    Holders = new RunView(_harness.Store).Wire()
                        .Where(revision => revision.Record.Event == "delegated")
                        .Select(revision => revision.Record.Agent)
                        .ToList(),
                })));
            }

            return Task.FromResult(TaskCommit.Running(DurableJson.ToElement(new DelegationCheckpoint
            {
                Phase = Phases.Route,
                Holder = Agents.Coordinator,
                Turn = turn + 1,
                Request = null,
                DelegationEntry = null,
                Attempts = 0,
            })));
        });
    }

    // ── the kill point ──────────────────────────────────────────────────────

    /// <summary>
    /// Whether this process should stop at a seam, and the record that makes it a one-shot.
    /// </summary>
    /// <remarks>
    /// A seam belongs to the process that was asked to stop at it. Once it has served (this process
    /// is about to be killed), the run records that with a memo keyed by the seam and the turn, so a
    /// later process cannot be made to stop at the same place again. Without this, the evidence's
    /// control — which resumes the same killed store deliberately — would stop at the same seam
    /// forever and could never show the difference it exists to show.
    /// <para>
    /// The memo is written before the process announces itself, so a process killed between the memo
    /// and the announcement still leaves the seam marked as served. That ordering matters: a seam
    /// that could fire twice would make a resume look like a fresh run.
    /// </para>
    /// </remarks>
    private async Task<bool> ShouldKillAsync(TaskRuntime runtime, string seam, int turn)
    {
        var requested = seam switch
        {
            "routing" => _input.KillAfterRouting.Contains(turn),
            "delegating" => _input.KillAfterDelegating.Contains(turn),
            "producing" => _input.KillAfterProducing.Contains(turn),
            "in-turn" => _input.KillInTurn.Contains(turn),
            _ => false,
        };

        if (!requested)
        {
            return false;
        }

        var key = $"seam/{seam}/{turn}";
        if (await runtime.MemoAsync(key) is not null)
        {
            return false;
        }

        await runtime.MemoAsync(key, DurableJson.ToElement(new { seam, turn, pid = Environment.ProcessId }));
        return true;
    }

    /// <summary>
    /// Announces that a commit has landed and then waits, doing nothing, until the harness kills this
    /// process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line is written after the commit returned, so the condition the evidence kills on is "the
    /// store has it", not "the program intends to write it". Waiting rather than exiting is the whole
    /// point: a process that exited here would have flushed whatever it had, and the evidence could
    /// not distinguish "the commit survived" from "the shutdown wrote it". A <c>SIGKILL</c> lets
    /// nothing run, so what is in the file afterwards is what the commits put there.
    /// </para>
    /// <para>
    /// The wait is unbounded because the kill is what ends it; a process that was never killed hangs,
    /// visibly, rather than quietly finishing the run and making the evidence a lie. <c>evidence.sh</c>
    /// kills by pid after reading the line, and fails the stage if the line never appears.
    /// </para>
    /// </remarks>
    private void KillPoint(string boundary)
    {
        _log.Line($"[seam] ready-to-kill pid={Environment.ProcessId} — {boundary}");
        _log.Line("[seam] the store holds the commits above; this process will not exit on its own");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }

    private static Task<TaskCommit> CompleteAsync(TaskRuntime runtime, DelegationInput input, string reason) =>
        runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(new DelegationResult
        {
            Reason = reason,
            Turns = input.Turns.Count,
            Holders = [],
        })));

    private static void WriteWire(Transaction tx, string owner, WireRecord record)
    {
        // Anchored to its own entry id, so a reader can tell two records written by one commit from
        // two records written by adjacent commits.
        var draft = tx.Doc(Docs.Wire, owner);
        draft.Value = record;
        draft.Save(record.EntryId);
    }
}

/// <summary>Writes the run's human-readable trace. Stdout only; it holds no state.</summary>
internal sealed class ConsoleLog
{
    public void Step(string text)
    {
        Console.WriteLine();
        Console.WriteLine(text);
    }

    public void Line(string text) => Console.WriteLine(text);
}
