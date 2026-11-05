using Durable;

namespace AgentOrchestrator.Durable;

/// <summary>
/// The enquiry state machine: one task phase that loops over the enquiry's durable phase.
/// </summary>
/// <remarks>
/// <para>
/// Each iteration runs the current phase's model turn and commits the next phase together with the
/// turn's transcript entry. That commit <em>is</em> the checkpoint, so a process that dies in a
/// turn resumes at the same phase and a process that dies after the commit resumes at the next
/// one. A restart therefore continues the enquiry instead of starting it again.
/// </para>
/// <para>
/// The loop stops on a terminal document. That is what a rejection is: the model reports a
/// rejected phase through <c>set_current_phase</c> while a turn is in flight, the write lands in
/// the document, and the turn's own transition then meets the terminal guard and is dropped. A
/// late wakeup cannot overwrite it for the same reason — the guard is evaluated inside the store's
/// commit gate.
/// </para>
/// </remarks>
public static class EnquiryTask
{
    /// <summary>How many model turns one enquiry may run before it is failed.</summary>
    /// <remarks>
    /// The model is told to stop at a terminal phase. This bound is the honest backstop for a
    /// model that does not: without it the loop would dispatch prompts indefinitely, which is the
    /// one thing a durable task must never do.
    /// </remarks>
    public const int MaxTurns = 8;

    /// <summary>The task memo that records a recovery attempt, so it happens at most once.</summary>
    public const string RecoveryMemoKey = "report-recovery";

    private const string RecoveryAttempted = "attempted";
    private const string RecoveryRecovered = "recovered";
    private const string RecoveryFailed = "failed";

