using Durable;

namespace AgentOrchestrator.Durable;

/// <summary>The result of a guarded write to an enquiry document.</summary>
/// <param name="Written">Whether the write changed the document.</param>
/// <param name="Phase">The document's durable phase name after the call.</param>
/// <param name="Report">The document's report after the call.</param>
public readonly record struct PhaseWrite(bool Written, string Phase, string? Report)
{
    /// <summary>Whether the write found the document terminal or already advanced, and left it alone.</summary>
    public bool Skipped => !Written;
}

/// <summary>
/// Every write to an enquiry document goes through here.
/// </summary>
/// <remarks>
/// <para>
/// The baseline's defect 4 was an unprotected <c>Dictionary</c>: two enquiries mutated it from two
/// threads. Here every write takes the store's commit gate — <see cref="DurableStore.CommitAsync{T}"/>
/// serializes transactions across the store — and the guards below are evaluated <em>inside</em>
/// that gate, so no writer can slip between a read and the write it justifies.
/// </para>
/// <para>
/// There are two guards and they answer two different races:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Terminal is final.</b> A document that has reached a terminal phase is never moved, and
///     its report is never replaced. A late wakeup, a late tool call, and a phase that finished
///     after the enquiry was rejected all meet this check, so none can overwrite the decision that
///     ended the enquiry. This is requirement 3.
///     <para>
///     The check lives in every writer, not only in <see cref="MoveToAsync"/>: the phase tools
///     commit transitions, but a tool result and an intent report commit transcript entries, and
///     a late one of those would grow a terminal document without moving its phase. All of them
///     read <see cref="EnquiryState.IsTerminal"/> inside their own transaction.
///     </para>
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>The model's report is authoritative, and it may only move forward.</b> The model moves
///     the phase through <c>set_current_phase</c> while a turn runs, including skipping straight
///     to a rejection or to <c>done</c>. A report that would move the phase backwards is refused
///     and the refusal is returned to the model, so a replayed phase cannot rewind the enquiry.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>The driver's transition is compare-and-swap.</b> When the model has not moved the phase
///     itself, the driver advances it — but only if the document is still in the phase the turn
///     worked on. Two writers cannot both advance the same phase, because the second one's
///     expectation no longer holds.
///     </description>
///   </item>
/// </list>
/// </remarks>
public static class EnquiryWrites
{
    /// <summary>How long a rejected enquiry's card stays visible, matching the baseline's delay.</summary>
    public static readonly TimeSpan RejectionRetention = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Commits a phase transition on an enquiry document.
    /// </summary>
    /// <param name="store">The store that owns the document.</param>
    /// <param name="conversationId">The conversation that owns the document.</param>
    /// <param name="expected">The phase the caller believes the document is in.</param>
    /// <param name="phase">The phase to move to.</param>
    /// <param name="entryKind">The transcript entry kind that records the transition.</param>
    /// <param name="text">The transcript entry payload.</param>
    /// <param name="report">The report to attach, when the transition finishes the enquiry.</param>
    /// <returns>Whether the write happened, and the document's phase afterwards.</returns>
    public static Task<PhaseWrite> MoveToAsync(
        DurableStore store,
        string conversationId,
        string? expected,
        string phase,
        string entryKind = "phase",
        string? text = null,
        string? report = null) =>
        store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            var state = draft.Value;

            if (state.IsTerminal)
            {
                await Task.CompletedTask;
                return new PhaseWrite(false, EnquiryPhases.Name(state.Phase), state.Report);
            }

            if (expected is not null && EnquiryPhases.Name(state.Phase) != expected)
            {
                await Task.CompletedTask;
                return new PhaseWrite(false, EnquiryPhases.Name(state.Phase), state.Report);
            }

            var target = EnquiryPhases.From(phase);

            // A report naming the phase the enquiry is already in is a repeat, not a transition.
            if (target == state.Phase)
            {
                await Task.CompletedTask;
                return new PhaseWrite(false, EnquiryPhases.Name(state.Phase), state.Report);
            }

            // Backwards is refused. Forwards is allowed anywhere the model says it is, including
            // the skips to a rejection and the jump to done: the model is what judges the
            // enquiry, and the baseline never constrained that judgement either.
            if (!MayMoveForward(state.Phase, target))
            {
                await Task.CompletedTask;
                return new PhaseWrite(false, EnquiryPhases.Name(state.Phase), state.Report);
            }

            var at = DateTimeOffset.UtcNow;
            state.Phase = EnquiryPhases.From(phase);
            if (report is not null)
            {
                state.Report = report;
            }

