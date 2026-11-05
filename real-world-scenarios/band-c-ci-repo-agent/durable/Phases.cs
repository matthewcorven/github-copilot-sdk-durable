using System.Globalization;
using System.Text.Json;
using Durable;

namespace BandC.Durable;

/// <summary>
/// The eight phases, one durable handler per phase.
/// </summary>
/// <remarks>
/// <para>
/// This file changes nothing about what a phase decides — it calls <see cref="Triage"/> for that,
/// with the same fixture, the same rules and the same vocabulary. It changes when a decision becomes
/// a fact: each handler writes its decision to the store before it returns, and the run record it
/// commits names the phase the next attempt should enter.
/// </para>
/// <para>
/// The baseline runs all eight in one pass over local variables. Here the only thing that crosses a
/// phase boundary is what was committed, so a process that stops after Phase 4 has a run record at
/// <c>recommend</c> and 13 documents whose stage is <c>recommend</c> — and the next process enters at
/// Phase 5 and reads them.
/// </para>
/// </remarks>
public static class TriagePhases
{
    /// <summary>
    /// Builds the task definition for one process.
    /// </summary>
    /// <param name="store">The store the phase handlers write through.</param>
    /// <param name="seam">Where this process should stop, or null to run to the end.</param>
    /// <remarks>
    /// A definition per process rather than a static field, because a handler closes over the store
    /// and the seam. The layer's <see cref="TaskRuntime"/> gives a handler its own task and its own
    /// commit, but no store and no way to read another document — README §"What the layer could and
    /// could not express" records that, because every host that writes more than its own checkpoint
    /// will meet it.
    /// </remarks>
    public static DurableTaskDefinition Build(DurableStore store, ExitSeam? seam) =>
        DurableTask.DefinePhases<RunInput, RunState>(
            Store.TaskName,
            () => new RunState { Phase = Phases.Collect },
            new Dictionary<string, Func<RunInput, RunState, TaskRuntime, Task<TaskCommit>>>
            {
                [Phases.Collect] = (input, state, runtime) => CollectAsync(store, seam, input, state, runtime),
                [Phases.Classify] = (input, state, runtime) => ClassifyAsync(store, seam, input, state, runtime),
                [Phases.Score] = (input, state, runtime) => ScoreAsync(store, seam, input, state, runtime),
                [Phases.Recommend] = (input, state, runtime) => RecommendAsync(store, seam, input, state, runtime),
                [Phases.Batch] = (input, state, runtime) => BatchAsync(store, seam, input, state, runtime),
                [Phases.Comment] = (input, state, runtime) => CommentAsync(store, seam, input, state, runtime),
                [Phases.Report] = (input, state, runtime) => ReportAsync(store, seam, input, state, runtime),
                [Phases.Save] = (input, state, runtime) => SaveAsync(store, seam, input, state, runtime),
            });

    // ─── Phase 1: load state and collect pull requests ────────────────────────────────────────

    /// <summary>
    /// Reads the fixture and the run record, and splits the fixture into eligible and excluded.
    /// </summary>
    /// <remarks>
    /// The baseline's run gate is absent, deliberately. Its state file could not say "a run was
    /// interrupted", so a second run inside the cooldown had to refuse. Here the record says whether
    /// the last attempt finished: a finished record means a new generation, which re-reads the same
    /// state the baseline's gate read and then, because every PR's digest is unchanged, posts
    /// nothing. A run that did not finish is resumed instead.
    /// </remarks>
    private static async Task<TaskCommit> CollectAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        Console.WriteLine(Phases.Header(Phases.Collect));

        var fixture = Fixture.Load(input.FixturePath);
        var prior = TriageStore.Run(store, TriageStore.RunId(fixture.Repository));
        var resuming = state.Attempts > 0;

        if (!resuming)
        {
            Console.WriteLine("  state:      no unfinished run record for this repository — a new generation");
            if (prior is not null)
            {
                Console.WriteLine($"  prior run:  {prior.StartedAt} triaged {prior.Generation} generation(s) of state");
            }
        }
        else
        {
            Console.WriteLine($"  state:      resuming the run record at phase '{state.Phase}' "
                + $"(attempt {state.Attempts + 1}, {state.Commented.Count} comment(s) on record)");
        }

