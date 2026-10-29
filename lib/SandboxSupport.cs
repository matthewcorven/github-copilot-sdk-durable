// Shared support for the sandbox examples (33-35).
//
// This file is `#:include`-able, not compiled into the package: it is example scaffolding,
// not library surface. It holds the three things every sandbox example needs and none of
// them should re-derive:
//
//   1. Run-mode resolution. A sandbox example has to say which of two things happened:
//      the sandbox was ENFORCED (a real backend engaged and the policy was applied by the
//      OS) or the policy was REPLAYED (the policy was built and mapped, but no backend
//      engaged on this host). Those are the only two words the examples use. There is no
//      third "pretend" mode: a host with no backend reports Replayed and asserts only the
//      policy, never the enforcement.
//
//   2. Backend and platform reporting. The runtime reports the backend it chose
//      (seatbelt on macOS, bubblewrap on Linux, process_container on Windows) and the
//      platform it is on. Both are string-wrapper value types in the SDK, so they are
//      printed by their Value.
//
//   3. Shared assertions. The policy assertion set is the same in both modes; the
//      enforcement assertion set only runs when the backend engaged.

using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Durable.Examples;

/// <summary>How a sandbox example's run is being demonstrated.</summary>
public enum SandboxRunMode
{
    /// <summary>
    /// The policy was built and mapped, but no sandbox backend engaged on this host. The
    /// example asserts the policy only.
    /// </summary>
    Replayed,

    /// <summary>
    /// A real backend engaged and the OS applied the policy. The example asserts the policy
    /// and the enforcement.
    /// </summary>
    Enforced,
}

/// <summary>What the runtime reported about the sandbox for a session.</summary>
public sealed record SandboxReport(
    SandboxRunMode Mode,
    string Backend,
    string Platform,
    string Outcome,
    string Reason)
{
    /// <summary>Whether the sandbox actually engaged.</summary>
    public bool Engaged => Mode == SandboxRunMode.Enforced;

    /// <summary>A one-line description for the example's output.</summary>
    public string Describe() =>
        $"run mode: {Mode} (backend: {Backend}, platform: {Platform}, outcome: {Outcome}" +
        (string.IsNullOrEmpty(Reason) ? ")" : $", reason: {Reason})");
}

/// <summary>Run-mode resolution, backend reporting, and the shared sandbox assertions.</summary>
public static class SandboxSupport
{
    /// <summary>
    /// Resolves the run mode from the decisions a session reported.
    /// </summary>
    /// <remarks>
    /// The runtime reports the resolved policy first, then the spawn result, then the
    /// enforcement state. The enforcement state is the authority: it says whether the backend
    /// engaged (<c>engaged</c>) or not (<c>inactive</c>, <c>degraded</c>, <c>failed</c>). A
    /// session that reported no enforcement state at all is <see cref="SandboxRunMode.Replayed"/>,
    /// because nothing proved the OS applied anything.
    /// </remarks>
    public static SandboxReport Resolve(IReadOnlyList<SandboxDecisionEvent> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var resolved = decisions
            .Select(decision => decision.Data)
            .OfType<SandboxDecisionDataPolicyResolved>()
            .FirstOrDefault();
        var enforcement = decisions
            .Select(decision => decision.Data)
            .OfType<SandboxDecisionDataEnforcementState>()
            .FirstOrDefault();

        var backend = (enforcement?.Backend ?? resolved?.Backend ?? SandboxBackend.Unsupported).Value;
        var platform = (enforcement?.Platform ?? resolved?.Platform ?? SandboxPlatform.Other).Value;
        var outcome = (enforcement?.Outcome ?? resolved?.Outcome ?? SandboxOutcome.Inactive).Value;
        var reason = resolved?.DegradationReason?.Value ?? string.Empty;

        var engaged = enforcement is not null
            && enforcement.Outcome == SandboxOutcome.Engaged
            && backend is not ("none" or "unsupported");

        return new SandboxReport(
            engaged ? SandboxRunMode.Enforced : SandboxRunMode.Replayed,
            backend,
            platform,
            outcome,
            reason);
    }

    /// <summary>
    /// Asserts the policy the example built, independent of whether a backend engaged.
    /// </summary>
    /// <remarks>
    /// These assertions are about the durable layer, not the OS: the policy round-trips, the
    /// token expands, and the mapping is field-for-field. They hold on every platform, which
    /// is why they always run.
    /// </remarks>
    public static void AssertPolicy(
        DurableSandboxPolicy policy,
        SandboxConfig config,
        string workingDirectory,
        string because)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(config);

        Check.That(config.Enabled, $"{because}: the sandbox is enabled");
        Check.Equal(policy.AddCurrentWorkingDirectory, config.AddCurrentWorkingDirectory, $"{because}: AddCurrentWorkingDirectory maps");
        Check.Equal(policy.AllowBypass, config.AllowBypass, $"{because}: AllowBypass maps");
        Check.Equal(policy.AllowDevToolAccess, config.AllowDevToolAccess, $"{because}: AllowDevToolAccess maps");
        Check.Equal(policy.SandboxMcpServers, config.SandboxMcpServers, $"{because}: SandboxMcpServers maps");
        Check.Equal(policy.SandboxLspServers, config.SandboxLspServers, $"{because}: SandboxLspServers maps");

