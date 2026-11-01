namespace Durable.Runtime;

/// <summary>
/// How much of an agent's authority the host can actually enforce.
/// </summary>
/// <remarks>
/// The classification is deliberately pessimistic. A host-supplied permission handler is
/// host code the durable layer cannot inspect, so "I could not verify it" reads as the worst
/// case — <see cref="None"/> — rather than as the best one.
/// </remarks>
internal enum Containment
{
    /// <summary>The agent runs inside an enabled sandbox policy.</summary>
    Sandboxed,

    /// <summary>
    /// No sandbox, and no host handler: every permission request is decided by the durable
    /// fail-closed policy against the agent's stored grants.
    /// </summary>
    PermissionGated,

    /// <summary>No sandbox and a host-supplied handler, so the durable layer enforces nothing.</summary>
    None,
}

/// <summary>Classifies an agent definition's containment.</summary>
internal static class ContainmentClassifier
{
    /// <summary>
    /// Classifies <paramref name="definition"/>, given whether the host supplies its own
    /// permission handler.
    /// </summary>
    public static (Containment Kind, string Reason) Classify(AgentDefinition definition, bool hostHandlerSupplied)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Sandbox is { Enabled: true })
        {
            return (Containment.Sandboxed, "the agent definition carries an enabled sandbox policy");
        }

        if (hostHandlerSupplied)
        {
            return (Containment.None, "the host supplied its own permission handler, so the durable fail-closed policy is not in force");
        }

        return (Containment.PermissionGated, "no sandbox, so every permission request is decided by the durable fail-closed policy against the agent's grants");
    }
}
