namespace BandC.Durable;

/// <summary>
/// The deterministic stop points: leave the process at a named phase boundary, or after a named
/// number of comments.
/// </summary>
/// <remarks>
/// <para>
/// Seams rather than signals, for the reason the Band A and Band B variants give: an arbitrary
/// <c>SIGKILL</c> demonstrates the store's crash safety, not what the run does afterwards, and it
/// cannot be repeated. Each seam here is a stop the store can describe, and each one is a different
/// question:
/// </para>
/// <list type="bullet">
/// <item><c>BAND_C_EXIT_AFTER_PHASE=comment</c> stops after the comment phase has posted every
/// comment and committed the run record at the next phase. The resume enters at the report phase, so
/// the phase pointer alone is what prevents a duplicate — the cheap case.</item>
/// <item><c>BAND_C_EXIT_AFTER_COMMENTS=n</c> stops inside the comment phase, after n comments exist
/// and before the phase commits. The resume re-enters the comment phase and must not re-post the n.
/// This is the case that needs the memo, and the one the issue asks for.</item>
/// <item><c>BAND_C_EXIT_AFTER_COMMENT_EFFECT=1</c> stops between the comment and its ledger record —
/// the window no memo can close, measured rather than argued about.</item>
/// </list>
/// <para>
/// <c>BAND_C_NO_IDEMPOTENCY_GUARD=1</c> is not a seam: it removes the guard, so the evidence can
/// show what the guard is worth. Every seam exits with <see cref="ExitCode"/>, and the exit is
/// <see cref="Environment.Exit"/> — no unwind, no finally, no flush the store did not already do.
/// </para>
/// </remarks>
public sealed class ExitSeam
{
    /// <summary>The process exit code every seam uses.</summary>
    public const int ExitCode = 42;

    /// <summary>The phase to stop after, or null.</summary>
    public string? Phase { get; private init; }

    /// <summary>How many comments to let through the comment phase, or null.</summary>
    public int? AfterComments { get; private init; }

    /// <summary>Whether to stop between the comment effect and its ledger record.</summary>
    public bool AfterCommentEffect { get; private init; }

    /// <summary>
    /// Whether the ledger is hidden from the comment step, leaving the memo as the only guard.
    /// </summary>
    /// <remarks>
    /// The control the mid-phase seam needs. Stage 4 stops inside phase 6 after seven comments; at
    /// that point the memo and the ledger both hold the seven, so a resume that skips seven has not
    /// shown *which* one did it. With the ledger hidden the memo is the only thing left, and the
    /// same seven are still skipped — which is what makes the memo the demonstrated guard rather than
    /// an incidental passenger. The ledger is not written by this process either, so a reader sees
    /// the memo carrying the whole run.
    /// </remarks>
    public bool LedgerHidden { get; private init; }

    /// <summary>
    /// Whether the comment step's guard is deliberately removed.
    /// </summary>
    /// <remarks>
    /// The control. With it set, the memo and the ledger are not read, so every entry into the
    /// comment phase re-posts: a re-run duplicates all thirteen, and a resume duplicates the ones
    /// already posted. The evidence runs the same commands with and without it, which is what makes
    /// the idempotency claim a measurement instead of a description.
    /// </remarks>
    public bool GuardDisabled { get; private init; }

    /// <summary>Reads the environment, or null when no seam is set.</summary>
    public static ExitSeam? FromEnvironment()
    {
        var phase = Environment.GetEnvironmentVariable("BAND_C_EXIT_AFTER_PHASE");
        var comments = Environment.GetEnvironmentVariable("BAND_C_EXIT_AFTER_COMMENTS");
        var effect = Environment.GetEnvironmentVariable("BAND_C_EXIT_AFTER_COMMENT_EFFECT");
        var guard = Environment.GetEnvironmentVariable("BAND_C_NO_IDEMPOTENCY_GUARD");
        var ledger = Environment.GetEnvironmentVariable("BAND_C_HIDE_LEDGER");

        var seam = new ExitSeam
        {
            Phase = string.IsNullOrWhiteSpace(phase) ? null : phase,
            AfterComments = string.IsNullOrWhiteSpace(comments)
                ? null
                : int.TryParse(comments, out var count) && count > 0
                    ? count
                    : throw new ArgumentException($"BAND_C_EXIT_AFTER_COMMENTS='{comments}' is not a positive number"),
            AfterCommentEffect = effect == "1",
            GuardDisabled = guard == "1",
            LedgerHidden = ledger == "1",
        };

        if (seam.Phase is not null && !Phases.Order.Contains(seam.Phase))
        {
            throw new ArgumentException(
                $"BAND_C_EXIT_AFTER_PHASE='{seam.Phase}' is not one of {string.Join(", ", Phases.Order)}");
        }

        return seam.Phase is null && seam.AfterComments is null && !seam.AfterCommentEffect
            && !seam.GuardDisabled && !seam.LedgerHidden
            ? null
            : seam;
    }

    /// <summary>Stops the process after <paramref name="completed"/> has committed.</summary>
    public void AfterPhase(string completed)
    {
        if (Phase is null || !string.Equals(completed, Phase, StringComparison.Ordinal))
        {
            return;
        }

        Exit($"stopped after phase '{completed}' (BAND_C_EXIT_AFTER_PHASE)");
    }

    /// <summary>Stops the process when the comment phase has posted the requested number.</summary>
    /// <returns>Whether the process stopped. Never returns true: the call does not come back.</returns>
    public void AfterNthComment(int posted)
    {
        if (AfterComments is not { } limit || posted < limit)
        {
            return;
        }

        Exit($"stopped inside phase 'comment' after {posted} comment(s) (BAND_C_EXIT_AFTER_COMMENTS)");
    }

    /// <summary>Stops the process between the comment itself and its ledger record.</summary>
    public void AfterCommentEffectWritten(int posted)
    {
        if (!AfterCommentEffect)
        {
            return;
        }

        Exit($"stopped between the comment and its ledger record, after {posted} comment(s) "
            + "(BAND_C_EXIT_AFTER_COMMENT_EFFECT)");
    }

    private static void Exit(string reason)
    {
        Console.WriteLine($"  seam:       {reason}, exit {ExitCode}");
        Console.Out.Flush();
        Environment.Exit(ExitCode);
    }
}
