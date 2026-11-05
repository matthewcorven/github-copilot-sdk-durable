namespace AgentOrchestrator.Durable;

/// <summary>The input of an enquiry task.</summary>
public sealed class EnquiryInput
{
    /// <summary>The conversation that owns the enquiry document and its transcript.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>The short id the UI shows.</summary>
    public string EnquiryId { get; set; } = string.Empty;

    /// <summary>The customer's enquiry, verbatim.</summary>
    public string Enquiry { get; set; } = string.Empty;
}

/// <summary>The outcome of an enquiry task: the phase it reached and its report.</summary>
public sealed class EnquiryOutcome
{
    /// <summary>The durable name of the phase the enquiry ended in.</summary>
    public string Phase { get; set; } = EnquiryPhases.Queued;

    /// <summary>The report, when the enquiry ended in one.</summary>
    public string? Report { get; set; }
}