            if (state.IsTerminal)
            {
                state.FinishedAt = at.ToString("O");
                if (state.IsRejected)
                {
                    // The baseline deleted the agent fifteen seconds later. The delay is
                    // committed data here and the record stays, so a restart still knows the
                    // enquiry was rejected and when its card stops showing.
                    state.HideAt = at.Add(RejectionRetention).ToString("O");
                }
            }

            var entry = tx.AppendEntry(conversationId, entryKind, text);
            state.Entries.Add(new EnquiryEntry
            {
                EntryId = entry,
                Kind = entryKind,
                Text = text,
                At = at.ToString("O"),
            });
            draft.Save(entry);

            await Task.CompletedTask;
            return new PhaseWrite(true, EnquiryPhases.Name(state.Phase), state.Report);
        });

    /// <summary>
    /// Whether a phase move goes forward in the pipeline.
    /// </summary>
    /// <remarks>
    /// The order is the pipeline's own row order: <c>Queued</c>, <c>Validating</c>,
    /// <c>Searching</c>, <c>WritingReport</c>, then any terminal phase. Moving to a later phase is
    /// allowed — the model may skip from validation straight to a rejection, which the baseline
    /// allowed too — and moving to an earlier one is refused, which is what stops a replayed
    /// phase from rewinding an enquiry that has moved on.
    /// </remarks>
    private static bool MayMoveForward(Phase from, Phase to) =>
        Rank(to) > Rank(from);

    /// <summary>The position of a phase in the pipeline, with terminal phases last.</summary>
    private static int Rank(Phase phase) => phase switch
    {
        Phase.Queued => 0,
        Phase.Validating => 1,
        Phase.Searching => 2,
        Phase.WritingReport => 3,
        Phase.RejectedGarbage => 4,
        Phase.RejectedNoMatches => 4,
        Phase.Done => 4,
        _ => 0,
    };

    /// <summary>
    /// Commits a phase transition and then applies the deterministic exit seam.
    /// </summary>
    /// <remarks>
    /// The seam is applied here, after the commit returns and before the caller can do anything
    /// else, so the phase the process exits on is guaranteed to be the phase the store holds.
    /// </remarks>
    public static async Task<PhaseWrite> MoveToAndMaybeExitAsync(
        DurableStore store,
        string conversationId,
        string? expected,
        string phase,
        string entryKind = "phase",
        string? text = null,
        string? report = null)
    {
        var write = await MoveToAsync(store, conversationId, expected, phase, entryKind, text, report);
        if (write.Written)
        {
            ExitSeam.AfterCommit(write.Phase);
        }

        return write;
    }

    /// <summary>
    /// Fills in the report of a completed enquiry without touching its phase.
    /// </summary>
    /// <remarks>
    /// A model that reports <c>done</c> through its phase tool ends the enquiry before its final
    /// answer exists, so the report arrives one commit later. This can only fill a null report on
    /// an enquiry that is already <c>done</c>: it never changes a phase, and it refuses an enquiry
    /// that ended any other way.
    /// <para>
    /// This is the one write that deliberately targets a terminal document — filling the report is
    /// its entire purpose — so it does not carry the <see cref="EnquiryState.IsTerminal"/> guard
    /// that the other writes do. Its own guard is narrower and still inside the commit: the
    /// document must be <c>done</c> and its report must still be null, so a second attach is a
    /// no-op and a rejected enquiry can never acquire a report.
    /// </para>
    /// </remarks>
    public static Task<PhaseWrite> AttachReportAsync(
        DurableStore store,
        string conversationId,
        string report) =>
        store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            var state = draft.Value;
            if (state.Phase != Phase.Done || state.Report is not null)
            {
                await Task.CompletedTask;
                return new PhaseWrite(false, EnquiryPhases.Name(state.Phase), state.Report);
            }

            state.Report = report;
            var entry = tx.AppendEntry(conversationId, "report", report);
            state.Entries.Add(new EnquiryEntry
            {
                EntryId = entry,
                Kind = "report",
                Text = report,
                At = DateTimeOffset.UtcNow.ToString("O"),
            });
            draft.Save(entry);

            await Task.CompletedTask;
            return new PhaseWrite(true, EnquiryPhases.Name(state.Phase), state.Report);
        });

    /// <summary>
    /// Appends the answer of the turn the task is currently running.
    /// </summary>
    /// <remarks>
    /// Deliberately not guarded on <see cref="EnquiryState.IsTerminal"/>, and deliberately not
    /// reachable from a tool. This is the one write that belongs to a turn that may itself have
    /// ended the enquiry: a model that reports <c>rejected_garbage</c> through its phase tool and
    /// then answers has produced both in the same turn, and dropping the answer would throw away
    /// the reason it gave. It cannot be a late write, because the task is still running: the store
    /// refuses any task commit after the terminal outcome, so this runs only while the turn that
    /// wrote it is in flight.
    /// </remarks>
    public static Task AppendTurnAnswerAsync(DurableStore store, string conversationId, string answer) =>
        store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            var state = draft.Value;
            var entry = tx.AppendEntry(conversationId, "answer", answer);
            state.Entries.Add(new EnquiryEntry
            {
                EntryId = entry,
                Kind = "answer",
                Text = answer,
                At = DateTimeOffset.UtcNow.ToString("O"),
            });
            draft.Save(entry);

            await Task.CompletedTask;
        });

    /// <summary>
    /// Appends a note the task writes about its own work, such as a lost deliverable.
    /// </summary>
    /// <remarks>
    /// A note is written <em>after</em> the enquiry reached a terminal phase — it records what
    /// happened to a finished enquiry — so it does not carry the terminal guard. It is not the
    /// model's transcript and cannot inflate it: the recovery path appends at most one of these
    /// per outcome, and the outcome is guarded by the task's own memo.
    /// </remarks>
    public static Task AppendNoteAsync(DurableStore store, string conversationId, string text) =>
        store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            var state = draft.Value;
            var entry = tx.AppendEntry(conversationId, "note", text);
            state.Entries.Add(new EnquiryEntry
            {
                EntryId = entry,
                Kind = "note",
                Text = text,
                At = DateTimeOffset.UtcNow.ToString("O"),
            });
            draft.Save(entry);

            await Task.CompletedTask;
        });

    /// <summary>Appends a transcript entry that is not a phase transition.</summary>
    /// <remarks>
    /// The terminal guard is here, inside the commit, not at the call site: a tool result that
    /// arrives after the enquiry ended is exactly the late write requirement 3 is about, and a
    /// pre-check outside the transaction would let it slip between the read and the write.
    /// </remarks>
    /// <returns>Whether the entry was written; a terminal document leaves its transcript alone.</returns>
    public static Task<bool> AppendAsync(
        DurableStore store,
        string conversationId,
        string kind,
        string? text,
        string? toolName = null,
        bool? success = null) =>
        store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            var state = draft.Value;
            if (state.IsTerminal)
            {
                await Task.CompletedTask;
                return false;
            }

            var entry = tx.AppendEntry(conversationId, kind, text);
            state.Entries.Add(new EnquiryEntry
            {
                EntryId = entry,
                Kind = kind,
                Text = text,
                ToolName = toolName,
                Success = success,
                At = DateTimeOffset.UtcNow.ToString("O"),
            });
            draft.Save(entry);

            await Task.CompletedTask;
            return true;
        });

    /// <summary>The intent the model last reported, committed as its own entry.</summary>
    /// <remarks>
    /// Guarded inside the commit for the same reason as <see cref="AppendAsync"/>: an intent
    /// reported by a turn that outlived a rejection must not write into the terminal record.
    /// </remarks>
    /// <returns>Whether the intent was written; a terminal document keeps the intent it had.</returns>
    public static Task<bool> ReportIntentAsync(DurableStore store, string conversationId, string intent) =>
        store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            var state = draft.Value;
            if (state.IsTerminal)
            {
                await Task.CompletedTask;
                return false;
            }

            state.Intent = intent;
            var entry = tx.AppendEntry(conversationId, "intent", intent);
            state.Entries.Add(new EnquiryEntry
            {
                EntryId = entry,
                Kind = "intent",
                Text = intent,
                At = DateTimeOffset.UtcNow.ToString("O"),
            });
            draft.Save(entry);

            await Task.CompletedTask;
            return true;
        });

    /// <summary>Records that this process re-entered an interrupted enquiry.</summary>
    /// <remarks>
    /// This writes the restart counter and an entry that is not part of the enquiry's transcript
    /// semantics, so it is deliberately <em>not</em> guarded on the terminal phase: the point of
    /// the record is that a process picked the enquiry up, including a resume that then finds it
    /// already finished.
    /// </remarks>
    public static Task NoteRestartAsync(DurableStore store, string conversationId) =>
        store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversationId);
            var state = draft.Value;
            state.Restarts++;
            draft.Save(tx.AppendEntry(conversationId, "restart"));
            await Task.CompletedTask;
        });
}
