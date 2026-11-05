namespace AgentOrchestrator.Durable;

/// <summary>
/// The checkpoint of an enquiry task: where the enquiry got to, and what the next evaluation
/// needs to know about the turn that got it there.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Phase"/> is the task's own phase key and is always <see cref="EnquiryPhases.Run"/>.
/// The work is a conversation, so there is one task phase; the enquiry's progress is the
/// <em>document's</em> phase, mirrored in <see cref="EnquiryPhase"/>. The durable runtime reads
/// this property to choose a handler and requires a JSON string, which is why it is a string
/// rather than an enum.
/// </para>
/// <para>
/// <see cref="Turns"/> is the count of turn boundaries this task has committed. It is what makes a
/// restart visible: a task whose checkpoint says two turns resumes at two, not at zero.
/// </para>
/// </remarks>
public sealed class EnquiryCheckpoint
{
    /// <summary>The task phase key. Always <see cref="EnquiryPhases.Run"/>.</summary>
    public string Phase { get; set; } = EnquiryPhases.Run;

    /// <summary>The document's phase at the last committed turn boundary.</summary>
    public string EnquiryPhase { get; set; } = EnquiryPhases.Queued;

    /// <summary>How many turn boundaries this task has committed.</summary>
    /// <remarks>
    /// The count names the turn the task is on, not the turn it has finished: a turn is only
    /// finished once its effects are all committed, and the document — not this count — is what
    /// says whether that happened. A restart therefore re-enters at the same index and re-reads
    /// the document.
    /// </remarks>
    public int Turns { get; set; }

    /// <summary>
    /// The answer the turn in flight produced, so an interrupted process does not lose it.
    /// </summary>
    /// <remarks>
    /// A model that reports <c>done</c> through its phase tool ends the enquiry before its answer
    /// is committed. If the process dies between those two commits, the answer exists nowhere
    /// else: the document is already terminal and the transcript entry never landed. Carrying it
    /// on the checkpoint is what closes that window — the resumed task attaches it as the report
    /// instead of finishing with <c>report: null</c>.
    /// </remarks>
    public string? Answer { get; set; }
}
