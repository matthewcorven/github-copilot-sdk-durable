namespace Durable.Runtime;

/// <summary>
/// The exit codes the host returns. A scheduler needs to tell "the agent could not run"
/// apart from "the agent ran and the turn did not succeed", so the two are separate codes
/// rather than one generic failure.
/// </summary>
internal static class ExitCodes
{
    /// <summary>The agent ran and the turn succeeded.</summary>
    public const int Succeeded = 0;

    /// <summary>
    /// The agent could not run at all: an unknown agent, a definition that failed
    /// validation, a store another host already owns, or a runtime error. Nothing was
    /// admitted, so retrying the same invocation is safe.
    /// </summary>
    public const int AgentFailed = 1;

    /// <summary>The command line was not understood. Nothing was opened and nothing ran.</summary>
    public const int UsageError = 2;

    /// <summary>
    /// The agent ran and the turn was admitted, checkpointed, and settled, but produced no
    /// assistant message. The durable state advanced, so a retry continues from here rather
    /// than repeating the turn.
    /// </summary>
    public const int TurnUnsuccessful = 3;

    /// <summary>
    /// The subcommand exists and parsed, but its implementation is not in this build. The
    /// host names the issue that delivers it.
    /// </summary>
    public const int NotAvailable = 4;
}
