using System.Text.Json.Serialization;
using Durable;

namespace AgentOrchestrator.Durable;

/// <summary>
/// The committed state of one customer enquiry. This is the document the baseline kept in an
/// in-process <c>Agent</c> object: the phase it is in, the intent the model last reported, the
/// report it wrote, and every transcript entry that belongs to it.
/// </summary>
/// <remarks>
/// Every field is plain JSON-serializable data on purpose. A checkpoint is a committed value, so
/// it has to be readable by a process that never saw the object that wrote it.
/// </remarks>
public sealed class EnquiryState
{
    /// <summary>The short enquiry id the UI shows. The conversation id is the durable half.</summary>
    public string EnquiryId { get; set; } = string.Empty;

    /// <summary>The customer's enquiry, verbatim.</summary>
    public string Enquiry { get; set; } = string.Empty;

    /// <summary>The committed phase. A restart resumes at this phase, not before it.</summary>
    public Phase Phase { get; set; } = Phase.Queued;

    /// <summary>The intent the model last reported, or null before it reports one.</summary>
    public string? Intent { get; set; }

    /// <summary>The report the model wrote for the salesperson, or null until it writes one.</summary>
    public string? Report { get; set; }

    /// <summary>When the enquiry was created, as an ISO 8601 timestamp.</summary>
    public string StartedAt { get; set; } = string.Empty;

    /// <summary>When the enquiry last reached a terminal phase, as an ISO 8601 timestamp.</summary>
    public string? FinishedAt { get; set; }

    /// <summary>When a rejected enquiry's card stops being shown.</summary>
    /// <remarks>
    /// The baseline deleted the agent from its dictionary after a fifteen-second delay. The
    /// delay is data here, not a timer: the card fades on its own and the record stays, so a
    /// restart still knows the enquiry was rejected.
    /// </remarks>
    public string? HideAt { get; set; }

    /// <summary>Why the enquiry ended where it did, when it did not end in a report.</summary>
    public string? Note { get; set; }

    /// <summary>How many times a process has re-entered this enquiry after an interruption.</summary>
    public int Restarts { get; set; }

    /// <summary>The durable task that runs the enquiry.</summary>
    public string? TaskId { get; set; }

    /// <summary>The transcript entries committed for this enquiry, oldest first.</summary>
    public List<EnquiryEntry> Entries { get; set; } = [];

    /// <summary>The terminal phases, which a later commit must never move out of.</summary>
    [JsonIgnore]
    public bool IsTerminal => PipelineConfig.Nodes[Phase].IsFinished;

    /// <summary>Whether the enquiry ended in anything other than a completed report.</summary>
    [JsonIgnore]
    public bool IsRejected => IsTerminal && Phase != Phase.Done;

    /// <summary>Whether this enquiry has a report to show.</summary>
    [JsonIgnore]
    public bool IsDone => Phase == Phase.Done;
}

/// <summary>One committed transcript entry of an enquiry.</summary>
public sealed class EnquiryEntry
{
    /// <summary>The durable entry id, so a restart can read the same entry back.</summary>
    public string EntryId { get; set; } = string.Empty;

    /// <summary>What the entry is: <c>enquiry</c>, <c>phase</c>, <c>intent</c>, <c>tool</c>, <c>report</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The entry's payload: the phase name, the intent, a tool line, the report.</summary>
    public string? Text { get; set; }

    /// <summary>The tool name, for a tool entry.</summary>
    public string? ToolName { get; set; }

    /// <summary>Whether the tool call succeeded, for a tool entry.</summary>
    public bool? Success { get; set; }

    /// <summary>When the entry was committed, as an ISO 8601 timestamp.</summary>
    public string At { get; set; } = string.Empty;
}
