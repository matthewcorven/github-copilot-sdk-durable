using System.Text.Json;
using Durable;

namespace BandGDurable;

/// <summary>
/// The approval gate: a task that waits for an external decision with a durable deadline.
/// </summary>
/// <remarks>
/// The gate is a task of its own, a child of the workflow, for one reason: a task that is
/// <see cref="TaskStatus.Waiting"/> holds no invocation. The workflow commits that it is waiting on
/// this child and returns; the process can end there, and a later process re-enters both from their
/// checkpoints. Nothing blocks, nothing polls, and no thread is held for the length of the wait.
/// <para>
/// The deadline is in the gate's own checkpoint, not re-derived from the clock at wake time. A gate
/// created with a ten-minute window that is restarted after five minutes still has five minutes
/// left, and one restarted after eleven minutes wakes immediately and records a timeout. That is
/// what "durable deadline" has to mean, or a restart silently extends the window.
/// </para>
/// <para>
/// The gate reads the decision; it does not write it. The only writer is
/// <see cref="GateCommands"/>, through a document whose first writer wins. A gate that woke on a
/// stale timer cannot overwrite a decision that landed first, because it never writes one.
/// </para>
/// </remarks>
public static class ApprovalGate
{
    /// <summary>The gate's definition.</summary>
    /// <param name="store">
    /// The store the gate reads its decision document from. The runtime hands a phase only its own
    /// task, so the store a document is read through has to come in from outside the definition.
    /// </param>
    public static DurableTaskDefinition Define(DurableStore store) =>
        DurableTask.DefinePhases<GateInput, GateCheckpoint>(
            Brief.GateTaskName,
            () => new GateCheckpoint { Phase = "open" },
            new Dictionary<string, Func<GateInput, GateCheckpoint, TaskRuntime, Task<TaskCommit>>>
            {
                // Opening records the deadline once. It is the one fact a restart must not
                // recompute, so it is committed before anything can fail.
                ["open"] = (input, checkpoint, runtime) => runtime.CommitAsync(() =>
                {
                    var deadline = checkpoint.Deadline ?? runtime.Now.AddSeconds(input.WindowSeconds);
                    return TaskCommit.Running(DurableJson.ToElement(new GateCheckpoint
                    {
                        Phase = "wait",
                        Deadline = deadline,
                        Owner = input.Owner,
                    }));
                }),

                // The wait. A decision already on file means there is nothing to wait for, which is
                // the case a duplicate approval arrives into. Otherwise the gate sleeps to the
                // deadline the record holds.
                ["wait"] = (input, checkpoint, runtime) =>
                {
                    var decision = Brief.ReadDecision(store, input.Owner);
                    if (decision is not null)
                    {
                        return runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(
                            new GateOutcome { Verdict = decision.Approved ? "approved" : "rejected", Decision = decision })));
                    }

                    var deadline = checkpoint.Deadline ?? runtime.Now.AddSeconds(input.WindowSeconds);

                    // SleepUntilAsync commits the next checkpoint when the deadline arrives. The
                    // commit is what the store sees, and it is the commit a terminal gate refuses.
                    return runtime.SleepUntilAsync(deadline, () =>
                    {
                        // Re-read at the deadline rather than trusting the read that started the
                        // wait: a decision written while this process slept is the one to honour.
                        var settled = Brief.ReadDecision(store, input.Owner) ?? new ApprovalDecision
                        {
                            BriefId = input.Owner,
                            Approved = false,
                            Comment = "no decision before the gate's deadline",
                            Recorder = "timeout",
                            At = runtime.Now,
                        };

                        return TaskCommit.Completed(DurableJson.ToElement(new GateOutcome
                        {
                            Verdict = settled.Recorder == "timeout" ? "timed-out"
                                : settled.Approved ? "approved" : "rejected",
                            Decision = settled,
                        }));
                    });
                },
            },
            // An aborted gate records that it was cancelled rather than a verdict it never reached.
            (input, _, runtime) => runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(
                new GateOutcome
                {
                    Verdict = "aborted",
                    Decision = new ApprovalDecision
                    {
                        BriefId = input.Owner,
                        Recorder = "abort",
                        Comment = "the workflow was aborted while the gate was open",
                        At = runtime.Now,
                    },
                }))));
}

/// <summary>What a gate is opened with.</summary>
public sealed class GateInput
{
    /// <summary>The brief whose decision document the gate reads.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>How long the gate stays open, in seconds, counted from the open commit.</summary>
    public int WindowSeconds { get; set; } = Brief.DefaultGateSeconds;
}

/// <summary>The gate's position.</summary>
public sealed class GateCheckpoint
{
    /// <summary>The phase to enter next: <c>open</c> or <c>wait</c>.</summary>
    public string Phase { get; set; } = "open";

    /// <summary>The deadline, recorded once at open and never recomputed.</summary>
    public DateTimeOffset? Deadline { get; set; }

    /// <summary>The brief this gate belongs to.</summary>
    public string? Owner { get; set; }
}

