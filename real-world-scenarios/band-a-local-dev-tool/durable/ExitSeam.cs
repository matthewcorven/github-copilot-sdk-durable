namespace AgentOrchestrator.Durable;

/// <summary>
/// The deterministic kill point.
/// </summary>
/// <remarks>
/// <para>
/// <c>BAND_A_EXIT_AFTER_PHASE=&lt;phase&gt;</c> makes the process stop immediately after that phase
/// is committed, and before anything else runs. It is not a timer and not a race: the exit is
/// checked on the commit path, so the store is guaranteed to hold the phase the process died
/// after. That is the seam the restart evidence uses to kill the process mid-enquiry at a chosen
/// phase.
/// </para>
/// <para>
/// The exit is a real process exit, not an exception a handler could catch, because the point of
/// the seam is to reproduce a process that is gone: no finally blocks, no disposal, no flushing
/// except what the store already did. The store's own commit flushed before this runs, which is
/// exactly the durability property being demonstrated.
/// </para>
/// </remarks>
public static class ExitSeam
{
    /// <summary>The environment variable that names the phase to exit after.</summary>
    public const string PhaseVariable = "BAND_A_EXIT_AFTER_PHASE";

    /// <summary>The exit code the harness uses, so a caller can tell a seam exit from a crash.</summary>
    public const int ExitCode = 42;

    /// <summary>Stops the process when <paramref name="phase"/> is the phase to exit after.</summary>
    public static void AfterCommit(string phase)
    {
        var target = Environment.GetEnvironmentVariable(PhaseVariable);
        if (string.IsNullOrWhiteSpace(target) || !string.Equals(target, phase, StringComparison.Ordinal))
        {
            return;
        }

        Console.WriteLine(
            $"{PhaseVariable}={target}: committed, exiting the process mid-enquiry (exit code {ExitCode})");
        Console.Out.Flush();
        Environment.Exit(ExitCode);
    }
}
