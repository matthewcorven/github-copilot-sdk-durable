namespace AgentOrchestrator.Durable;

/// <summary>
/// The durable names of the pipeline phases.
/// </summary>
/// <remarks>
/// The names are the task phase keys, the checkpoint's <c>phase</c> property, and the transcript
/// entry kinds, so one string identifies a phase everywhere it is written. They are deliberately
/// not derived from the enum's member names: the store is a wire format, and renaming a member
/// must not silently rename what a stored enquiry says.
/// </remarks>
public static class EnquiryPhases
{
    /// <summary>The enquiry task's single phase key.</summary>
    public const string Run = "run";

    /// <summary>Created and not yet run.</summary>
    public const string Queued = "queued";

    /// <summary>The model is deciding whether the enquiry is genuine.</summary>
    public const string Validating = "validating";

    /// <summary>The model is searching the listings.</summary>
    public const string Searching = "searching";

    /// <summary>The model is writing the report.</summary>
    public const string WritingReport = "writing_report";

    /// <summary>Rejected as spam or garbage. Terminal.</summary>
    public const string RejectedGarbage = "rejected_garbage";

    /// <summary>Rejected because nothing matched. Terminal.</summary>
    public const string RejectedNoMatches = "rejected_no_matches";

    /// <summary>Finished with a report. Terminal.</summary>
    public const string Done = "done";

    /// <summary>The durable name of a phase.</summary>
    public static string Name(Phase phase) => phase switch
    {
        Phase.Queued => Queued,
        Phase.Validating => Validating,
        Phase.Searching => Searching,
        Phase.WritingReport => WritingReport,
        Phase.RejectedGarbage => RejectedGarbage,
        Phase.RejectedNoMatches => RejectedNoMatches,
        Phase.Done => Done,
        _ => Queued,
    };

    /// <summary>The phase a durable name refers to, or <see cref="Phase.Queued"/> when unknown.</summary>
    public static Phase From(string? name) => name switch
    {
        Validating => Phase.Validating,
        Searching => Phase.Searching,
        WritingReport => Phase.WritingReport,
        RejectedGarbage => Phase.RejectedGarbage,
        RejectedNoMatches => Phase.RejectedNoMatches,
        Done => Phase.Done,
        _ => Phase.Queued,
    };

    /// <summary>Whether a durable phase name is terminal.</summary>
    public static bool IsTerminal(string? name) =>
        From(name) is Phase.RejectedGarbage or Phase.RejectedNoMatches or Phase.Done;

    /// <summary>The phase the driver runs after this one, or the same phase when it is terminal.</summary>
    public static string Next(string name) => name switch
    {
        Queued => Validating,
        Validating => Searching,
        Searching => WritingReport,
        _ => name,
    };
}
