namespace BandF.Durable;

/// <summary>
/// The user's turns.
/// </summary>
/// <remarks>
/// <para>
/// Turns 1, 2 and 3 are the baseline's scripted user, verbatim and in its order: the initial message,
/// the damaged-order response, and the closing "thanks". Those three are the sample's, and the port
/// in <c>../baseline/</c> uses the same three plus one insertion.
/// </para>
/// <para>
/// The fourth turn is this variant's insertion, and it is here for a reason the baseline did not
/// need. The band's claim is that a specialist is handed its <i>own</i> prior output on a later turn,
/// so the script needs a turn that returns to the specialist that already produced something:
/// <c>return_agent</c> answers turn 0, and the fourth turn asks it to continue. It also exercises a
/// second routing decision for the same specialist, which the evidence kills <i>inside</i> so the
/// resumed process has to reuse a committed decision rather than make one.
/// </para>
/// <para>
/// <b>Why the sample's closing "Thanks for resolving this." is not here.</b> The script was written
/// that way first, and the live run failed on it — correctly. This coordinator has exactly one
/// capability, the delegation tool, so a turn it cannot delegate is a turn it cannot serve; the run
/// failed with "the coordinator answered without calling delegate_to_specialist" rather than
/// inventing a holder. That is a real behavioural difference from the sample, where the triage agent
/// is an ordinary chat agent and can simply reply: in this variant routing <i>is</i> the coordinator's
/// job, and a turn with nothing to route is outside it. Rather than widen the coordinator's authority
/// to keep a closing line, the script's last turn is a real request that also closes the
/// conversation.
/// </para>
/// <para>
/// The run stops on its own turn count rather than on a phrase. "The model said goodbye" is a weaker
/// completion signal than "the run had four turns and served them", and the sample's <c>"welcome"</c>
/// predicate only governs autonomous continuation in the .NET port anyway (see the baseline README's
/// "What it does not do").
/// </para>
/// </remarks>
internal static class Script
{
    public static readonly List<string> Turns =
    [
        "My order 1234 arrived damaged and the packaging was destroyed. I'd like to return it.",
        "Please also process a refund for order 1234.",
        "One more thing: what is the status of order 1234?",
        "Thanks for resolving this. One last thing: can you confirm the return for order 1234 is still open?",
    ];
}
