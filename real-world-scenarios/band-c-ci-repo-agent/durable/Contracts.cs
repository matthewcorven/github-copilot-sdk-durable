using Durable;

namespace BandC.Durable;

/// <summary>
/// The store keys and the three documents this variant keeps.
/// </summary>
/// <remarks>
/// This file is durability. The phase names are the baseline's own headings, kept identical so a
/// reader can put the two stdouts side by side; the documents exist only because the state moved.
/// </remarks>
public static class Store
{
    /// <summary>The task name the triage state machine is registered under.</summary>
    public const string TaskName = "band-c.triage";

    /// <summary>The extension that carries the task.</summary>
    public const string ExtensionName = "band-c";

    /// <summary>The document kind holding one run's record.</summary>
    public const string RunKind = "band-c.run";

    /// <summary>The document kind holding one pull request's committed assessment.</summary>
    public const string PullRequestKind = "band-c.pr";

    /// <summary>The document kind holding the comment ledger.</summary>
    public const string LedgerKind = "band-c.comments";

    /// <summary>
    /// The owner id of the comment ledger.
    /// </summary>
    /// <remarks>
    /// Not a conversation id: a posted comment is an effect on the repository, and it outlives the
    /// process, the run and the conversation that produced it. An owner named for the agent is what
    /// makes that explicit in the store — and it is why a resumed run in a different process, with a
    /// different task and no memos, still finds the ledger.
    /// </remarks>
    public const string LedgerOwner = "agent:pr-triage";

    /// <summary>
    /// The run record: one document per repository, so two repos triaged by one store have separate
    /// records, and a second run of the same repo continues the first.
    /// </summary>
    public static readonly DocumentDefinition<RunState> Run =
        new(RunKind, DocumentHistory.Rewindable, ForkPolicy.Initial, () => new RunState());

    /// <summary>
    /// One pull request's assessment. Rewindable, so a later reader can see which run wrote the
    /// assessment it is looking at.
    /// </summary>
    public static readonly DocumentDefinition<PullRequestState> PullRequest =
        new(PullRequestKind, DocumentHistory.Rewindable, ForkPolicy.Initial, () => new PullRequestState());

    /// <summary>The comment ledger: every comment this agent has posted, with the digest it was posted for.</summary>
    public static readonly DocumentDefinition<CommentLedger> Ledger =
        new(LedgerKind, DocumentHistory.Rewindable, ForkPolicy.Initial, () => new CommentLedger());

    /// <summary>The owner id of a pull request's document.</summary>
    public static string PullRequestOwner(int number) => $"pr:{number}";

    /// <summary>
    /// The run document's owner id: derived from the repository name, not a clock or a process, so
    /// the next run of the same repository continues this record instead of starting a second one.
    /// </summary>
    public static string OwnerOf(string repository) =>
        new(repository.Select(ch => char.IsLetterOrDigit(ch) || ch is '/' or '-' or '.' or '_' ? ch : '_').ToArray());
}

/// <summary>
/// The eight phase names, exactly the baseline's order and spelling.
/// </summary>
public static class Phases
{
    public const string Collect = "collect";
    public const string Classify = "classify";
    public const string Score = "score";
    public const string Recommend = "recommend";
    public const string Batch = "batch";
    public const string Comment = "comment";
    public const string Report = "report";
    public const string Save = "save";

    /// <summary>The order the phases are numbered in.</summary>
    public static readonly string[] Order = [Collect, Classify, Score, Recommend, Batch, Comment, Report, Save];

    /// <summary>The `phase n/8` header the baseline prints.</summary>
    public static string Header(string phase) => $"phase {Array.IndexOf(Order, phase) + 1}/8  {Title(phase)}";

    /// <summary>The baseline's heading for a phase, so the two runs read the same.</summary>
    public static string Title(string phase) => phase switch
    {
        Collect => "load state and collect pull requests",
        Classify => "classify and assess risk",
        Score => "score priority (impact 0-50 + urgency 0-30 + quality 0-20)",
        Recommend => "recommend actions",
        Batch => "batch and label",
        Comment => "comment each triaged PR",
        Report => "create the triage report issue",
        Save => "save run state",
        _ => phase,
    };
}

/// <summary>
/// One run's record. <see cref="Phase"/> is the resume point: a process that stops leaves it at the
/// phase it was working on, and the next process enters there instead of at the beginning.
/// </summary>
/// <remarks>
/// A record with settable properties, so a phase handler can write `state with { … }` and the layer
/// can read `phase` straight out of the serialized checkpoint. The property name matters: the layer
/// selects a handler by `checkpoint["phase"]`, so renaming it would leave the task unrunnable.
/// </remarks>
public sealed record RunState
{
    /// <summary>The handler the task is at. The layer reads this to pick a phase handler.</summary>
    public string Phase { get; set; } = Phases.Collect;