    /// <summary>Runs one enquiry, resuming from its checkpoint.</summary>
    /// <param name="store">The store that owns the document.</param>
    /// <param name="send">Sends a phase prompt and returns the model's answer.</param>
    /// <param name="input">The enquiry this task belongs to.</param>
    /// <param name="checkpoint">The task's checkpoint, as it stood when this process started.</param>
    /// <param name="runtime">The task runtime that commits.</param>
    /// <param name="conversationCheckpoint">
    /// Reads the conversation's most recent checkpoint, or null when there is none. This is what a
    /// resume uses to tell a finished turn from a started one; see
    /// <see cref="SettledTurnAnswer"/>.
    /// </param>
    public static async Task<TaskCommit> Run(
        DurableStore store,
        Func<EnquiryInput, string, CancellationToken, Task<string>> send,
        EnquiryInput input,
        EnquiryCheckpoint checkpoint,
        TaskRuntime runtime,
        Func<ConversationCheckpoint?>? conversationCheckpoint = null)
    {
        for (var turn = checkpoint.Turns; turn < MaxTurns; turn++)
        {
            var document = EnquiryDurable.Read(store, input.ConversationId);
            if (document.IsTerminal)
            {
                return await Finish(runtime, await RecoverAsync(store, send, input, document, runtime));
            }

            var phase = EnquiryPhases.Name(document.Phase);

            // A turn that was interrupted mid-flight is not re-sent if the conversation shows it
            // finished. Resuming re-enters the loop at the checkpoint's turn index and the
            // document's phase, and both of those were already true when the turn began — so the
            // only evidence that separates "finished" from "started" is the conversation's own
            // checkpoint, which the layer writes when a turn's answer commits.
            var resumedAnswer = SettledTurnAnswer(checkpoint, phase, conversationCheckpoint?.Invoke());
            if (resumedAnswer is not null)
            {
                return await ResolveSettledTurnAsync(store, input, resumedAnswer, turn, runtime);
            }

            // Record what the conversation had before this turn's prompt goes out. A process that
            // dies during the turn leaves this on the checkpoint, and the next process compares it
            // against what the conversation holds then.
            var started = new EnquiryCheckpoint
            {
                Phase = EnquiryPhases.Run,
                EnquiryPhase = phase,
                Turns = turn,
                SeenCheckpoint = conversationCheckpoint?.Invoke()?.EventId,
            };
            var startedCommit = await runtime.CommitAsync(
                () => TaskCommit.Running(DurableJson.ToElement(started)));
            if (startedCommit.Outcome is not null)
            {
                return startedCommit;
            }

            string answer;
            try
            {
                answer = await send(input, PromptFor(phase, input.Enquiry), runtime.CancellationToken);
                ExitSeam.BeforeAnswer(phase);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A turn that throws ends the enquiry as failed, and the failure is committed: a
                // restart must not retry a prompt whose effect it cannot observe.
                return await runtime.CommitAsync(() => TaskCommit.Failed(error.Message));
            }

            if (answer.Length > 0)
            {
                await EnquiryWrites.AppendTurnAnswerAsync(store, input.ConversationId, answer);
                ExitSeam.AfterAnswer(phase);
            }

            // The settled boundary, committed once the turn's answer is durable. It advances the
            // turn count, so a restart re-enters at the next turn rather than this one.
            var boundary = new EnquiryCheckpoint
            {
                Phase = EnquiryPhases.Run,
                EnquiryPhase = phase,
                Turns = turn + 1,
                SeenCheckpoint = conversationCheckpoint?.Invoke()?.EventId,
            };
            var boundaryCommit = await runtime.CommitAsync(
                () => TaskCommit.Running(DurableJson.ToElement(boundary)));
            if (boundaryCommit.Outcome is not null)
            {
                return boundaryCommit;
            }

            // Re-read after the turn: the model's tool calls commit phase transitions while the
            // turn runs, so the document — not the checkpoint — is where the enquiry is now.
            var afterTurn = EnquiryDurable.Read(store, input.ConversationId);
            if (afterTurn.IsTerminal)
            {
                return await Finish(runtime, await AttachAnswerAsReportAsync(store, input.ConversationId, answer));
            }

            // The model normally moved the phase itself through its tool calls, so the driver
            // only advances when the phase is unchanged. Both writes are the same guarded edge:
            // a phase that already moved on, or a rejection that landed, drops this transition
            // and the loop re-reads the document instead of forcing it back.
            var next = EnquiryPhases.Next(phase);
            _ = await EnquiryWrites.MoveToAndMaybeExitAsync(store, input.ConversationId, phase, next);

            // The document — not the driver's write — decides where the enquiry is. The model may
            // have moved it during the turn, in which case the driver's transition is a no-op;
            // either way the checkpoint records the document's phase, so a restart resumes where
            // the store says the enquiry got to.
            var settled = EnquiryDurable.Read(store, input.ConversationId);
            if (settled.IsTerminal)
            {
                return await Finish(runtime, await AttachAnswerAsReportAsync(store, input.ConversationId, answer));
            }

            var advanced = new EnquiryCheckpoint
            {
                Phase = EnquiryPhases.Run,
                EnquiryPhase = EnquiryPhases.Name(settled.Phase),
                Turns = turn + 1,
            };

            var committed = await runtime.CommitAsync(
                () => TaskCommit.Running(DurableJson.ToElement(advanced)));

            // A task that became terminal while the iteration ran has had the last word, so the
            // loop must not continue past it.
            if (committed.Outcome is not null)
            {
                return committed;
            }
        }

        return await runtime.CommitAsync(() => TaskCommit.Failed(
            $"the enquiry did not reach a terminal phase in {MaxTurns} turns"));
    }

    /// <summary>
    /// The answer of a turn the previous process finished but never recorded, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resume re-enters the loop at the checkpoint's turn index and the document's phase, and
    /// both were already true when the interrupted turn sent its prompt — so neither can say
    /// whether that turn ran. The conversation can:
    /// <see cref="DurableConversation.Checkpoint"/> returns the most recent
    /// <c>turn:&lt;messageId&gt;</c> checkpoint, and the task records the one it saw before it
    /// sent. A different, non-null checkpoint now means the turn finished, and its content is the
    /// answer it produced.
    /// </para>
    /// <para>
    /// This is what stops a resume at a turn boundary from re-sending the turn. It depends on the
    /// layer having committed the turn checkpoint before the process died, which
    /// <c>SubmitAsync</c>'s completion barrier guarantees once the answer has arrived.
    /// </para>
    /// </remarks>
    private static string? SettledTurnAnswer(
        EnquiryCheckpoint checkpoint,
        string phase,
        ConversationCheckpoint? conversation)
    {
        if (conversation is not { Kind: "turn" } || conversation.Content is not { Length: > 0 } content)
        {
            return null;
        }

        // Only a turn that was started for this phase can have finished it. A checkpoint whose
        // phase differs is the settled boundary of an earlier turn, which the loop already steps
        // past.
        if (checkpoint.EnquiryPhase != phase)
        {
            return null;
        }

        // Nothing was observed before the prompt, so there is no baseline to compare against.
        if (checkpoint.SeenCheckpoint is not { Length: > 0 } seen || seen == conversation.EventId)
        {
            return null;
        }

        return content;
    }