        var eligible = new List<PullRequest>();
        var notOurs = new List<PullRequest>();
        var notOpen = new List<PullRequest>();
        var inCooldown = new List<(PullRequest Pr, int Minutes)>();

        foreach (var pr in fixture.PullRequests)
        {
            if (!string.Equals(pr.Author, Triage.AgentAuthor, StringComparison.Ordinal))
            {
                notOurs.Add(pr);
            }
            else if (!string.Equals(pr.State, "open", StringComparison.Ordinal))
            {
                notOpen.Add(pr);
            }
            else if (Math.Min(pr.OpenedMinutesAgo, pr.UpdatedMinutesAgo) is var youngest
                     && youngest < Triage.CooldownMinutes)
            {
                inCooldown.Add((pr, youngest));
            }
            else
            {
                eligible.Add(pr);
            }
        }

        Console.WriteLine($"  collected:  {fixture.PullRequests.Count} pull requests"
            + " (pull_request_read get / get_files / get_reviews / get_check_runs)");
        foreach (var pr in notOurs)
        {
            Console.WriteLine($"  not ours:   #{pr.Number} authored by {pr.Author} — not {Triage.AgentAuthor}");
        }

        foreach (var pr in notOpen)
        {
            Console.WriteLine($"  not open:   #{pr.Number} state={pr.State}");
        }

        foreach (var (pr, minutes) in inCooldown)
        {
            Console.WriteLine($"  cooldown:   #{pr.Number} "
                + $"{(pr.OpenedMinutesAgo < pr.UpdatedMinutesAgo ? "opened" : "updated")} "
                + $"{minutes}m ago (< {Triage.CooldownMinutes}m) — {Triage.CooldownNote}");
        }

        Console.WriteLine($"  eligible:   {eligible.Count} pull request(s)");
        Console.WriteLine();

        var next = state with
        {
            Phase = Phases.Classify,
            Repository = fixture.Repository,
            StartedAt = resuming ? state.StartedAt : input.BaseTime,
            BaseTime = input.BaseTime,
            Attempts = state.Attempts + 1,
            Generation = resuming ? state.Generation : state.Generation + 1,
            ResumedFrom = resuming ? state.Phase : null,
            Excluded =
            [
                .. inCooldown.Select(pair => pair.Pr.Number),
                .. notOurs.Select(pr => pr.Number),
                .. notOpen.Select(pr => pr.Number),
            ],
        };

