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
    public int Turns { get; set; }
}