    /// <summary>
    /// Records the answer of a turn the previous process finished, then carries on from it.
    /// </summary>
    /// <remarks>
    /// The answer was produced by a turn that already ran, so it is written where that turn's
    /// answer belongs — the transcript — and the loop then continues down the path the interrupted
    /// process would have taken. Nothing is sent to the model.
    /// </remarks>
    private static async Task<TaskCommit> ResolveSettledTurnAsync(
        DurableStore store,
        EnquiryInput input,
        string answer,
        int turn,
        TaskRuntime runtime)
    {
        await EnquiryWrites.AppendTurnAnswerAsync(store, input.ConversationId, answer);

        var settled = EnquiryDurable.Read(store, input.ConversationId);
        if (settled.IsTerminal)
        {
            return await Finish(runtime, await RecoverAsync(store, null, input, settled, runtime));
        }

        // The turn is over, so the settled boundary advances the count: a later restart re-enters
        // at the next turn, exactly as it would after an uninterrupted one.
        var advanced = new EnquiryCheckpoint
        {
            Phase = EnquiryPhases.Run,
            EnquiryPhase = EnquiryPhases.Name(settled.Phase),
            Turns = turn + 1,
        };
        return await runtime.CommitAsync(() => TaskCommit.Running(DurableJson.ToElement(advanced)));
    }