        return await StepAsync(store, seam, input, next, runtime);
    }

    // ─── Phase 2: classify and assess risk ───────────────────────────────────────────────────

    /// <summary>
    /// Assesses every eligible PR, reusing what the store already holds at the current digest.
    /// </summary>
    /// <remarks>
    /// The reuse is per PR, not per run: a run whose only change is one edited title re-classifies
    /// that PR and reads the other twelve back. The baseline re-decides all thirteen every time, so
    /// this is the first of its behaviours that changes, and the output says so per PR rather than
    /// claiming a number.
    /// </remarks>
    private static async Task<TaskCommit> ClassifyAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        Console.WriteLine(Phases.Header(Phases.Classify));

        var fixture = Fixture.Load(input.FixturePath);
        var fresh = 0;
        var reused = 0;

        foreach (var pr in Triage.Eligible(fixture).OrderBy(p => p.Number))
        {
            var digest = Triage.InputDigest(pr);
            var stored = TriageStore.PullRequest(store, pr.Number);
            var category = stored.InputDigest == digest && stored.Category.Length > 0
                ? stored.Category
                : Triage.Classify(pr);
            var risk = stored.InputDigest == digest && stored.Risk.Length > 0
                ? stored.Risk
                : Triage.AssessRisk(pr, category);

            if (stored.InputDigest == digest && stored.Category.Length > 0)
            {
                reused++;
            }
            else
            {
                fresh++;
                var next = stored with
                {
                    Number = pr.Number,
                    InputDigest = digest,
                    Stage = Phases.Classify,
                    Category = category,
                    Risk = risk,
                    Impact = 0,
                    Urgency = 0,
                    Quality = 0,
                    Action = string.Empty,
                    Reason = string.Empty,
                    Batch = null,
                    Labels = [],
                    Generation = state.Generation,
                };
                await TriageStore.CommitPullRequestAsync(store, input, next, "assessed");
            }

            Console.WriteLine($"  #{pr.Number,-4} {category,-10} risk={risk,-6} "
                + $"{pr.Diff.Files} file(s), +{pr.Diff.Additions}/-{pr.Diff.Deletions}"
                + $"  {pr.Title}"
                + (stored.InputDigest == digest && stored.Category.Length > 0 ? "   [from the store]" : string.Empty));
        }

        Console.WriteLine();
        if (reused > 0)
        {
            Console.WriteLine($"  reused:     {reused} assessment(s) at an unchanged digest, "
                + $"{fresh} newly classified");
            Console.WriteLine();
        }

        return await StepAsync(store, seam, input, state with { Phase = Phases.Score }, runtime);
    }

    // ─── Phase 3: score priority ─────────────────────────────────────────────────────────────

    private static async Task<TaskCommit> ScoreAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        Console.WriteLine(Phases.Header(Phases.Score));

        var fixture = Fixture.Load(input.FixturePath);
        foreach (var pr in Triage.Eligible(fixture).OrderBy(p => p.Number))
        {
            var stored = TriageStore.PullRequest(store, pr.Number);
            var score = Triage.Score(Assessment.From(pr, stored));
            await TriageStore.CommitPullRequestAsync(store, input, stored with
            {
                Stage = Phases.Score,
                Impact = score.Impact,
                Urgency = score.Urgency,
                Quality = score.Quality,
            }, "scored");
            Console.WriteLine($"  #{pr.Number,-4} {score.Total,3} = "
                + $"impact {score.Impact,2} + urgency {score.Urgency,2} + quality {score.Quality,2}");
        }

        Console.WriteLine();
        return await StepAsync(store, seam, input, state with { Phase = Phases.Recommend }, runtime);
    }

    // ─── Phase 4: recommend actions ──────────────────────────────────────────────────────────

    private static async Task<TaskCommit> RecommendAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        Console.WriteLine(Phases.Header(Phases.Recommend));

        var fixture = Fixture.Load(input.FixturePath);
        foreach (var pr in Triage.Eligible(fixture).OrderBy(p => p.Number))
        {
            var stored = TriageStore.PullRequest(store, pr.Number);
            var assessment = Assessment.From(pr, stored);
            var action = Triage.Recommend(assessment);
            assessment.Action = action;
            var reason = Triage.ActionReason(assessment);
            await TriageStore.CommitPullRequestAsync(store, input, stored with
            {
                Stage = Phases.Recommend,
                Action = action,
                Reason = reason,
            }, "recommended");
            Console.WriteLine($"  #{pr.Number,-4} {action,-13} {reason}");
        }

        Console.WriteLine();
        return await StepAsync(store, seam, input, state with { Phase = Phases.Batch }, runtime);
    }

    // ─── Phase 5: batch and label ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the batches over the whole eligible set, then commits each PR's batch and labels.
    /// </summary>
    /// <remarks>
    /// A batch is a property of the set, not of one PR, so it cannot be decided per document: this
    /// phase reads every assessment back, computes the clusters once, and writes the result onto each
    /// PR. Everything else it writes was already decided in the phase that owns it.
    /// </remarks>
    private static async Task<TaskCommit> BatchAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        Console.WriteLine(Phases.Header(Phases.Batch));

        var fixture = Fixture.Load(input.FixturePath);
        var assessments = Triage.Eligible(fixture).OrderBy(p => p.Number)
            .Select(pr => Assessment.From(pr, TriageStore.PullRequest(store, pr.Number)))
            .ToList();
        var batches = Triage.Batch(assessments);

        foreach (var batch in batches)
        {
            Console.WriteLine($"  {batch.Id}  {batch.Category} x{batch.Numbers.Count}: "
                + string.Join(", ", batch.Numbers.Select(n => $"#{n}")));
        }

        if (batches.Count == 0)
        {
            Console.WriteLine("  no cluster of 3+ similar PRs; no batch assigned");
        }

        foreach (var a in assessments)
        {
            a.Labels = Triage.TriageLabels(a);
            var removed = a.Pr.Labels.Where(Triage.IsTriageLabel).Except(a.Labels).ToArray();
            var preserved = a.Pr.Labels.Where(l => !Triage.IsTriageLabel(l)).ToArray();
            Console.WriteLine($"  #{a.Pr.Number,-4} add [{string.Join(", ", a.Labels)}]"
                + (removed.Length > 0 ? $" remove [{string.Join(", ", removed)}]" : "")
                + $" keep [{string.Join(", ", preserved)}]");
            await TriageStore.CommitPullRequestAsync(store, input, TriageStore.PullRequest(store, a.Pr.Number) with
            {
                Stage = Phases.Batch,
                Batch = a.Batch,
                Labels = [.. a.Labels],
            }, "batched");
        }

        var labelPayload = new System.Text.Json.Nodes.JsonObject();
        foreach (var a in assessments)
        {
            labelPayload[$"#{a.Pr.Number}"] = new System.Text.Json.Nodes.JsonObject
            {
                ["add"] = Triage.ToArray(a.Labels),
                ["remove"] = Triage.ToArray(a.Pr.Labels.Where(Triage.IsTriageLabel).Except(a.Labels).ToArray()),
                ["preserve"] = Triage.ToArray(a.Pr.Labels.Where(l => !Triage.IsTriageLabel(l)).ToArray()),
            };
        }

        Triage.WriteText(Path.Combine(input.OutputDirectory, "labels.json"),
            labelPayload.ToJsonString(Triage.Json) + "\n");
        Console.WriteLine("  wrote:      labels.json (add-labels safe output)");
        Console.WriteLine();

        return await StepAsync(store, seam, input, state with { Phase = Phases.Comment }, runtime);
    }

    // ─── Phase 6: comment each triaged PR ────────────────────────────────────────────────────

    /// <summary>
    /// Posts one comment per assessed PR, guarded by a memo and by the ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the step the issue is about. Two records have to disagree before a comment is written:
    /// </para>
    /// <list type="number">
    /// <item><b>The memo</b>, keyed <c>comment/{number}/{digest}</c>. It belongs to the run's task and
    /// is written after the comment exists, so a resume of the same run finds it and stops.</item>
    /// <item><b>The ledger</b>, a document owned by the agent. It survives the run: a later run is a
    /// new task with no memos and still must not re-post a comment that exists.</item>
    /// </list>
    /// <para>
    /// Both keys are the digest rather than the run, which is the whole mechanism: an unchanged PR
    /// produces the same key in every future run. And the digest rather than the number alone, so a PR
    /// whose input changed gets a new key and a new comment — a correction, not a duplicate.
    /// </para>
    /// </remarks>
    private static async Task<TaskCommit> CommentAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        Console.WriteLine(Phases.Header(Phases.Comment));

        var fixture = Fixture.Load(input.FixturePath);
        var commentDirectory = Path.Combine(input.OutputDirectory, "comments");
        var posted = state.Commented.ToList();
        var memoHits = 0;
        var ledgerHits = 0;
        var written = 0;

        foreach (var pr in Triage.Eligible(fixture).OrderBy(p => p.Number))
        {
            var number = pr.Number.ToString(CultureInfo.InvariantCulture);
            var assessment = Assessment.From(pr, TriageStore.PullRequest(store, pr.Number));
            var digest = Triage.InputDigest(pr);
            var key = $"comment/{number}/{digest}";
            var path = Path.Combine(commentDirectory, $"pr-{number}.md");
            var text = Triage.Comment(assessment);

            var memo = seam?.GuardDisabled == true ? null : await runtime.MemoAsync(key);
            // Two controls, and they are different experiments: `GuardDisabled` removes every guard
            // (a duplicate is the point), `LedgerHidden` removes only the cross-run record so the
            // in-run memo is what remains (memo-only is the point).
            var ledger = seam?.GuardDisabled == true || seam?.LedgerHidden == true
                ? new CommentLedger()
                : TriageStore.Ledger(store);
            var recorded = ledger.For(pr.Number, digest);

            if (memo is not null)
            {
                memoHits++;
                Console.WriteLine($"  skipped:    comments/pr-{number}.md — memo {key} present");
                Triage.WriteText(path, text);
                posted.Add(pr.Number);
                continue;
            }

            if (recorded is not null)
            {
                ledgerHits++;
                Console.WriteLine($"  skipped:    comments/pr-{number}.md — ledger records it posted at "
                    + $"{recorded.PostedAt} by run {recorded.RunId}");
                Triage.WriteText(path, text);
                await runtime.MemoAsync(key, DurableJson.ToElement(recorded));
                posted.Add(pr.Number);
                continue;
            }

            // The external effect. Everything above is a read; everything below records what it did.
            Triage.WriteText(path, text);
            var record = new CommentRecord
            {
                Number = pr.Number,
                Digest = digest,
                PostedAt = state.BaseTime,
                RunId = TriageStore.RunId(state.Repository),
                Bytes = System.Text.Encoding.UTF8.GetByteCount(text),
            };
            seam?.AfterCommentEffectWritten(posted.Count + 1);
            // The ledger entry and the step memo commit together: one transaction, so a resume sees
            // both or neither. The memo cannot be written through runtime.MemoAsync here — that
            // takes the gate this commit holds — which is why the transaction-scoped form exists.
            if (seam?.LedgerHidden != true)
            {
                await TriageStore.RecordCommentAsync(store, input, runtime.Task.Id, key, record);
            }
            else
            {
                // The memo on its own, committed outside a transaction so it survives on the task
                // record exactly as it would have — only the ledger entry is withheld.
                await runtime.MemoAsync(key, DurableJson.ToElement(record));
            }

            posted.Add(pr.Number);
            written++;
            Console.WriteLine($"  wrote:      comments/pr-{number}.md ({record.Bytes} bytes)");
            seam?.AfterNthComment(written);
        }

        Console.WriteLine();
        if (memoHits > 0 || ledgerHits > 0)
        {
            Console.WriteLine($"  idempotent: {written} comment(s) posted, {memoHits} skipped by memo, "
                + $"{ledgerHits} skipped by the ledger");
            Console.WriteLine();
        }

        return await StepAsync(store, seam, input, state with { Phase = Phases.Report, Commented = posted }, runtime);
    }

    // ─── Phase 7: create the triage report ───────────────────────────────────────────────────

    private static async Task<TaskCommit> ReportAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        Console.WriteLine(Phases.Header(Phases.Report));

        var fixture = Fixture.Load(input.FixturePath);
        var assessments = Triage.Eligible(fixture).OrderBy(p => p.Number)
            .Select(pr => Assessment.From(pr, TriageStore.PullRequest(store, pr.Number)))
            .ToList();
        var batches = Triage.Batch(assessments);

        var report = Triage.Report(Triage.Clock(state.BaseTime, state.StartedAt), fixture, assessments, batches,
            Triage.InCooldown(fixture), Triage.NotOurs(fixture), Triage.NotOpen(fixture));
        var reportPath = Path.Combine(input.OutputDirectory, "triage-report.md");
        Triage.WriteText(reportPath, report);
        Console.WriteLine($"  wrote:      triage-report.md ({new FileInfo(reportPath).Length} bytes)");

        var dataPayload = new System.Text.Json.Nodes.JsonObject();
        foreach (var a in assessments)
        {
            dataPayload[$"#{a.Pr.Number}"] = new System.Text.Json.Nodes.JsonObject
            {
                ["pr_number"] = a.Pr.Number,
                ["category"] = a.Category,
                ["risk"] = a.Risk,
                ["action"] = a.Action,
            };
        }

        Triage.WriteText(Path.Combine(input.OutputDirectory, "triage-data.json"),
            dataPayload.ToJsonString(Triage.Json) + "\n");
        Console.WriteLine("  wrote:      triage-data.json (data safe output)");
        Console.WriteLine();

        return await StepAsync(store, seam, input, state with { Phase = Phases.Save }, runtime);
    }

    // ─── Phase 8: save run state ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Finishes the task. There is no state file: every decision was committed as it was made, and
    /// the layer commits the task's outcome with the checkpoint that names this phase.
    /// </summary>
    private static async Task<TaskCommit> SaveAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        Console.WriteLine(Phases.Header(Phases.Save));

        var fixture = Fixture.Load(input.FixturePath);
        var assessments = Triage.Eligible(fixture).OrderBy(p => p.Number)
            .Select(pr => Assessment.From(pr, TriageStore.PullRequest(store, pr.Number)))
            .ToList();

        Console.WriteLine($"  wrote:      band-c.run@{TriageStore.RunId(state.Repository)} — generation "
            + $"{state.Generation}, phase {state.Phase}");
        Console.WriteLine($"  wrote:      band-c.pr@pr:<number> for {assessments.Count} pull request(s), "
            + "stage batch");
        Console.WriteLine($"  wrote:      band-c.comments@{Store.LedgerOwner} — "
            + $"{TriageStore.Ledger(store).Comments.Count} comment(s) on record");
        Console.WriteLine($"  outcome:    the task's terminal record carries the counts below");
        Console.WriteLine();

        var result = new RunResult
        {
            Triaged = assessments.Count,
            Commented = state.Commented.Distinct().Count(),
            InCooldown = Triage.InCooldown(fixture).Count,
            NotAgent = Triage.NotOurs(fixture).Count,
            NotOpen = Triage.NotOpen(fixture).Count,
        };

        Console.WriteLine($"triaged {assessments.Count} PR(s): "
            + string.Join(", ", assessments.Select(a => $"#{a.Pr.Number} {a.Action}")));
        Console.WriteLine($"{result.InCooldown} skipped by the 1-hour cooldown, "
            + $"{result.NotAgent} not authored by {Triage.AgentAuthor}, {result.NotOpen} not open.");

        // The run record is written at `save` first, then the task finishes. There is deliberately
        // no Running commit after this point: a terminal outcome is the only commit that ends a
        // task, and a Running one here would re-enter this phase for ever. That mistake was made and
        // caught by the guard-disabled control run hanging at 10,263 entries of `phase 8/8`.
        await TriageStore.CommitRunAsync(store, input, state);
        var commit = await runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(result)));
        seam?.AfterPhase(Phases.Save);
        return commit;
    }

    // ─── The step wrapper and the seam ──────────────────────────────────────────────────────

    /// <summary>
    /// Commits the run record at its new phase, then stops the process if the seam names it.
    /// </summary>
    /// <remarks>
    /// The order is the point: the commit lands first and the process leaves second, so the seam is a
    /// stop the store knows about. A seam that exited before the commit would demonstrate a crash the
    /// store cannot describe, which is the opposite of what the evidence needs.
    /// </remarks>
    private static async Task<TaskCommit> StepAsync(
        DurableStore store, ExitSeam? seam, RunInput input, RunState state, TaskRuntime runtime)
    {
        await TriageStore.CommitRunAsync(store, input, state);
        var commit = await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(state)));
        seam?.AfterPhase(Completed(state.Phase));
        return commit;
    }

    /// <summary>The phase a run record at <paramref name="phase"/> has just finished.</summary>
    /// <remarks>
    /// The record names the phase the *next* attempt enters, so the phase just completed is the one
    /// before it. Keeping the mapping here rather than at each call site is what stops the seam from
    /// meaning two things: <c>BAND_C_EXIT_AFTER_PHASE=comment</c> must stop after the comments exist,
    /// not before they do.
    /// </remarks>
    private static string Completed(string next)
    {
        var index = Array.IndexOf(Phases.Order, next);
        return index <= 0 ? next : Phases.Order[index - 1];
    }
}
