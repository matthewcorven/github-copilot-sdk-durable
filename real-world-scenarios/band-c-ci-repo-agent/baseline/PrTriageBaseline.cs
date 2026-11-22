// Band C baseline — a local reproduction of github/gh-aw's `pr-triage-agent` workflow
// (`.github/workflows/pr-triage-agent.md`, vendored verbatim under `upstream/`).
//
// The workflow is a scheduled agent job that triages open `app/github-copilot` pull requests. It
// keeps its only memory between runs in a git branch: the `repo-memory` tool writes
// `/tmp/gh-aw/repo-memory/default/pr-triage-latest.json` on branch `memory/pr-triage`. Nothing else
// survives the run — the process, the Actions workspace and `/tmp` are all created per run. That is
// the property this band exists to compare, so this reproduction keeps it exactly:
//
//   * state is a JSON file on a branch of a local bare repository, read and written with git
//     plumbing (`hash-object`, `mktree`, `commit-tree`, `update-ref`);
//   * every run is a cold start in a new process, with no in-process carry-over;
//   * the eight phases run in the workflow's order and read/write the same shape of JSON.
//
// It is deliberately NOT improved. There is no lock on the branch, no compare-and-swap beyond what
// `update-ref` gives for free, no merge of concurrent writers, no journal, no retry, and a run that
// dies between Phase 6 and Phase 8 leaves the comments written and the state from the run before.
// Those are the behaviours under test, not defects to fix here.
//
// What stands in for the platform:
//   * the GitHub API          -> `baseline/fixture/pull-requests.json`, a fixed snapshot
//   * `add-comment` / `create-issue` / `add-labels` -> files under the run directory
//   * the model call          -> a deterministic classifier and scorer (see FIDELITY.md)
//
// Usage:
//   dotnet run PrTriageBaseline.cs -- --run-dir <dir> [--repo <dir>] [--fixture <file>]
//                                    [--now <iso8601>] [--after <minutes>]
//
// Exit codes: 0 the run completed (including "skipped by the run cooldown"); 1 bad input or a git
// failure; 2 bad usage.

#:property TargetFramework=net10.0
#:property Nullable=enable
#:property NoWarn=NU1903;IL2026;IL3050

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

return await Triage.RunAsync(args);

static class Triage
{
    // The phase names are the workflow's own headings. Printing them is how a reader checks the
    // reproduction against `upstream/pr-triage-agent.md`.
    const string CooldownNote =
        "skip any PR that was opened or last updated (new commits, edits) less than 1 hour before " +
        "the current run time";