/// <summary>What the gate concluded.</summary>
public sealed class GateOutcome
{
    /// <summary>One of <c>approved</c>, <c>rejected</c>, <c>timed-out</c>, or <c>aborted</c>.</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>The decision the gate read, or the timeout record it wrote in its place.</summary>
    public ApprovalDecision? Decision { get; set; }
}

/// <summary>
/// The writes a host may make against a gate: an approval, a rejection, or a wake. Every one of
/// them is idempotent by construction, because every one of them is a read of the decision document
/// first.
/// </summary>
/// <remarks>
/// This is where "a decision recorded after the gate cannot be overwritten" is enforced, and it is
/// enforced in one place rather than in each caller. The decision is a durable document; the first
/// write wins because a second write finds the first and returns it. A duplicate approval, a late
/// approval after a timeout, and an approval for a different brief all take the same path and all
/// end at the same answer: the decision on file does not change.
/// <para>
/// A write to a terminal or missing task is refused before the document is read, so a late caller
/// cannot record a decision into a run that has already finished.
/// </para>
/// </remarks>
public static class GateCommands
{
    /// <summary>Records an approval, unless a decision is already on file.</summary>
    public static Task<GateWriteResult> ApproveAsync(
        DurableStore store,
        string briefId,
        string? workTaskId,
        string recorder,
        string comment) =>
        RecordAsync(store, briefId, workTaskId, recorder, approved: true, comment);

    /// <summary>Records a rejection, unless a decision is already on file.</summary>
    public static Task<GateWriteResult> RejectAsync(
        DurableStore store,
        string briefId,
        string? workTaskId,
        string recorder,
        string comment) =>
        RecordAsync(store, briefId, workTaskId, recorder, approved: false, comment);

    /// <summary>
    /// What a gate write did: whether it recorded the decision or found one already there.
    /// </summary>
    /// <param name="Recorded">Whether this caller's value reached the document.</param>
    /// <param name="Decision">The decision on file after the call, which is the caller's only when <paramref name="Recorded"/>.</param>
    /// <param name="Note">The sentence explaining which happened.</param>
    public sealed record GateWriteResult(bool Recorded, ApprovalDecision Decision, string Note);

    private static async Task<GateWriteResult> RecordAsync(
        DurableStore store,
        string briefId,
        string? workTaskId,
        string recorder,
        bool approved,
        string comment)
    {
        // A decision is only accepted for a run that can still use it. The workflow's own record is
        // the authority: a missing task is a wrong id, and a terminal one is a run that has already
        // decided what it is doing.
        var work = workTaskId is null ? store.Tasks.FirstOrDefault(task => task.Name == Brief.TaskName) : store.Task(workTaskId);
        if (work is null)
        {
            var orphan = new ApprovalDecision { BriefId = briefId, Approved = approved, Comment = comment, Recorder = recorder, At = DateTimeOffset.UtcNow };
            return new GateWriteResult(false, orphan, "refused: no workflow task to decide");
        }

        if (work.Status == Durable.TaskStatus.Terminal)
        {
            var settled = Brief.ReadDecision(store, work.Id) ?? new ApprovalDecision
            {
                BriefId = briefId,
                Approved = false,
                Comment = "the run finished before this decision arrived",
                Recorder = "finished",
                At = DateTimeOffset.UtcNow,
            };

            return new GateWriteResult(false, settled, $"refused: the workflow is {work.Status}, so this decision is late");
        }

        if (!string.Equals(briefId, work.Id, StringComparison.Ordinal))
        {
            var other = Brief.ReadDecision(store, work.Id) ?? new ApprovalDecision
            {
                BriefId = work.Id,
                Approved = false,
                Comment = "no decision yet",
                Recorder = "pending",
                At = DateTimeOffset.UtcNow,
            };

            return new GateWriteResult(false, other, $"refused: this decision names '{briefId}', and the run is '{work.Id}'");
        }

        GateWriteResult? result = null;
        await store.CommitAsync(tx =>
        {
            var draft = tx.Doc(Brief.DecisionDefinition, work.Id);

            // The first write wins. Everything after it reads what is already on file and records
            // that it did not change it, so the caller is told which of the two happened.
            var onFile = draft.Value.Recorder.Length == 0 ? null : draft.Value;
            if (onFile is not null)
            {
                result = new GateWriteResult(
                    false,
                    onFile,
                    $"ignored: a decision is already on file ({onFile.Recorder}, {(onFile.Approved ? "approved" : "rejected")})");
                return Task.CompletedTask;
            }

            var decision = new ApprovalDecision
            {
                BriefId = briefId,
                Approved = approved,
                Comment = comment,
                Recorder = recorder,
                At = DateTimeOffset.UtcNow,
            };

            draft.Value = decision;
            draft.Save(tx.AppendEntry(work.Id, "approval"));
            result = new GateWriteResult(true, decision, $"recorded: {recorder} {(approved ? "approved" : "rejected")} the plan");
            return Task.CompletedTask;
        });

        return result!;
    }
}
