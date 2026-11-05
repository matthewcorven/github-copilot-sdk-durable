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
    public static async Task<TaskCommit> Run(
        DurableStore store,
        Func<EnquiryInput, string, CancellationToken, Task<string>> send,
        EnquiryInput input,
        EnquiryCheckpoint checkpoint,
        TaskRuntime runtime)
    {
        // The answer the previous process was holding when it died. Re-reading the document is
        // not enough to recover it: the model had reported done, the process was killed before the
        // answer was committed, and nothing on the document holds it.
        var carried = checkpoint.Answer;

        for (var turn = checkpoint.Turns; turn < MaxTurns; turn++)
        {
            var document = EnquiryDurable.Read(store, input.ConversationId);
            if (document.IsTerminal)
            {
                return await Finish(runtime, await RecoverAsync(store, send, input, document, carried, runtime));
            }

            var phase = EnquiryPhases.Name(document.Phase);
            string answer;
            try
            {
                answer = await send(input, PromptFor(phase, input.Enquiry), runtime.CancellationToken);
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
            }

            // The turn boundary is committed as soon as the turn is over, before the driver's
            // transition. The checkpoint names the phase the turn worked on and carries the
            // answer it produced, so a process that dies before the answer is committed can
            // still attach it rather than losing it.
            //
            // What this boundary does *not* claim is that the turn is finished: the phase the
            // document holds is the source of truth for what the model actually did, and a turn
            // whose turn-entry commit was interrupted is re-run. That is the documented cost of
            // this design, not a guarantee it can make.
            var boundary = new EnquiryCheckpoint
            {
                Phase = EnquiryPhases.Run,
                EnquiryPhase = phase,
                Turns = turn + 1,
                Answer = answer,
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
    /// What a terminal document still owes its caller before the task may finish.
    /// </summary>
    /// <remarks>
    /// A <c>done</c> enquiry has exactly one deliverable: the report. A process killed between the
    /// model reporting <c>done</c> and the answer being committed leaves a terminal document with
    /// <see cref="EnquiryState.Report"/> null, and the resumed task would otherwise commit
    /// <c>completed</c> with no report at all — a lost enquiry that still looks successful. The
    /// checkpoint's answer covers a kill after a turn boundary; it does not cover a kill inside
    /// the tool call that reported <c>done</c>, so both cases are handled here.
    /// </remarks>
    private static async Task<EnquiryState> RecoverAsync(
        DurableStore store,
        Func<EnquiryInput, string, CancellationToken, Task<string>> send,
        EnquiryInput input,
        EnquiryState document,
        string? carried,
        TaskRuntime runtime)
    {
        if (!document.IsDone || document.Report is { Length: > 0 })
        {
            return document;
        }

        // The checkpoint's answer is the cheapest recovery: it is the answer the dead process was
        // already holding, so nothing has to be asked for again.
        if (carried is { Length: > 0 })
        {
            await EnquiryWrites.AttachReportAsync(store, input.ConversationId, carried);
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
    /// <param name="carried">
    /// The answer an interrupted process held in its checkpoint, or null when this process ran the
    /// turn. It is the last resort for a report the document never got.
    /// </param>
    private static Task<TaskCommit> Finish(TaskRuntime runtime, EnquiryState document, string? carried = null)
    {
        // A process killed between the model reporting done and the answer committing leaves a
        // terminal document with no report and nothing else on it that holds the answer. The
        // checkpoint is the only place it survives — empty at the moment of the kill, because the
        // commit never happened, and filled here when the boundary carried it forward.
        var report = document.Report ?? (document.IsDone && carried is { Length: > 0 } ? carried : null);
        return runtime.CommitAsync(() => TaskCommit.Completed(DurableJson.ToElement(new EnquiryOutcome
        {
            Phase = EnquiryPhases.Name(document.Phase),
            Report = report,
        })));
    }

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