    const int CooldownMinutes = 60;
    // Named to match upstream: the branch `repo-memory` writes to and the file it writes.
    const string StateBranch = "refs/heads/memory/pr-triage";
    const string StatePath = "pr-triage-latest.json";
    static readonly string StateRef = $"{StateBranch}:{StatePath}";
    const string AgentAuthor = "app/github-copilot";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"baseline: {ex.Message}");
            Console.Error.WriteLine("usage: dotnet run PrTriageBaseline.cs -- --run-dir <dir> [--repo <dir>] "
                + "[--fixture <file>] [--now <iso8601>] [--after <minutes>]");
            return 2;
        }

        try
        {
            return await RunPhasesAsync(options);
        }
        catch (GitException ex)
        {
            Console.Error.WriteLine($"baseline: git failed: {ex.Message}");
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"baseline: {ex.Message}");
            return 1;
        }
        catch (JsonException ex)
        {
            // A previous writer left something that is not JSON on the state branch. This is a
            // failed run, not a crash: name the file and the parse position so a reader can find
            // the bad commit, and exit 1 like every other Phase 1 input failure. State is not a
            // fallback: the run does not silently start cold, because the branch clearly exists.
            Console.Error.WriteLine($"baseline: state on {StateBranch} is not valid JSON: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> RunPhasesAsync(Options options)
    {
        Directory.CreateDirectory(options.RunDir);
        var commentsDir = Path.Combine(options.RunDir, "comments");
        Directory.CreateDirectory(commentsDir);

        var fixture = Fixture.Load(options.FixturePath);
        // `--after` shifts whatever base clock the run starts from, so the two options combine:
        // an explicit `--now` is the base, the fixture's own captured time is the default base.
        var baseTime = options.Now
            ?? DateTimeOffset.Parse(fixture.CapturedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var now = baseTime.AddMinutes(options.AfterMinutes);

        Console.WriteLine("band C baseline — local reproduction of github/gh-aw pr-triage-agent");
        Console.WriteLine($"run time:   {Iso(now)}");
        Console.WriteLine($"repository: {fixture.Repository}");
        Console.WriteLine($"repo:       {options.RepoPath}");
        Console.WriteLine($"fixture:    {options.FixturePath} ({fixture.PullRequests.Count} pull requests)");
        Console.WriteLine($"output:     {options.RunDir}");
        Console.WriteLine();

        Repo.Ensure(options.RepoPath);

        // ── Phase 1 ────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("phase 1/8  load state and collect pull requests");

        var previous = Repo.TryReadState(options.RepoPath);
        string? priorRun = null;
        if (previous is null)
        {
            Console.WriteLine("  state:      cold start — no memory/pr-triage branch, nothing loaded");
        }
        else
        {
            priorRun = previous["run"]?["startedAt"]?.GetValue<string>();
            var priorTotal = previous["summary"]?["triaged"]?.GetValue<int>() ?? 0;
            Console.WriteLine($"  state:      loaded memory/pr-triage:{StatePath} "
                + $"({Repo.StateBytes(options.RepoPath)} bytes, commit {Repo.StateCommit(options.RepoPath)?[..8]})");
            Console.WriteLine($"  prior run:  {priorRun} triaged {priorTotal} PR(s)");
        }

        // The per-PR cooldown, verbatim: "skip any PR that was opened or last updated ... less than
        // 1 hour before the current run time".
        var notOurs = new List<PullRequest>();
        var notOpen = new List<PullRequest>();
        var inCooldown = new List<(PullRequest Pr, int Minutes)>();
        var eligible = new List<PullRequest>();

        foreach (var pr in fixture.PullRequests)
        {
            if (!string.Equals(pr.Author, AgentAuthor, StringComparison.Ordinal))
            {
                notOurs.Add(pr);
            }
            else if (!string.Equals(pr.State, "open", StringComparison.Ordinal))
            {
                notOpen.Add(pr);
            }
            else
            {
                var youngest = Math.Min(pr.OpenedMinutesAgo, pr.UpdatedMinutesAgo);
                if (youngest < CooldownMinutes)
                {
                    inCooldown.Add((pr, youngest));
                }
                else
                {
                    eligible.Add(pr);
                }
            }
        }

        Console.WriteLine($"  collected:  {fixture.PullRequests.Count} pull requests"
            + " (pull_request_read get / get_files / get_reviews / get_check_runs)");
        foreach (var pr in notOurs)
        {
            Console.WriteLine($"  not ours:   #{pr.Number} authored by {pr.Author} — not {AgentAuthor}");
        }

        foreach (var pr in notOpen)
        {
            Console.WriteLine($"  not open:   #{pr.Number} state={pr.State}");
        }

        foreach (var (pr, minutes) in inCooldown)
        {
            Console.WriteLine($"  cooldown:   #{pr.Number} "
                + $"{(pr.OpenedMinutesAgo < pr.UpdatedMinutesAgo ? "opened" : "updated")} "
                + $"{minutes}m ago (< {CooldownMinutes}m) — {CooldownNote}");
        }

        Console.WriteLine($"  eligible:   {eligible.Count} pull request(s)");
        Console.WriteLine();

        // The run gate. This is NOT in the vendored workflow: a scheduled run is six hours apart, so
        // the file never has to ask what to do when the previous run was minutes ago. Here, where a
        // run is one command and can be started twice in a row, that question is exactly the one the
        // state exists to answer, and the answer the workflow's own rule gives is "nothing is
        // eligible yet". It reads the state and adds nothing to it.
        if (previous is not null && priorRun is not null)
        {
            var age = (int)Math.Floor((now - DateTimeOffset.Parse(priorRun, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)).TotalMinutes);
            if (age < CooldownMinutes)
            {
                Console.WriteLine($"  run gate:   the previous run finished {age}m ago "
                    + $"(< {CooldownMinutes}m); this run is inside the cooldown and takes no action.");
                Console.WriteLine($"  run gate:   would have triaged {eligible.Count} PR(s); "
                    + "no comments, no labels, no report, no state write.");
                foreach (var pr in eligible)
                {
                    Console.WriteLine($"              #{pr.Number} {pr.Title}");
                }

                Console.WriteLine();
                Console.WriteLine("run skipped by the one-hour cooldown; no state was written.");
                return 0;
            }
        }

        // ── Phase 2 ────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("phase 2/8  classify and assess risk");

        var assessments = new List<Assessment>();
        foreach (var pr in eligible.OrderBy(p => p.Number))
        {
            var category = Classify(pr);
            var risk = AssessRisk(pr, category);
            assessments.Add(new Assessment(pr, category, risk));
            Console.WriteLine($"  #{pr.Number,-4} {category,-10} risk={risk,-6} "
                + $"{pr.Diff.Files} file(s), +{pr.Diff.Additions}/-{pr.Diff.Deletions}"
                + $"  {pr.Title}");
        }

        Console.WriteLine();

        // ── Phase 3 ────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("phase 3/8  score priority (impact 0-50 + urgency 0-30 + quality 0-20)");

        foreach (var a in assessments)
        {
            a.Score = Score(a);
            Console.WriteLine($"  #{a.Pr.Number,-4} {a.Score.Total,3} = "
                + $"impact {a.Score.Impact,2} + urgency {a.Score.Urgency,2} + quality {a.Score.Quality,2}");
        }

        Console.WriteLine();

        // ── Phase 4 ────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("phase 4/8  recommend actions");

        foreach (var a in assessments)
        {
            a.Action = Recommend(a);
            a.Reason = ActionReason(a);
            Console.WriteLine($"  #{a.Pr.Number,-4} {a.Action,-13} {a.Reason}");
        }

        Console.WriteLine();

        // ── Phase 5 ────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("phase 5/8  batch and label");

        var batches = Batch(assessments);
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
            a.Labels = TriageLabels(a);
            var removed = a.Pr.Labels.Where(IsTriageLabel).Except(a.Labels).ToArray();
            var preserved = a.Pr.Labels.Where(l => !IsTriageLabel(l)).ToArray();
            Console.WriteLine($"  #{a.Pr.Number,-4} add [{string.Join(", ", a.Labels)}]"
                + (removed.Length > 0 ? $" remove [{string.Join(", ", removed)}]" : "")
                + $" keep [{string.Join(", ", preserved)}]");
        }

        var labelPayload = new JsonObject();
        foreach (var a in assessments)
        {
            labelPayload[$"#{a.Pr.Number}"] = new JsonObject
            {
                ["add"] = ToArray(a.Labels),
                ["remove"] = ToArray(a.Pr.Labels.Where(IsTriageLabel).Except(a.Labels).ToArray()),
                ["preserve"] = ToArray(a.Pr.Labels.Where(l => !IsTriageLabel(l)).ToArray()),
            };
        }

        WriteText(Path.Combine(options.RunDir, "labels.json"), labelPayload.ToJsonString(Json) + "\n");
        Console.WriteLine($"  wrote:      labels.json (add-labels safe output)");
        Console.WriteLine();

        // ── Phase 6 ────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("phase 6/8  comment each triaged PR");

        foreach (var a in assessments)
        {
            var path = Path.Combine(commentsDir, $"pr-{a.Pr.Number}.md");
            WriteText(path, Comment(a));
            Console.WriteLine($"  wrote:      comments/pr-{a.Pr.Number}.md");
        }

        Console.WriteLine();

        // ── Phase 7 ────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("phase 7/8  create the triage report issue");

        var reportPath = Path.Combine(options.RunDir, "triage-report.md");
        WriteText(reportPath, Report(now, fixture, assessments, batches, inCooldown, notOurs, notOpen));
        Console.WriteLine($"  wrote:      {Path.GetFileName(reportPath)} "
            + $"({new FileInfo(reportPath).Length} bytes)");

        var dataPayload = new JsonObject();
        foreach (var a in assessments)
        {
            dataPayload[$"#{a.Pr.Number}"] = new JsonObject
            {
                ["pr_number"] = a.Pr.Number,
                ["category"] = a.Category,
                ["risk"] = a.Risk,
                ["action"] = a.Action,
            };
        }

        WriteText(Path.Combine(options.RunDir, "triage-data.json"), dataPayload.ToJsonString(Json) + "\n");
        Console.WriteLine("  wrote:      triage-data.json (data safe output)");
        Console.WriteLine();

        // ── Phase 8 ────────────────────────────────────────────────────────────────────────────
        Console.WriteLine("phase 8/8  save run state");

        var state = BuildState(now, fixture, assessments, batches, inCooldown, notOurs, notOpen, priorRun);
        var commit = Repo.WriteState(options.RepoPath, state.ToJsonString(Json) + "\n", now,
            moveRef: !options.DryRun);
        Console.WriteLine($"  wrote:      memory/pr-triage:{StatePath} ({commit.Bytes} bytes)");
        Console.WriteLine($"  commit:     {commit.Sha[..8]} on {StateBranch} (parent {commit.Parent?[..8] ?? "none"})");
        Console.WriteLine();

        Console.WriteLine($"triaged {assessments.Count} PR(s): "
            + string.Join(", ", assessments.Select(a => $"#{a.Pr.Number} {a.Action}")));
        Console.WriteLine($"{inCooldown.Count} skipped by the 1-hour cooldown, "
            + $"{notOurs.Count} not authored by {AgentAuthor}, {notOpen.Count} not open.");

        return 0;
    }

    // ─── Classification, risk, scoring, recommendation ─────────────────────────────────────────

    static string Classify(PullRequest pr)
    {
        if (pr.Diff.WhitespaceOnly)
        {
            return "formatting";
        }

        if (pr.Files.All(IsDocsPath))
        {
            return "docs";
        }

        if (pr.Files.All(f => IsTestPath(f) || IsDocsPath(f)))
        {
            return "test";
        }

        var title = pr.Title.ToLowerInvariant();
        var planned = pr.Files.Any(f => f.EndsWith(".csproj", StringComparison.Ordinal)
            || f.EndsWith("lock.json", StringComparison.Ordinal)
            || f.EndsWith(".lock", StringComparison.Ordinal)
            || f.EndsWith("Directory.Packages.props", StringComparison.Ordinal)
            || f.EndsWith("Dockerfile", StringComparison.Ordinal));

        if (title.StartsWith("feat", StringComparison.Ordinal))
        {
            return "feature";
        }

        if (title.StartsWith("fix", StringComparison.Ordinal))
        {
            return "bug";
        }

        if (title.StartsWith("refactor", StringComparison.Ordinal))
        {
            return "refactor";
        }

        if (title.StartsWith("chore", StringComparison.Ordinal))
        {
            return "chore";
        }

        if (planned)
        {
            return "chore";
        }

        return pr.Files.Length <= 2 ? "docs" : "refactor";
    }

    static string AssessRisk(PullRequest pr, string category)
    {
        var churn = pr.Diff.Additions + pr.Diff.Deletions;

        if (category is "docs" or "test" or "formatting")
        {
            return "low";
        }

        if (category == "chore")
        {
            return churn > 120 || pr.Diff.Files > 4 ? "high" : "medium";
        }

        if (category == "refactor")
        {
            return churn > 1000 || pr.Diff.Files > 20 ? "high" : "medium";
        }

        // bug and feature: high when it touches a critical path or is large, otherwise medium.
        return pr.Files.Any(IsCriticalPath) || churn > 1000 || pr.Diff.Files > 20 ? "high" : "medium";
    }

    static ScoreBreakdown Score(Assessment a)
    {
        var pr = a.Pr;
        var churn = pr.Diff.Additions + pr.Diff.Deletions;

        var impact = churn switch
        {
            <= 20 => 8,
            <= 100 => 6,
            <= 500 => 4,
            <= 1500 => 2,
            _ => 0,
        };
        impact += pr.Files.Any(IsCriticalPath) ? 22 : 10;
        impact += a.Category switch
        {
            "bug" => 20,
            "feature" => 18,
            "test" => 10,
            "refactor" => 10,
            "docs" => 6,
            "chore" => 6,
            _ => 4,
        };
        impact += pr.Files.Any(IsTestPath) ? 5 : 0;
        impact = Math.Min(50, impact);

        var urgency = 0;
        if (a.Category == "bug")
        {
            urgency += 12;
        }

        urgency += pr.Ci switch
        {
            "failing" => 12,
            "pending" => 6,
            _ => 0,
        };
        if (pr.UpdatedMinutesAgo >= 14 * 24 * 60)
        {
            urgency += 6;
        }

        if (pr.SupersededBy is not null)
        {
            urgency += 8;
        }

        urgency = Math.Min(30, urgency);

        var quality = 10;
        quality += pr.Ci switch
        {
            "passing" => 6,
            "pending" => 0,
            _ => -8,
        };
        quality += pr.Reviews switch
        {
            "approved" => 4,
            "changes-requested" => -4,
            _ => 0,
        };
        quality += pr.AgentQuality switch
        {
            "high" => 4,
            "low" => -4,
            _ => 0,
        };
        quality = Math.Clamp(quality, 0, 20);

        return new ScoreBreakdown(impact, urgency, quality);
    }

    static string Recommend(Assessment a)
    {
        var pr = a.Pr;

        if (pr.SupersededBy is not null || pr.UpdatedMinutesAgo >= 30 * 24 * 60)
        {
            return "close";
        }

        if (a.Risk == "high" && (pr.Ci == "failing" || a.Score.Quality < 8))
        {
            return "defer";
        }

        if (a.Risk == "low" && a.Score.Quality >= 16 && pr.Ci == "passing")
        {
            return "auto_merge";
        }

        if (a.Score.Quality >= 18 && a.Score.Impact >= 35)
        {
            return "fast_track";
        }

        if (a.Score.Impact < 20)
        {
            return "defer";
        }

        return "batch_review";
    }

    static string ActionReason(Assessment a) => a.Action switch
    {
        "close" when a.Pr.SupersededBy is not null => $"superseded by #{a.Pr.SupersededBy}",
        "close" => "no activity for 30+ days",
        "defer" when a.Risk == "high" && a.Pr.Ci == "failing" => "high risk and CI failing",
        "defer" when a.Risk == "high" => $"high risk, quality {a.Score.Quality}/20",
        "defer" => $"impact {a.Score.Impact}/50 below the triage threshold",
        "auto_merge" => $"low risk, quality {a.Score.Quality}/20, CI passing",
        "fast_track" => $"quality {a.Score.Quality}/20, impact {a.Score.Impact}/50",
        _ => "similar low/medium-risk work, reviewed together",
    };

    // ─── Batching and labels ───────────────────────────────────────────────────────────────────

    static List<Batch> Batch(List<Assessment> assessments)
    {
        var batches = new List<Batch>();
        var index = 0;
        // Groups of 3+, in a fixed category order so two runs produce the same ids.
        foreach (var category in new[] { "docs", "test", "formatting", "chore", "refactor", "bug", "feature" })
        {
            var members = assessments
                .Where(a => a.Category == category && a.Action != "close")
                .OrderBy(a => a.Pr.Number)
                .Select(a => a.Pr.Number)
                .ToList();
            if (members.Count < 3)
            {
                continue;
            }

            index++;
            var batch = new Batch($"batch-{index:000}", category, members);
            batches.Add(batch);
            foreach (var a in assessments.Where(a => members.Contains(a.Pr.Number)))
            {
                a.Batch = batch.Id;
            }
        }

        return batches;
    }

    static bool IsTriageLabel(string label) =>
        label.StartsWith("pr-type:", StringComparison.Ordinal)
        || label.StartsWith("pr-risk:", StringComparison.Ordinal)
        || label.StartsWith("pr-priority:", StringComparison.Ordinal)
        || label.StartsWith("pr-action:", StringComparison.Ordinal)
        || label.StartsWith("pr-agent:", StringComparison.Ordinal)
        || label.StartsWith("pr-batch:", StringComparison.Ordinal);

    static List<string> TriageLabels(Assessment a)
    {
        var labels = new List<string>
        {
            $"pr-type:{a.Category}",
            $"pr-risk:{a.Risk}",
            $"pr-priority:{PriorityBand(a.Score.Total)}",
            $"pr-action:{a.Action}",
            $"pr-agent:{(a.Pr.IsFork ? "fork" : "same-repo")}",
        };

        if (a.Batch is not null)
        {
            labels.Add($"pr-batch:{a.Batch}");
        }

        return labels;
    }

    static string PriorityBand(int total) => total switch
    {
        >= 70 => "p0",
        >= 55 => "p1",
        >= 40 => "p2",
        _ => "p3",
    };

    // ─── Output documents ──────────────────────────────────────────────────────────────────────

    static string Comment(Assessment a)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Triage — #{a.Pr.Number} {a.Pr.Title}**");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Category | `{a.Category}` |");
        sb.AppendLine($"| Risk | `{a.Risk}` |");
        sb.AppendLine($"| Score | **{a.Score.Total}** / 100 |");
        sb.AppendLine($"| Action | `{a.Action}` |");
        if (a.Batch is not null)
        {
            sb.AppendLine($"| Batch | `{a.Batch}` |");
        }

        sb.AppendLine();
        sb.AppendLine("**Score breakdown**");
        sb.AppendLine();
        sb.AppendLine("| Component | Points | Max |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine($"| Impact | {a.Score.Impact} | 50 |");
        sb.AppendLine($"| Urgency | {a.Score.Urgency} | 30 |");
        sb.AppendLine($"| Quality | {a.Score.Quality} | 20 |");
        sb.AppendLine();
        sb.AppendLine($"CI: `{a.Pr.Ci}` · reviews: `{a.Pr.Reviews}` · "
            + $"updated {a.Pr.UpdatedMinutesAgo}m ago · {a.Pr.Diff.Files} file(s), "
            + $"+{a.Pr.Diff.Additions}/-{a.Pr.Diff.Deletions}");
        if (a.Reason.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Why: {a.Reason}.");
        }

        return sb.ToString();
    }

    static string Report(DateTimeOffset now, Fixture fixture, List<Assessment> assessments,
        List<Batch> batches, List<(PullRequest Pr, int Minutes)> inCooldown,
        List<PullRequest> notOurs, List<PullRequest> notOpen)
    {
        var sb = new StringBuilder();
        // The workflow requires "###/#### headings only" for this document.
        sb.AppendLine("### PR Triage Report");
        sb.AppendLine();
        sb.AppendLine($"Run {Iso(now)} · `{fixture.Repository}` · "
            + $"{assessments.Count} PR(s) triaged · {inCooldown.Count} in cooldown · "
            + $"{notOurs.Count} not authored by `{AgentAuthor}` · {notOpen.Count} not open.");
        sb.AppendLine();

        sb.AppendLine("#### Totals");
        sb.AppendLine();
        sb.AppendLine("| Metric | Count |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Pull requests in the fixture | {fixture.PullRequests.Count} |");
        sb.AppendLine($"| Triaged | {assessments.Count} |");
        sb.AppendLine($"| Skipped by the 1-hour cooldown | {inCooldown.Count} |");
        sb.AppendLine($"| Skipped, not `{AgentAuthor}` | {notOurs.Count} |");
        sb.AppendLine($"| Skipped, not open | {notOpen.Count} |");
        sb.AppendLine();

        sb.AppendLine("#### Distribution");
        sb.AppendLine();
        sb.AppendLine("| Category | Count |");
        sb.AppendLine("| --- | --- |");
        foreach (var group in assessments.GroupBy(a => a.Category).OrderBy(g => g.Key))
        {
            sb.AppendLine($"| `{group.Key}` | {group.Count()} |");
        }

        sb.AppendLine();
        sb.AppendLine("| Risk | Count |");
        sb.AppendLine("| --- | --- |");
        foreach (var group in assessments.GroupBy(a => a.Risk).OrderBy(g => g.Key))
        {
            sb.AppendLine($"| `{group.Key}` | {group.Count()} |");
        }

        sb.AppendLine();
        sb.AppendLine("| Priority | Count |");
        sb.AppendLine("| --- | --- |");
        foreach (var group in assessments.GroupBy(a => PriorityBand(a.Score.Total)).OrderBy(g => g.Key))
        {
            sb.AppendLine($"| `{group.Key}` | {group.Count()} |");
        }

        sb.AppendLine();
        sb.AppendLine("| Action | Count |");
        sb.AppendLine("| --- | --- |");
        foreach (var group in assessments.GroupBy(a => a.Action).OrderBy(g => g.Key))
        {
            sb.AppendLine($"| `{group.Key}` | {group.Count()} |");
        }

        sb.AppendLine();
        sb.AppendLine("#### Top priority");
        sb.AppendLine();
        sb.AppendLine("| PR | Score | Category | Risk | Action |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var a in assessments.OrderByDescending(a => a.Score.Total).ThenBy(a => a.Pr.Number).Take(5))
        {
            sb.AppendLine($"| #{a.Pr.Number} | {a.Score.Total} | `{a.Category}` | `{a.Risk}` | `{a.Action}` |");
        }

        Section(sb, "Auto-merge candidates", assessments.Where(a => a.Action == "auto_merge"));
        Section(sb, "Fast-track items", assessments.Where(a => a.Action == "fast_track"));
        Section(sb, "Close candidates", assessments.Where(a => a.Action == "close"));
        Section(sb, "Deferred", assessments.Where(a => a.Action == "defer"));

        sb.AppendLine();
        sb.AppendLine("#### Batch opportunities");
        sb.AppendLine();
        if (batches.Count == 0)
        {
            sb.AppendLine("No cluster of three or more similar PRs.");
        }
        else
        {
            sb.AppendLine("| Batch | Category | PRs |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var batch in batches)
            {
                sb.AppendLine($"| `{batch.Id}` | `{batch.Category}` | "
                    + $"{string.Join(", ", batch.Numbers.Select(n => $"#{n}"))} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("#### In cooldown, not triaged this run");
        sb.AppendLine();
        if (inCooldown.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            sb.AppendLine("<details><summary>Skipped PRs</summary>");
            sb.AppendLine();
            foreach (var (pr, minutes) in inCooldown)
            {
                sb.AppendLine($"- #{pr.Number} {pr.Title} — youngest event {minutes}m ago");
            }

            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        sb.AppendLine();
        sb.AppendLine("#### Trends and next actions");
        sb.AppendLine();
        var autoMerge = assessments.Count(a => a.Action == "auto_merge");
        var close = assessments.Count(a => a.Action == "close");
        sb.AppendLine($"- {autoMerge} PR(s) are low risk with passing CI and a clean review; "
            + "they can land without discussion.");
        sb.AppendLine($"- {close} PR(s) are superseded or untouched for a month; close them before "
            + "they are triaged again.");
        sb.AppendLine($"- The next scheduled run must wait at least {CooldownMinutes} minutes "
            + "before any PR opened or updated since this one is eligible.");
        return sb.ToString();
    }

    static void Section(StringBuilder sb, string title, IEnumerable<Assessment> items)
    {
        var list = items.OrderBy(a => a.Pr.Number).ToList();
        sb.AppendLine();
        sb.AppendLine($"#### {title}");
        sb.AppendLine();
        if (list.Count == 0)
        {
            sb.AppendLine("None.");
            return;
        }

        sb.AppendLine("<details><summary>List</summary>");
        sb.AppendLine();
        foreach (var a in list)
        {
            sb.AppendLine($"- #{a.Pr.Number} {a.Pr.Title} — score {a.Score.Total}, "
                + $"`{a.Category}` / `{a.Risk}`");
        }

        sb.AppendLine();
        sb.AppendLine("</details>");
    }

    // ─── State payload ─────────────────────────────────────────────────────────────────────────

    static JsonObject BuildState(DateTimeOffset now, Fixture fixture, List<Assessment> assessments,
        List<Batch> batches, List<(PullRequest Pr, int Minutes)> inCooldown,
        List<PullRequest> notOurs, List<PullRequest> notOpen, string? previousRun)
    {
        var selected = new JsonArray();
        foreach (var a in assessments)
        {
            selected.Add(new JsonObject
            {
                ["number"] = a.Pr.Number,
                ["title"] = a.Pr.Title,
                ["category"] = a.Category,
                ["risk"] = a.Risk,
                ["score"] = a.Score.Total,
                ["breakdown"] = new JsonObject
                {
                    ["impact"] = a.Score.Impact,
                    ["urgency"] = a.Score.Urgency,
                    ["quality"] = a.Score.Quality,
                },
                ["action"] = a.Action,
                ["batch"] = a.Batch,
                ["labels"] = ToArray(a.Labels),
            });
        }

        var batchArray = new JsonArray();
        foreach (var batch in batches)
        {
            batchArray.Add(new JsonObject
            {
                ["id"] = batch.Id,
                ["category"] = batch.Category,
                ["numbers"] = ToArray(batch.Numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray()),
            });
        }

        var cooldownArray = new JsonArray();
        foreach (var (pr, minutes) in inCooldown)
        {
            cooldownArray.Add(new JsonObject
            {
                ["number"] = pr.Number,
                ["youngestEventMinutes"] = minutes,
            });
        }

        return new JsonObject
        {
            ["run"] = new JsonObject
            {
                ["startedAt"] = Iso(now),
                ["previousRun"] = previousRun,
                ["repository"] = fixture.Repository,
                ["fixtureCapturedAt"] = fixture.CapturedAt,
            },
            ["selectedCandidates"] = selected,
            ["batches"] = batchArray,
            ["summary"] = new JsonObject
            {
                ["collected"] = fixture.PullRequests.Count,
                ["triaged"] = assessments.Count,
                ["inCooldown"] = inCooldown.Count,
                ["notAgent"] = notOurs.Count,
                ["notOpen"] = notOpen.Count,
                ["autoMerge"] = assessments.Count(a => a.Action == "auto_merge"),
                ["fastTrack"] = assessments.Count(a => a.Action == "fast_track"),
                ["batchReview"] = assessments.Count(a => a.Action == "batch_review"),
                ["defer"] = assessments.Count(a => a.Action == "defer"),
                ["close"] = assessments.Count(a => a.Action == "close"),
                ["batches"] = batches.Count,
            },
            ["cooldownSkipped"] = cooldownArray,
        };
    }

    // ─── Paths ─────────────────────────────────────────────────────────────────────────────────

    static bool IsDocsPath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && !IsTestPath(path);

    static bool IsTestPath(string path) =>
        path.StartsWith("tests/", StringComparison.Ordinal)
        || path.Contains("/tests/", StringComparison.Ordinal)
        || path.Contains(".Tests/", StringComparison.Ordinal)
        || Path.GetFileName(path).StartsWith("test_", StringComparison.Ordinal);

    static bool IsCriticalPath(string path) =>
        path.StartsWith("lib/", StringComparison.Ordinal)
        || path.StartsWith("src/", StringComparison.Ordinal)
        || path.StartsWith("tools/", StringComparison.Ordinal);

    static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

// ─── The git branch as a key/value store ───────────────────────────────────────────────────────
//
// This is the `repo-memory` tool's storage in the workflow, reduced to the operations it performs:
// read a file from `memory/pr-triage`, write a file to `memory/pr-triage`. It is a branch write
// with a compare-and-swap on the ref, and nothing else — no merge, no lock, no retry.

sealed class Repo
{
    // The bare repository standing in for the GitHub repository. `memory/pr-triage` is a branch in
    // it; the state file is the only thing on that branch.
    public static void Ensure(string repoPath)
    {
        if (Directory.Exists(Path.Combine(repoPath, "objects")))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(repoPath) ?? repoPath);
        Run(repoPath, null, ["init", "--bare", "--quiet", "--initial-branch=main", repoPath]);
        var tree = Run(repoPath, string.Empty, ["mktree"]).Trim();
        var root = Run(repoPath, null, ["commit-tree", tree, "-m", "fixture repository"]).Trim();
        Run(repoPath, null, ["update-ref", "refs/heads/main", root]);
    }

    public static JsonObject? TryReadState(string repoPath)
    {
        var commit = StateCommit(repoPath);
        if (commit is null)
        {
            return null;
        }

        return JsonNode.Parse(Run(repoPath, null, ["show", $"{commit}:{StatePath}"])) as JsonObject;
    }

    public static string? StateCommit(string repoPath)
    {
        var result = Run(repoPath, null,
            ["rev-parse", "--verify", "--quiet", StateBranch], allowFailure: true);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    public static int StateBytes(string repoPath)
    {
        var commit = StateCommit(repoPath);
        if (commit is null)
        {
            return 0;
        }

        var size = Run(repoPath, null, ["cat-file", "-s", $"{commit}:{StatePath}"]);
        return int.Parse(size.Trim(), CultureInfo.InvariantCulture);
    }

    public static (string Sha, string? Parent, int Bytes) WriteState(
        string repoPath, string content, DateTimeOffset now, bool moveRef)
    {
        var blob = Run(repoPath, content, ["hash-object", "-w", "--stdin"]).Trim();
        var tree = Run(repoPath, $"100644 blob {blob}\t{StatePath}\n", ["mktree"]).Trim();
        var parent = StateCommit(repoPath);

        var message = $"pr-triage: run {Triage.Iso(now)}";
        var commitArgs = parent is null
            ? new[] { "commit-tree", tree, "-m", message }
            : ["commit-tree", tree, "-p", parent, "-m", message];
        var commit = Run(repoPath, null, commitArgs).Trim();

        if (!moveRef)
        {
            // --dry-run: the objects are written, the branch is not moved, so the next run reads the
            // state from before this one. It is the seam for reproducing a run that dies in Phase 8.
            return (commit, parent, Encoding.UTF8.GetByteCount(content));
        }

        // A compare-and-swap on the branch ref: `update-ref <ref> <new> <old>`. There is no lock and
        // no retry — a concurrent writer that moved the ref in between makes this fail.
        if (parent is null)
        {
            Run(repoPath, null, ["update-ref", StateBranch, commit]);
        }
        else
        {
            Run(repoPath, null, ["update-ref", StateBranch, commit, parent]);
        }

        return (commit, parent, Encoding.UTF8.GetByteCount(content));
    }

    // Named to match upstream: the branch `repo-memory` writes to and the file it writes.
    const string StateBranch = "refs/heads/memory/pr-triage";
    const string StatePath = "pr-triage-latest.json";
    static readonly string StateRef = $"{StateBranch}:{StatePath}";

    static string Run(string repoPath, string? stdin, string[] args, bool allowFailure = false)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("--git-dir");
        start.ArgumentList.Add(repoPath);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_AUTHOR_NAME"] = "pr-triage";
        start.Environment["GIT_AUTHOR_EMAIL"] = "pr-triage@invalid";
        start.Environment["GIT_COMMITTER_NAME"] = "pr-triage";
        start.Environment["GIT_COMMITTER_EMAIL"] = "pr-triage@invalid";
        start.Environment["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z";
        start.Environment["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z";

        using var process = Process.Start(start) ?? throw new GitException("could not start git");
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 && !allowFailure)
        {
            throw new GitException($"git {string.Join(' ', args)}: {stderr.Trim()}");
        }

        return stdout;
    }
}

sealed class GitException(string message) : Exception(message);

// ─── Fixture model ─────────────────────────────────────────────────────────────────────────────

sealed class Fixture
{
    public required string Repository { get; init; }
    public required string CapturedAt { get; init; }
    public required List<PullRequest> PullRequests { get; init; }

    public static Fixture Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"no fixture at {path}");
        }

        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new GitException($"fixture {path} is not a JSON object");

        var list = new List<PullRequest>();
        foreach (var node in root["pullRequests"]!.AsArray())
        {
            var o = node!.AsObject();
            list.Add(new PullRequest
            {
                Number = o["number"]!.GetValue<int>(),
                Title = o["title"]!.GetValue<string>(),
                Author = o["author"]!.GetValue<string>(),
                HeadRef = o["headRef"]!.GetValue<string>(),
                IsFork = o["isFork"]!.GetValue<bool>(),
                State = o["state"]!.GetValue<string>(),
                OpenedMinutesAgo = o["openedMinutesAgo"]!.GetValue<int>(),
                UpdatedMinutesAgo = o["updatedMinutesAgo"]!.GetValue<int>(),
                Files = o["files"]!.AsArray().Select(f => f!.GetValue<string>()).ToArray(),
                Diff = new DiffShape(
                    o["diff"]!["files"]!.GetValue<int>(),
                    o["diff"]!["additions"]!.GetValue<int>(),
                    o["diff"]!["deletions"]!.GetValue<int>(),
                    o["diff"]!["whitespaceOnly"]!.GetValue<bool>()),
                Ci = o["ci"]!.GetValue<string>(),
                Reviews = o["reviews"]!.GetValue<string>(),
                AgentQuality = o["agentQuality"]!.GetValue<string>(),
                Labels = o["labels"]!.AsArray().Select(l => l!.GetValue<string>()).ToArray(),
                SupersededBy = o["supersededBy"] is null ? null : o["supersededBy"]!.GetValue<int>(),
            });
        }

        return new Fixture
        {
            Repository = root["repository"]!.GetValue<string>(),
            CapturedAt = root["capturedAt"]!.GetValue<string>(),
            PullRequests = list,
        };
    }
}

sealed class PullRequest
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string HeadRef { get; init; }
    public required bool IsFork { get; init; }
    public required string State { get; init; }
    public required int OpenedMinutesAgo { get; init; }
    public required int UpdatedMinutesAgo { get; init; }
    public required string[] Files { get; init; }
    public required DiffShape Diff { get; init; }
    public required string Ci { get; init; }
    public required string Reviews { get; init; }
    public required string AgentQuality { get; init; }
    public required string[] Labels { get; init; }
    public required int? SupersededBy { get; init; }
}

sealed record DiffShape(int Files, int Additions, int Deletions, bool WhitespaceOnly);

sealed record ScoreBreakdown(int Impact, int Urgency, int Quality)
{
    public int Total => Impact + Urgency + Quality;
}

sealed class Assessment(PullRequest pr, string category, string risk)
{
    public PullRequest Pr { get; } = pr;
    public string Category { get; } = category;
    public string Risk { get; } = risk;
    public ScoreBreakdown Score { get; set; } = new(0, 0, 0);
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Batch { get; set; }
    public List<string> Labels { get; set; } = [];
}

sealed record Batch(string Id, string Category, List<int> Numbers);

sealed class Options
{
    public required string RunDir { get; init; }
    public required string RepoPath { get; init; }
    public required string FixturePath { get; init; }
    public DateTimeOffset? Now { get; init; }
    public int AfterMinutes { get; init; }
    public bool DryRun { get; init; }

    public static Options Parse(string[] args)
    {
        string? runDir = null, repo = null, fixture = null, now = null;
        var after = 0;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--run-dir":
                    runDir = Next(args, ref i);
                    break;
                case "--repo":
                    repo = Next(args, ref i);
                    break;
                case "--fixture":
                    fixture = Next(args, ref i);
                    break;
                case "--now":
                    now = Next(args, ref i);
                    break;
                case "--after":
                    after = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        if (runDir is null)
        {
            throw new ArgumentException("--run-dir is required");
        }

        var absolute = Path.GetFullPath(runDir);
        // Default: the fixture next to this file. `run.sh` passes --fixture explicitly; the default
        // exists so `dotnet run PrTriageBaseline.cs -- --run-dir <dir>` works on its own.
        var fixturePath = fixture is null
            ? Path.GetFullPath(Path.Combine(ThisFile.DirectoryName!, "fixture", "pull-requests.json"))
            : Path.GetFullPath(fixture);

        return new Options
        {
            RunDir = absolute,
            RepoPath = Path.GetFullPath(repo ?? Path.Combine(absolute, "repo.git")),
            FixturePath = fixturePath,
            Now = now is null
                ? null
                : DateTimeOffset.Parse(now, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            AfterMinutes = after,
            DryRun = dryRun,
        };
    }

    static readonly FileInfo ThisFile = SourceFile();

    static FileInfo SourceFile(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => new(path);

    static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{args[i]} needs a value");
        }

        return args[++i];
    }
}
