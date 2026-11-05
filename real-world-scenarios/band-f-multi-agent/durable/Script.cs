namespace BandF.Durable;

/// <summary>
/// The user's turns.
/// </summary>
/// <remarks>
/// <para>
/// Turns 0, 1 and 3 are the sample's own scripted responses, verbatim: response 1 (the damaged
/// order), response 2 (the refund) and response 3 (the closing "thanks"). Turn 2 is the F1 port's
/// insertion. The sample's <i>opening</i> message — <c>"Hello, I need assistance with my recent
/// purchase."</c> — is not here, and its absence is deliberate: it asks for help without naming an
/// order or a problem, so no specialist owns it and the routing guard would fail the run on it. The
/// script therefore starts where the requests start, and the README's turn table names the replaced
/// opening rather than pretending this is the sample's script unchanged.
/// </para>
/// <para>
/// Turn 3 is the sample's closing response with this variant's addition folded into it. It exists
/// because the band's claim is that a specialist is handed its <i>own</i> prior output on a later
/// turn, so the script needs a turn that returns to the specialist that already produced something:
/// <c>return_agent</c> answers turn 0, and turn 3 asks it to continue. It also gives the evidence a
/// turn whose routing decision it can kill after, so a resumed process has to reuse a committed
/// decision rather than make one.
/// </para>
/// <para>
/// <b>Why a turn with nothing to route is a failure here and a reply there.</b> The sample's closing
/// "Thanks for resolving this." stood alone when the script was written that way first, and the live
/// run failed on it — correctly. This coordinator has exactly one capability, the delegation tool, so
/// a turn it cannot delegate is a turn it cannot serve: the run failed with "the coordinator answered
/// without calling delegate_to_specialist" rather than inventing a holder. In the sample the triage
/// agent is an ordinary chat agent and can simply reply; here routing <i>is</i> the coordinator's job.
/// Rather than widen the coordinator's authority to keep a courtesy line, the closing line is folded
/// into a real request. The same reasoning is why the sample's opening greeting is absent: it names
/// no order and no problem, so no specialist owns it.
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
