namespace BandF.Durable;

/// <summary>The four agents. The names are the baseline's, so the evidence can name the same agent.</summary>
internal static class Agents
{
    public const string Coordinator = "triage_agent";
    public const string Refund = "refund_agent";
    public const string Order = "order_agent";
    public const string Returns = "return_agent";

    /// <summary>Written into a checkpoint before any agent holds the conversation.</summary>
    public const string Nobody = "(none)";

    public static readonly string[] Specialists = [Refund, Order, Returns];

    public static bool IsSpecialist(string? name) =>
        name is not null && Array.IndexOf(Specialists, name) >= 0;

    /// <summary>The specialist's tool, with the baseline's exact result string for each.</summary>
    public static string Tool(string specialist) => specialist switch
    {
        Refund => "process_refund",
        Order => "check_order_status",
        Returns => "process_return",
        _ => throw new ArgumentOutOfRangeException(nameof(specialist), specialist, "not a specialist"),
    };

    public static string Instructions(string specialist) => specialist switch
    {
        Refund => "You process refund requests.",
        Order => "You handle order and shipping inquiries.",
        Returns => "You manage product return requests.",
        _ => throw new ArgumentOutOfRangeException(nameof(specialist), specialist, "not a specialist"),
    };
}

/// <summary>Names that appear in the store, so a reader of the JSONL can find them.</summary>
internal static class Names
{
    /// <summary>The durable task's name; a reopened store finds its definition by it.</summary>
    public const string Task = "band-f.delegation";

    /// <summary>The directory a run keeps its store in, relative to the working directory.</summary>
    public const string StoreDirectory = ".durable";

    /// <summary>The file run.sh/resume.sh write to prove a directory is theirs to remove.</summary>
    public const string ScratchMarker = ".band-f-durable-scratch";

    /// <summary>The coordinator's one tool.</summary>
    public const string DecisionTool = "delegate_to_specialist";

    /// <summary>How many prior outputs of the same specialist are handed to it as context.</summary>
    public const int PriorOutputWindow = 3;
}

/// <summary>The phases a delegation checkpoint can name.</summary>
/// <remarks>
/// The phase is a property of the checkpoint, not a variable: <c>lib/Durable.cs</c> reads
/// <c>phase</c> out of the checkpoint JSON to pick the handler, which is what makes the checkpoint
/// the single record of where the run is.
/// </remarks>
internal static class Phases
{
    /// <summary>The coordinator decides which specialist takes the conversation.</summary>
    public const string Route = "route";

    /// <summary>The named specialist holds the conversation and produces its output.</summary>
    public const string Specialist = "specialist";

    /// <summary>The turn is finished with; the next one begins, or the run completes.</summary>
    public const string Settle = "settle";
}

/// <summary>
/// What the run was created with. Written once by the first process and read by every later one, so
/// a resume cannot be handed a different script or a different seam from its command line.
/// </summary>
internal sealed record DelegationInput
{
    /// <summary>The user's turns, in order. The script is part of the run, not of the process.</summary>
    public required List<string> Turns { get; init; }

    /// <summary>
    /// How this run's routing decisions are made: <c>model</c> or <c>scripted</c>.
    /// </summary>
    /// <remarks>
    /// Part of the run, not of the process. A run whose decisions came from a model must not start
    /// taking them from the scenario three turns later because a resume did not inherit an
    /// environment variable: the store would then hold a mixture and the recorded <c>Source</c> would
    /// be the only thing that still said so. Written once, like the script.
    /// </remarks>
    public required string Routing { get; init; }

    /// <summary>Turn indexes at which the process waits to be killed once the routing decision commits.</summary>
    public List<int> KillAfterRouting { get; init; } = [];

    /// <summary>Turn indexes at which the process waits to be killed once the delegation commits.</summary>
    public List<int> KillAfterDelegating { get; init; } = [];

