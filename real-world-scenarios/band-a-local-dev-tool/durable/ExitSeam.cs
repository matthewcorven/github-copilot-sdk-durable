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
    /// <param name="startedPhase">The phase the turn was sent for.</param>
    /// <param name="reachedPhase">The phase the document holds once the turn returned.</param>
    public static void BeforeAnswer(string startedPhase, string reachedPhase)
    {
        var target = Environment.GetEnvironmentVariable(AnswerVariable);
        if (string.IsNullOrWhiteSpace(target)) return;

        // Either name selects the kill. A caller naturally picks the phase it asked the model to
        // work on, and the turn usually moves the document somewhere else before answering, so
        // matching only the starting phase would make the seam inert for most turns.
        var matches = string.Equals(target, startedPhase, StringComparison.Ordinal)
            || string.Equals(target, reachedPhase, StringComparison.Ordinal);
        if (!matches) return;

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
    /// Here the turn <em>did</em> finish — the answer and the conversation's turn checkpoint are
    /// both durable — but the task's own checkpoint is still the one written before the prompt.
    /// That is the only state <see cref="EnquiryTask.SettledTurnAnswer"/> can act on, and it is
    /// reachable only on a turn that leaves a non-terminal document, which is why stages 6e-6g use
    /// the two-send fixture rather than the single-send one.
    /// </remarks>
    /// <param name="startedPhase">The phase the turn was sent for.</param>
    /// <param name="reachedPhase">The phase the document holds once the turn returned.</param>
    public static void AfterAnswer(string startedPhase, string reachedPhase)
    {
        var target = Environment.GetEnvironmentVariable(AnswerCommittedVariable);
        if (string.IsNullOrWhiteSpace(target)) return;

        // Match either name, for the same reason as BeforeAnswer: a turn usually moves the
        // document out of the phase it was sent for.
        var matches = string.Equals(target, startedPhase, StringComparison.Ordinal)
            || string.Equals(target, reachedPhase, StringComparison.Ordinal);
        if (!matches) return;

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
