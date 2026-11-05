using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace BandC.Durable;

/// <summary>
/// Everything the triage decision uses, and the two documents it produces.
/// </summary>
/// <remarks>
/// <para>
/// This is the baseline's logic, moved out of its `Triage` class and into a static type with no
/// state of its own. <c>Classify</c>, <c>AssessRisk</c>, <c>Score</c>, <c>Recommend</c>,
/// <c>Batch</c> and the rule functions are byte-for-byte the baseline's, and so are <c>Comment</c>,
/// <c>Report</c> and the label rules: if they ever diverge, "the two variants produce the same
/// comments" stops being a property and becomes a coincidence. <c>evidence/compare.py</c> re-reads
/// both files and checks it, so the claim is executable rather than asserted.
/// </para>
/// <para>
/// The only addition is <see cref="InputDigest"/>, which the baseline has no use for: it is the
/// identity of "the thing that was triaged", and it is what lets a later run skip a PR instead of
/// re-deciding it. It covers every field the decision reads and nothing else, so a PR whose title
/// changed is re-assessed and a PR whose body length changed is not.
/// </para>
/// </remarks>
public static class Triage
{
    /// <summary>The workflow's own wording, printed whenever a PR is skipped for it.</summary>
    public const string CooldownNote =
        "skip any PR that was opened or last updated (new commits, edits) less than 1 hour before " +
        "the current run time";

    public const int CooldownMinutes = 60;

    public const string AgentAuthor = "app/github-copilot";

    // ─── Classification, risk, scoring, recommendation ─────────────────────────────────────────

    public static string Classify(PullRequest pr)
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

    public static string AssessRisk(PullRequest pr, string category)
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

    public static ScoreBreakdown Score(Assessment a)
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

    public static string Recommend(Assessment a)
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

    public static string ActionReason(Assessment a) => a.Action switch
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

    public static List<Batch> Batch(List<Assessment> assessments)
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

    public static bool IsTriageLabel(string label) =>
        label.StartsWith("pr-type:", StringComparison.Ordinal)
        || label.StartsWith("pr-risk:", StringComparison.Ordinal)
        || label.StartsWith("pr-priority:", StringComparison.Ordinal)
        || label.StartsWith("pr-action:", StringComparison.Ordinal)
        || label.StartsWith("pr-agent:", StringComparison.Ordinal)
        || label.StartsWith("pr-batch:", StringComparison.Ordinal);

    public static List<string> TriageLabels(Assessment a)
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

    public static string PriorityBand(int total) => total switch
    {
        >= 70 => "p0",
        >= 55 => "p1",
        >= 40 => "p2",
        _ => "p3",
    };

    // ─── Output documents ──────────────────────────────────────────────────────────────────────

    public static string Comment(Assessment a)
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

