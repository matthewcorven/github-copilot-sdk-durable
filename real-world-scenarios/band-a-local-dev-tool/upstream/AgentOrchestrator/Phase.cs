namespace AgentOrchestrator;

public enum Phase
{
    Queued,
    Validating,
    Searching,
    WritingReport,
    RejectedGarbage,
    RejectedNoMatches,
    Done
}

public record PhaseNode(string Label, int Col, int Row, Phase[] Next, bool IsFinished = false, int YOffset = 0);

public static class PipelineConfig
{
    public static readonly Dictionary<Phase, PhaseNode> Nodes = new()
    {
        // Main sequence — col 0
        [Phase.Queued]             = new("Queued",             0, 0, [Phase.Validating]),
        [Phase.Validating]         = new("Validating",         0, 1, [Phase.Searching, Phase.RejectedGarbage]),
        [Phase.Searching]          = new("Searching",          0, 2, [Phase.WritingReport, Phase.RejectedNoMatches]),
        [Phase.WritingReport]      = new("Writing Report",     0, 3, [Phase.Done]),

        // Terminal branches — col 1, nudged down so connectors slope gently
        [Phase.RejectedGarbage]    = new("Rejected",           1, 1, [], IsFinished: true, YOffset: 20),
        [Phase.RejectedNoMatches]  = new("No Matches",         1, 2, [], IsFinished: true, YOffset: 20),
        [Phase.Done]               = new("Done",               1, 3, [], IsFinished: true, YOffset: 20),
    };

    // All edges in the DAG (for drawing connector lines)
    public static readonly (Phase From, Phase To)[] Edges =
        Nodes.SelectMany(kv => kv.Value.Next.Select(n => (kv.Key, n))).ToArray();
}