        Check.Equal(policy.Auth?.Gh, config.Auth?.Gh, $"{because}: Auth.Gh maps");
        Check.Equal(policy.Auth?.Git, config.Auth?.Git, $"{because}: Auth.Git maps");
        Check.Equal(
            policy.Credentials?.EnvVars?.Count ?? 0,
            config.Credentials?.EnvVars?.Count ?? 0,
            $"{because}: every injected credential maps");
        Check.Equal(policy.KeychainAccess, config.UserPolicy?.Seatbelt?.KeychainAccess, $"{because}: Seatbelt.KeychainAccess maps");

        var filesystem = Check.NotNull(config.UserPolicy?.Filesystem, $"{because}: the filesystem policy is mapped");
        var expectedDenied = Expand(policy.Filesystem?.DeniedPaths, workingDirectory);
        var expectedReadonly = Expand(policy.Filesystem?.ReadonlyPaths, workingDirectory);
        var expectedReadwrite = Expand(policy.Filesystem?.ReadwritePaths, workingDirectory);
        Check.Sequence(expectedDenied, filesystem.DeniedPaths ?? [], $"{because}: DeniedPaths maps with the token expanded");
        Check.Sequence(expectedReadonly, filesystem.ReadonlyPaths ?? [], $"{because}: ReadonlyPaths maps with the token expanded");
        Check.Sequence(expectedReadwrite, filesystem.ReadwritePaths ?? [], $"{because}: ReadwritePaths maps with the token expanded");

        // The token is a durable-layer concept: it must never reach the wire type.
        foreach (var path in (filesystem.DeniedPaths ?? [])
                     .Concat(filesystem.ReadonlyPaths ?? [])
                     .Concat(filesystem.ReadwritePaths ?? []))
        {
            Check.That(
                !path.Contains(DurableSandboxPolicy.WorkspaceToken, StringComparison.Ordinal),
                $"{because}: no path still contains the workspace token ({path})");
        }
    }

    /// <summary>
    /// Asserts that the sandbox actually denied an access, and reports what it said.
    /// </summary>
    /// <remarks>
    /// Only meaningful in <see cref="SandboxRunMode.Enforced"/>: a host with no backend reports
    /// no denial because nothing was enforced, not because the policy was wrong.
    /// <para>
    /// The runtime classifies a denial by what the denied process tried to do, not by which
    /// path it named: <c>DeniedResource</c> is empty for a denial the runtime inferred from the
    /// command's output, so this asserts the classification and the enforcement point and
    /// prints the resource when the runtime did name one.
    /// </para>
    /// </remarks>
    public static void AssertDenied(
        IReadOnlyList<SandboxDecisionEvent> decisions,
        SandboxDenialClass denialClass,
        string because)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var denials = decisions
            .Select(decision => decision.Data)
            .OfType<SandboxDecisionDataAccessDenied>()
            .ToList();

        Check.That(denials.Count > 0, $"{because}: the sandbox reported at least one access denial");
        Check.That(
            denials.Any(denial => denial.DenialClass == denialClass),
            $"{because}: a denial is classified as {denialClass.Value} " +
            $"(saw: {string.Join(", ", denials.Select(denial => denial.DenialClass.Value))})");
        Check.That(
            denials.Any(denial => denial.Outcome == SandboxOutcome.Denied),
            $"{because}: a denial reports the {SandboxOutcome.Denied.Value} outcome");
    }

    /// <summary>
    /// Asserts that no permission event reported a denial for the given tool call.
    /// </summary>
    /// <remarks>
    /// This is the distinction the example exists to draw. The permission handler approved the
    /// tool call — the durable policy had nothing against it — and the sandbox denied the read
    /// itself. A denial in a permission event would mean the two layers disagreed.
    /// </remarks>
    public static void AssertNoPermissionDenial(
        IReadOnlyList<SessionEvent> events,
        string toolCallId,
        string because)
    {
        ArgumentNullException.ThrowIfNull(events);

        var completions = events
            .OfType<PermissionCompletedEvent>()
            .Where(completion => completion.Data.ToolCallId == toolCallId)
            .ToList();

        Check.That(completions.Count > 0, $"{because}: the tool call produced a permission completion");
        foreach (var completion in completions)
        {
            var kind = completion.Data.Result?.Kind ?? string.Empty;
            Check.That(
                !kind.Contains("denied", StringComparison.OrdinalIgnoreCase),
                $"{because}: the permission result is not a denial (saw: {kind})");
        }
    }

    /// <summary>Expands the workspace token in a path list, the way the harness does.</summary>
    public static List<string> Expand(IEnumerable<string>? paths, string workingDirectory) =>
        paths is null
            ? []
            : paths.Select(path => DurableSandboxPolicy.ExpandPath(path, workingDirectory)).ToList();

    /// <summary>Prints a labelled list, so an example's output shows the resolved policy.</summary>
    public static void PrintList(string label, IEnumerable<string>? values)
    {
        var list = values?.ToList() ?? [];
        Console.WriteLine($"  {label}: [{(list.Count == 0 ? "" : string.Join(", ", list))}]");
    }
}