    /// <summary>
    /// Turn indexes at which the process waits to be killed <i>inside</i> the specialist's turn: after
    /// the attempt is committed and before the model is called.
    /// </summary>
    public List<int> KillInTurn { get; init; } = [];
}

/// <summary>
/// The delegation's control state. <see cref="Holder"/> is the band's headline claim: it names the
/// agent that holds the conversation, and a resume consults nothing else to decide who runs next.
/// </summary>
internal sealed record DelegationCheckpoint
{
    /// <summary>The handler the runner dispatches to. Read by <c>lib/Durable.cs</c>, not by this file.</summary>
    public string Phase { get; init; } = Phases.Route;

    /// <summary>The agent holding the conversation.</summary>
    public string Holder { get; init; } = Agents.Coordinator;

    /// <summary>The user turn being served.</summary>
    public int Turn { get; init; }

    /// <summary>What the holder was asked to do, in the routing agent's words.</summary>
    public string? Request { get; init; }

    /// <summary>The durable entry the delegation committed under: the routing decision's join key.</summary>
    public string? DelegationEntry { get; init; }

    /// <summary>How many times this phase has been entered while holding this turn.</summary>
    public int Attempts { get; init; }
}

/// <summary>The coordinator's routing decision for one turn, written once and then only read.</summary>
internal sealed record RoutingDecision
{
    public required int Turn { get; init; }
    public required string Specialist { get; init; }
    public required string Request { get; init; }

    /// <summary>The model the runtime reported for the deciding turn, or <c>null</c> when scripted.</summary>
    public string? Model { get; init; }

    /// <summary><c>model</c> or <c>scripted</c>. The README depends on which, so it is recorded.</summary>
    public required string Source { get; init; }
}

/// <summary>The specialist's domain output: what the agent produced, as a record rather than prose.</summary>
internal sealed record SpecialistOutput
{
    public required int Turn { get; init; }
    public required string Specialist { get; init; }

    /// <summary>The agent's answer, committed as the domain value.</summary>
    public required string Answer { get; init; }

    /// <summary>The specialist's tool, and the exact result the tool returned.</summary>
    public string? Tool { get; init; }
    public string? ToolResult { get; init; }

    /// <summary>Model turns this output took to produce. A reused output took zero.</summary>
    public int ModelTurns { get; init; }

    /// <summary>The agent conversation that produced it.</summary>
    public required string Conversation { get; init; }

    /// <summary>The process that committed it.</summary>
    public required string ProducedBy { get; init; }
}

/// <summary>The user message that opened a turn, so a resumed process shows it once.</summary>
internal sealed record UserTurnRecord
{
    public required int Turn { get; init; }
    public required string Message { get; init; }
}

/// <summary>One control-transfer record, read back from the store.</summary>
internal sealed record WireRecord
{
    public required int Turn { get; init; }

    /// <summary>
    /// One of the four events the code emits: <c>delegated</c> (no decision existed),
    /// <c>delegated-from-memo</c> (control moved from a decision already in the store),
    /// <c>produced</c> (the specialist's output committed) or <c>settled</c> (the turn closed).
    /// </summary>
    public required string Event { get; init; }

    /// <summary>The agent the record is about: the holder, or the agent that produced.</summary>
    public required string Agent { get; init; }

    public string? Request { get; init; }
    public string? Detail { get; init; }

    /// <summary>
    /// For a delegation, the entry the routing decision that produced it was committed under. The
    /// delegation names its memo, so a reader can tell a delegation made from a committed decision
    /// from one made from a fresh answer without comparing timestamps.
    /// </summary>
    public string? RoutingEntry { get; init; }

    /// <summary>The durable entry the record was committed under.</summary>
    public required string EntryId { get; init; }
}

/// <summary>The run's terminal outcome.</summary>
internal sealed record DelegationResult
{
    public required string Reason { get; init; }
    public required int Turns { get; init; }
    public required List<string> Holders { get; init; }
}