    /// <summary>
    /// What a terminal document still owes its caller before the task may finish.
    /// </summary>
    /// <remarks>
    /// A <c>done</c> enquiry has exactly one deliverable: the report. A process killed between the
    /// model reporting <c>done</c> and the answer being committed leaves a terminal document with
    /// <see cref="EnquiryState.Report"/> null, and the resumed task would otherwise commit
    /// <c>completed</c> with no report at all — a lost enquiry that still looks successful. Two
    /// cases reach here: the answer did commit (it is in the transcript, and is attached as is),
    /// and the answer did not (the model is asked once, and what comes back is a new answer).
    /// </remarks>
    private static async Task<EnquiryState> RecoverAsync(
        DurableStore store,
        Func<EnquiryInput, string, CancellationToken, Task<string>>? send,
        EnquiryInput input,
        EnquiryState document,
        TaskRuntime runtime)
    {
        if (!document.IsDone || document.Report is { Length: > 0 })
        {
            return document;
        }

        // The cheapest recovery needs no model: the turn that reported done may already have
        // committed its answer, in which case the transcript holds the deliverable and it is
        // simply attached. Stage 6c/6d take this path; stage 5e takes the one below.
        if (LastAnswer(document) is { Length: > 0 } written)
        {
            await EnquiryWrites.AttachReportAsync(store, input.ConversationId, written);
            await EnquiryWrites.AppendNoteAsync(
                store, input.ConversationId, "the report was recovered from the answer already committed");
            return EnquiryDurable.Read(store, input.ConversationId);
        }

        // Nothing durable holds the deliverable, so it has to be asked for. The attempt is
        // recorded in the task's own memo *before* the prompt goes out, and the outcome replaces
        // it afterwards. A process killed during the recovery therefore resumes with the memo
        // already set and does not dispatch a second prompt, and a second resume of a finished
        // recovery adds nothing to the transcript.
        if (await runtime.MemoAsync(RecoveryMemoKey) is not null)
        {
            return document;
        }

        await runtime.MemoAsync(RecoveryMemoKey, DurableJson.ToElement(EnquiryTask.RecoveryAttempted));
        await EnquiryWrites.AppendNoteAsync(
            store, input.ConversationId, "the report was lost; asking the model for it again");

        if (send is null)
        {
            // Nothing can be asked for. The enquiry stays finished with no report, and the note
            // says so — the outcome is still honest about having lost its deliverable.
            await EnquiryWrites.AppendNoteAsync(
                store, input.ConversationId, "the report was lost and could not be recovered");
            return EnquiryDurable.Read(store, input.ConversationId);
        }

        try
        {
            var answer = await send(
                input,
                $"<enquiry>{input.Enquiry}</enquiry>\n<phase>done</phase>\n" +
                "This enquiry is already marked done but its report was never saved. Write the " +
                "salesperson's report again now, exactly as before.",
                runtime.CancellationToken);

            if (answer.Length > 0)
            {
                // The attach and the note land in document reads that follow, so the memo is set
                // last: a resume between them re-enters with the memo set and simply re-finishes.
                await EnquiryWrites.AttachReportAsync(store, input.ConversationId, answer);
                await EnquiryWrites.AppendNoteAsync(store, input.ConversationId, "the report was recovered");
                await runtime.MemoAsync(RecoveryMemoKey, DurableJson.ToElement(RecoveryRecovered));
            }
            else
            {
                await EnquiryWrites.AppendNoteAsync(
                    store, input.ConversationId, "the report could not be re-derived");
                await runtime.MemoAsync(RecoveryMemoKey, DurableJson.ToElement(RecoveryFailed));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The report cannot be re-derived. The enquiry is still finished, so the terminal
            // outcome stands; saying so in the record is the honest result.
            await EnquiryWrites.AppendNoteAsync(
                store, input.ConversationId, $"the report could not be recovered: {error.Message}");
            await runtime.MemoAsync(RecoveryMemoKey, DurableJson.ToElement(RecoveryFailed));
        }

        return EnquiryDurable.Read(store, input.ConversationId);
    }

    /// <summary>
    /// Makes the turn's answer the report of a <c>done</c> enquiry that has none yet.
    /// </summary>
    /// <remarks>
    /// A model that reports <c>done</c> through its phase tool ends the enquiry before its final
    /// answer exists, so the answer arrives one commit later and is written onto the already
    /// terminal document. It can only fill a null report on an enquiry that ended in
    /// <c>done</c>: <see cref="EnquiryWrites.AttachReportAsync"/> refuses every other terminal
    /// phase, so a rejection never acquires a report and a second attach is a no-op.
    /// </remarks>
    private static async Task<EnquiryState> AttachAnswerAsReportAsync(
        DurableStore store,
        string conversationId,
        string answer)
    {
        await EnquiryWrites.AttachReportAsync(store, conversationId, answer);
        return EnquiryDurable.Read(store, conversationId);
    }

    /// <summary>Commits the terminal outcome the document already reached.</summary>
    /// <param name="runtime">The task runtime that commits the outcome.</param>
    /// <param name="document">The terminal document.</param>
    /// <summary>The text of the most recent committed answer, or null when there is none.</summary>
    private static string? LastAnswer(EnquiryState document) =>
        document.Entries.LastOrDefault(entry => entry.Kind == "answer" && entry.Text is { Length: > 0 })?.Text;

    private static Task<TaskCommit> Finish(TaskRuntime runtime, EnquiryState document) =>
        runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(new EnquiryOutcome
        {
            Phase = EnquiryPhases.Name(document.Phase),
            Report = document.Report,
        })));

    /// <summary>The prompt one phase sends.</summary>
    /// <remarks>
    /// The system prompt already describes the pipeline and tells the model to report each phase
    /// through <c>set_current_phase</c>, so a phase prompt is a short instruction naming the phase
    /// to work on, not a second copy of the instructions. It is derived from the committed phase,
    /// never from what the previous process was doing, which is what makes a restart send the
    /// right prompt.
    /// </remarks>
    public static string PromptFor(string phase, string enquiry) => phase switch
    {
        EnquiryPhases.Searching =>
            $"<enquiry>{enquiry}</enquiry>\n<phase>searching</phase>\n" +
            "The enquiry is genuine. Search the listings now, and finish this phase by either " +
            "writing the report or setting \"rejected_no_matches\".",

        EnquiryPhases.WritingReport =>
            $"<enquiry>{enquiry}</enquiry>\n<phase>writing_report</phase>\n" +
            "You have matching properties. Write the salesperson's report now.",

        // Queued and Validating share the opening prompt: validation is the model's judgement of
        // the enquiry text, so the text is the whole prompt.
        _ => $"<enquiry>{enquiry}</enquiry>",
    };
}