    /// <summary>The repository this run triages, as the fixture names it.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>This run's clock, ISO-8601 UTC.</summary>
    public string StartedAt { get; set; } = string.Empty;

    /// <summary>The base clock the fixture's relative ages were applied to, ISO-8601 UTC.</summary>
    public string BaseTime { get; set; } = string.Empty;

    /// <summary>The previous generation's start time, if the record held one.</summary>
    public string? PreviousRun { get; set; }

    /// <summary>How many times this run has been entered, including the first.</summary>
    public int Attempts { get; set; }

    /// <summary>How many generations this repository has run.</summary>
    public int Generation { get; set; }

    /// <summary>The PRs whose comment this run posted or adopted.</summary>
    public List<int> Commented { get; set; } = [];

    /// <summary>Every PR the run did not triage, by number: cooldown, not ours, not open.</summary>
    public List<int> Excluded { get; set; } = [];

    /// <summary>The phase a previous attempt stopped in, when this attempt is a resume.</summary>
    public string? ResumedFrom { get; set; }
}

/// <summary>
/// One pull request's committed assessment.
/// </summary>
/// <remarks>
/// <see cref="Stage"/> is what makes the per-phase writes meaningful rather than one blob that
/// happens to be written once: Phase 2 commits the category and risk, Phase 3 the score, Phase 4 the
/// action and its reason, Phase 5 the batch and labels. A process that stops between two of them
/// leaves a document that says how far it got, and the phase that resumes continues from there
/// instead of recomputing what is already decided.
/// </remarks>
public sealed record PullRequestState
{
    /// <summary>The PR number.</summary>
    public int Number { get; set; }

    /// <summary>The digest of everything the decision reads. Changing it restarts the assessment.</summary>
    public string InputDigest { get; set; } = string.Empty;

    /// <summary>How far the assessment has got: <c>classify</c>, <c>score</c>, <c>recommend</c>, <c>batch</c>.</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>The category, from the baseline's vocabulary.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The risk band.</summary>
    public string Risk { get; set; } = string.Empty;

    /// <summary>The three score components.</summary>
    public int Impact { get; set; }
    public int Urgency { get; set; }
    public int Quality { get; set; }

    /// <summary>The recommended action and the reason printed beside it.</summary>
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    /// <summary>The batch id, when the PR is in one.</summary>
    public string? Batch { get; set; }

    /// <summary>The labels Phase 5 applies.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>The run generation that last wrote this document.</summary>
    public int Generation { get; set; }
}

/// <summary>
/// The comment ledger: one entry per (pull request, input digest) that has been commented on.
/// </summary>
/// <remarks>
/// A document rather than a task memo, because it outlives the run: a new run is a new task with no
/// memos, and it still must not re-post a comment that already exists. The memo guards the step
/// within a run; the ledger is the fact across runs. Both are checked, and the evidence has a
/// control for each.
/// </remarks>
public sealed record CommentLedger
{
    /// <summary>Every comment this agent has posted, in the order they were posted.</summary>
    public List<CommentRecord> Comments { get; set; } = [];

    /// <summary>The record for a PR at a digest, or null.</summary>
    public CommentRecord? For(int number, string digest) =>
        Comments.LastOrDefault(comment => comment.Number == number && comment.Digest == digest);
}

/// <summary>One posted comment.</summary>
public sealed record CommentRecord
{
    public int Number { get; set; }
    public string Digest { get; set; } = string.Empty;
    public string PostedAt { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public int Bytes { get; set; }
}

/// <summary>The task's immutable input: what a run is asked to do, written once at creation.</summary>
public sealed record RunInput
{
    /// <summary>The fixture file, absolute.</summary>
    public string FixturePath { get; set; } = string.Empty;

    /// <summary>Where comments, labels, the report and the data payload are written.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>The base clock, ISO-8601 UTC. The fixture's ages are relative to it.</summary>
    public string BaseTime { get; set; } = string.Empty;

    /// <summary>
    /// The durable conversation that owns the entry log.
    /// </summary>
    /// <remarks>
    /// Every document version points at an entry, and an entry belongs to a conversation. This run
    /// has no model session, so the journal is the conversation record its entries hang off — a real
    /// record in the store, not an id invented at the call site. Resuming reads it from the task's
    /// input rather than creating a second one.
    /// </remarks>
    public string Journal { get; set; } = string.Empty;
}

/// <summary>The task's result: the counts the run finished with.</summary>
public sealed record RunResult
{
    public int Triaged { get; set; }
    public int Commented { get; set; }
    public int InCooldown { get; set; }
    public int NotAgent { get; set; }
    public int NotOpen { get; set; }
}