    public static string Report(DateTimeOffset now, Fixture fixture, List<Assessment> assessments,
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

    public static void Section(StringBuilder sb, string title, IEnumerable<Assessment> items)
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

    // ─── Paths ─────────────────────────────────────────────────────────────────────────────────

    public static bool IsDocsPath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && !IsTestPath(path);

    public static bool IsTestPath(string path) =>
        path.StartsWith("tests/", StringComparison.Ordinal)
        || path.Contains("/tests/", StringComparison.Ordinal)
        || path.Contains(".Tests/", StringComparison.Ordinal)
        || Path.GetFileName(path).StartsWith("test_", StringComparison.Ordinal);

    public static bool IsCriticalPath(string path) =>
        path.StartsWith("lib/", StringComparison.Ordinal)
        || path.StartsWith("src/", StringComparison.Ordinal)
        || path.StartsWith("tools/", StringComparison.Ordinal);

    public static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    public static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ─── The fixture partitions the phases share ─────────────────────────────────────────────

    /// <summary>
    /// The PRs a run triages: authored by the agent, open, and outside the one-hour cooldown.
    /// </summary>
    /// <remarks>
    /// The baseline computes this once, inside Phase 1, and carries the four lists in local
    /// variables through to Phase 8. Here every phase that needs a list recomputes it from the
    /// fixture, because a local variable does not survive a process. The rule is the same rule, in
    /// the same order, so the four lists are the same four lists.
    /// </remarks>
    public static List<PullRequest> Eligible(Fixture fixture) =>
        [.. fixture.PullRequests.Where(pr =>
            string.Equals(pr.Author, AgentAuthor, StringComparison.Ordinal)
            && string.Equals(pr.State, "open", StringComparison.Ordinal)
            && Math.Min(pr.OpenedMinutesAgo, pr.UpdatedMinutesAgo) >= CooldownMinutes)];

    /// <summary>The agent's PRs inside the cooldown, with the age that put them there.</summary>
    public static List<(PullRequest Pr, int Minutes)> InCooldown(Fixture fixture) =>
        [.. fixture.PullRequests
            .Where(pr => string.Equals(pr.Author, AgentAuthor, StringComparison.Ordinal)
                && string.Equals(pr.State, "open", StringComparison.Ordinal)
                && Math.Min(pr.OpenedMinutesAgo, pr.UpdatedMinutesAgo) < CooldownMinutes)
            .Select(pr => (pr, Math.Min(pr.OpenedMinutesAgo, pr.UpdatedMinutesAgo)))];

    /// <summary>The PRs the agent did not author.</summary>
    public static List<PullRequest> NotOurs(Fixture fixture) =>
        [.. fixture.PullRequests.Where(pr => !string.Equals(pr.Author, AgentAuthor, StringComparison.Ordinal))];

    /// <summary>The agent's PRs that are no longer open.</summary>
    public static List<PullRequest> NotOpen(Fixture fixture) =>
        [.. fixture.PullRequests.Where(pr =>
            string.Equals(pr.Author, AgentAuthor, StringComparison.Ordinal)
            && !string.Equals(pr.State, "open", StringComparison.Ordinal))];

    /// <summary>The run's clock: its base time, which is the only clock this variant has.</summary>
    public static DateTimeOffset Clock(string baseTime, string startedAt) =>
        DateTimeOffset.Parse(baseTime.Length > 0 ? baseTime : startedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ─── Re-assessment identity ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The digest of everything a triage decision reads.
    /// </summary>
    /// <remarks>
    /// The baseline has no equivalent: it re-decides every eligible PR on every run, so "the input
    /// changed" is a question it never asks. The list below is exactly the fields <see cref="Classify"/>,
    /// <see cref="AssessRisk"/>, <see cref="Score"/>, <see cref="Recommend"/> and
    /// <see cref="TriageLabels"/> read — including the labels, because Phase 5 removes and preserves
    /// them. A field that does not affect the decision is left out on purpose: `bodyChars` and
    /// `openedMinutesAgo` are in the fixture and in neither list, so editing a PR body does not
    /// restart its triage and a PR getting older does not either. `updatedMinutesAgo` *is* in it:
    /// the cooldown and the 30-day close rule both read it.
    /// </remarks>
    public static string InputDigest(PullRequest pr)
    {
        var sb = new StringBuilder();
        sb.Append("number=").Append(pr.Number).Append('\n');
        sb.Append("title=").Append(pr.Title).Append('\n');
        sb.Append("author=").Append(pr.Author).Append('\n');
        sb.Append("headRef=").Append(pr.HeadRef).Append('\n');
        sb.Append("isFork=").Append(pr.IsFork).Append('\n');
        sb.Append("state=").Append(pr.State).Append('\n');
        sb.Append("updatedMinutesAgo=").Append(pr.UpdatedMinutesAgo).Append('\n');
        sb.Append("files=").AppendJoin(',', pr.Files).Append('\n');
        sb.Append("diff=").Append(pr.Diff.Files).Append(',')
            .Append(pr.Diff.Additions).Append(',')
            .Append(pr.Diff.Deletions).Append(',')
            .Append(pr.Diff.WhitespaceOnly).Append('\n');
        sb.Append("ci=").Append(pr.Ci).Append('\n');
        sb.Append("reviews=").Append(pr.Reviews).Append('\n');
        sb.Append("agentQuality=").Append(pr.AgentQuality).Append('\n');
        sb.Append("labels=").AppendJoin(',', pr.Labels).Append('\n');
        sb.Append("supersededBy=").Append(pr.SupersededBy?.ToString(CultureInfo.InvariantCulture) ?? "(none)");
        return Digest(sb.ToString());
    }

    /// <summary>A short, stable hash of a string: the identity, not the content.</summary>
    public static string Digest(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>The ISO-8601 UTC form both variants print and store.</summary>
    public static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The baseline's JSON shape for the label and data payloads.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}

// ─── The shapes the decision works in ─────────────────────────────────────────────────────────

/// <summary>The three score components; the total is their sum, never typed in.</summary>
public sealed record ScoreBreakdown(int Impact, int Urgency, int Quality)
{
    public int Total => Impact + Urgency + Quality;
}

/// <summary>A cluster of similar pull requests, three or more members.</summary>
public sealed record Batch(string Id, string Category, List<int> Numbers);

/// <summary>
/// One pull request's assessment, as the report and the comment see it.
/// </summary>
/// <remarks>
/// In memory only, and rebuilt from the committed document when a phase needs it. That is the
/// deliberate difference from the baseline, which carries this object in a local variable from
/// Phase 2 to Phase 8: here nothing survives a phase except what was committed.
/// </remarks>
public sealed class Assessment(PullRequest pr, string category, string risk)
{
    public PullRequest Pr { get; } = pr;
    public string Category { get; } = category;
    public string Risk { get; } = risk;
    public ScoreBreakdown Score { get; set; } = new(0, 0, 0);
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Batch { get; set; }
    public List<string> Labels { get; set; } = [];

    /// <summary>Rebuilds the view from a PR and its committed document.</summary>
    public static Assessment From(PullRequest pr, PullRequestState state) => new(pr, state.Category, state.Risk)
    {
        Score = new ScoreBreakdown(state.Impact, state.Urgency, state.Quality),
        Action = state.Action,
        Reason = state.Reason,
        Batch = state.Batch,
        Labels = [.. state.Labels],
    };
}
