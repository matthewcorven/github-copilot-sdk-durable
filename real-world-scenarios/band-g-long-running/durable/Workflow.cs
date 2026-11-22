using System.Text.Json;
using Durable;

namespace BandGDurable;

/// <summary>What the run is asked to produce. Written once, at creation, and never restated.</summary>
public sealed class BriefInput
{
    /// <summary>The checkpoint directory this run reports into. Its own store lives here.</summary>
    public string RunDirectory { get; set; } = string.Empty;

    /// <summary>The task the two agents are asked to answer.</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>How long the gate stays open, in seconds, counted from when the run was created.</summary>
    public int GateSeconds { get; set; } = Brief.DefaultGateSeconds;
}

/// <summary>
/// The workflow's position. One field per fact the run has to remember, so a process that ends
/// anywhere leaves enough to continue rather than to start over.
/// </summary>
public sealed class BriefCheckpoint
{
    /// <summary>The phase the runner enters next.</summary>
    public string Phase { get; set; } = "plan";

    /// <summary>What the plan step decided.</summary>
    public string? Plan { get; set; }

    /// <summary>The researcher's answer, once it is recorded.</summary>
    public string? Research { get; set; }

    /// <summary>The writer's draft, once it is recorded.</summary>
    public string? Draft { get; set; }

    /// <summary>The gate's child task, once the gate has created it.</summary>
    public string? GateTask { get; set; }

    /// <summary>The gate's deadline, as a round-trip timestamp. Durable, so a restart honours the original.</summary>
    public DateTimeOffset? Deadline { get; set; }

    /// <summary>The decision the gate read back, once it is terminal.</summary>
    public string? Decision { get; set; }
}

/// <summary>A recorded model call: which step made it, and what came back.</summary>
public sealed class ModelCall
{
    /// <summary>The step that made the call: <c>plan</c>, <c>research</c>, <c>write</c>, or <c>brief</c>.</summary>
    public string Step { get; set; } = string.Empty;

    /// <summary>The prompt the turn was given.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>The assistant text the turn returned.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>The durable conversation the turn ran in.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Whether the turn was served by the scenario's own deterministic answer or a live model.</summary>
    public string ServedBy { get; set; } = string.Empty;
}

/// <summary>One line of the gate's ledger: who asked, and what they were told.</summary>
public sealed class GateLedgerEntry
{
    /// <summary>What the caller was trying to do: <c>approve</c>, <c>reject</c>, or <c>wake</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The brief the caller named, which may not be this run's.</summary>
    public string BriefId { get; set; } = string.Empty;

    /// <summary>Whether the caller's write reached the decision document.</summary>
    public bool Recorded { get; set; }

    /// <summary>Why it did or did not: the sentence a reader needs.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>When the caller arrived.</summary>
    public DateTimeOffset At { get; set; }
}

/// <summary>The approval, as recorded. First writer wins; a later caller reads this back.</summary>
public sealed class ApprovalDecision
{
    /// <summary>The brief this decision answers.</summary>
    public string BriefId { get; set; } = string.Empty;

    /// <summary>Whether the plan was approved.</summary>
    public bool Approved { get; set; }

    /// <summary>The reviewer's comment.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Which process wrote it: <c>resume</c>, <c>late</c>, <c>duplicate</c>, or <c>timeout</c>.</summary>
    public string Recorder { get; set; } = string.Empty;

    /// <summary>When it was written.</summary>
    public DateTimeOffset At { get; set; }
}

/// <summary>The constants the two scripts and both tasks agree on.</summary>
public static class Brief
{
    /// <summary>The workflow task's definition name.</summary>
    public const string TaskName = "band-g.brief-workflow";

    /// <summary>The gate's task definition name.</summary>
    public const string GateTaskName = "band-g.approval-gate";

    /// <summary>The document kind holding the approval. Not an internal kind: it is the scenario's.</summary>
    public const string DecisionKind = "band-g.approval";

    /// <summary>
    /// The approval document's definition, with an initial value. A document written for the first
    /// time hands back the definition's initial value, and one written with no initial hands back
    /// <see langword="null"/> — so every document that is read-modify-written carries one.
    /// </summary>
    public static readonly DocumentDefinition<ApprovalDecision> DecisionDefinition =
        new(DecisionKind, initial: () => new ApprovalDecision());

    /// <summary>The document kind holding the model-call ledger.</summary>
    public const string LedgerKind = "band-g.model-ledger";

    /// <summary>The default gate window: long enough for a human, short enough for a test.</summary>
    public const int DefaultGateSeconds = 30;

    /// <summary>The task the two agents are asked to answer, as the baseline asks it.</summary>
    public const string Task =
        "Draft a concise internal brief describing how our research and implementation teams should "
        + "collaborate to launch a beta feature for data-driven email summarization. Highlight the key "
        + "milestones, risks, and communication cadence.";

    /// <summary>The JSON body a decision document is written with.</summary>
    public static JsonElement DecisionElement(ApprovalDecision decision) => DurableJson.ToElement(decision);

    /// <summary>Reads the decision document's latest value, or <see langword="null"/> when never written.</summary>
    public static ApprovalDecision? ReadDecision(DurableStore store, string owner)
    {
        var state = store.Document(DecisionKind, owner);
        return state?.Latest is { } latest
            ? latest.Deserialize<ApprovalDecision>(DurableJson.Options)
            : null;
    }
}
