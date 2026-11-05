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

    /// <summary>The environment variable that names the kill point just after a phase's answer.</summary>
    public const string AnswerVariable = "BAND_A_EXIT_BEFORE_ANSWER";

    /// <summary>
    /// Stops the process after <paramref name="phase"/>'s turn returned and before its answer is
    /// committed.
    /// </summary>
    /// <remarks>
    /// This is the window between "the phase is durable" and "the turn's answer is durable". A
    /// process killed here leaves a terminal document whose answer never reached the transcript,
    /// which is the window <see cref="Durable.EnquiryTask"/>'s transcript recovery exists for. It
    /// is a second seam rather than a variant of <see cref="AfterCommit"/> because the interesting
    /// fact is not which phase committed, it is that the answer did not.
    /// </remarks>
    public static void BeforeAnswer(string phase)
    {
        var target = Environment.GetEnvironmentVariable(AnswerVariable);
        if (string.IsNullOrWhiteSpace(target) || !string.Equals(target, phase, StringComparison.Ordinal))
        {
            return;
        }

        Console.WriteLine(
            $"{AnswerVariable}={target}: the turn returned and the process exits before its answer " +
            $"is committed (exit code {ExitCode})");
        Console.Out.Flush();
        Environment.Exit(ExitCode);
    }

    /// <summary>The environment variable that names the kill point just after a phase's answer is committed.</summary>
    public const string AnswerCommittedVariable = "BAND_A_EXIT_AFTER_ANSWER";

    /// <summary>
    /// Stops the process after <paramref name="phase"/>'s answer is committed and before the
    /// task's boundary is.
    /// </summary>
    /// <remarks>
    /// The mirror image of <see cref="BeforeAnswer"/>. Here the turn <em>did</em> finish — the
    /// answer and the conversation's turn checkpoint are both durable — so a resume can recognise
    /// it and continue without sending the prompt again. That is the case
    /// <see cref="EnquiryTask.SettledTurnAnswer"/> exists for; stage 6e exercises it.
    /// </remarks>
    public static void AfterAnswer(string phase)
    {
        var target = Environment.GetEnvironmentVariable(AnswerCommittedVariable);
        if (string.IsNullOrWhiteSpace(target) || !string.Equals(target, phase, StringComparison.Ordinal))
        {
            return;
        }

        Console.WriteLine(
            $"{AnswerCommittedVariable}={target}: the answer is committed and the process exits " +
            $"before the task boundary (exit code {ExitCode})");
        Console.Out.Flush();
        Environment.Exit(ExitCode);
    }

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
