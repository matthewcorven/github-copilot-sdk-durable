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

    /// <summary>Runs one enquiry, resuming from its checkpoint.</summary>
    public static async Task<TaskCommit> Run(
        DurableStore store,
        Func<EnquiryInput, string, CancellationToken, Task<string>> send,
        EnquiryInput input,
        EnquiryCheckpoint checkpoint,
        TaskRuntime runtime)
    {
        for (var turn = checkpoint.Turns; turn < MaxTurns; turn++)
        {
            var document = EnquiryDurable.Read(store, input.ConversationId);
            if (document.IsTerminal)
            {
                return await Finish(runtime, document);
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
                await EnquiryWrites.AppendAsync(store, input.ConversationId, "answer", answer);
            }

            // The turn boundary is committed as soon as the turn is over, before the driver's
            // transition. A process that dies here leaves a checkpoint naming the phase it was
            // working on, so the restart can tell "the prompt was sent" from "the prompt was
            // never sent" — which is the whole reason the count is durable.
            var boundary = new EnquiryCheckpoint
            {
                Phase = EnquiryPhases.Run,
                EnquiryPhase = phase,
                Turns = turn + 1,
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
                if (afterTurn.IsDone && afterTurn.Report is null && answer.Length > 0)
                {
                    // The model ended the enquiry with its phase tool and only then answered, so
                    // the answer is the report. It is attached to the terminal record rather
                    // than dropped.
                    await EnquiryWrites.AttachReportAsync(store, input.ConversationId, answer);
                }

                return await Finish(runtime, EnquiryDurable.Read(store, input.ConversationId));
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
                if (settled.IsDone && settled.Report is null && answer.Length > 0)
                {
                    // The model ended the enquiry with its phase tool before its answer existed,
                    // so the answer is the report. It is attached to the terminal record rather
                    // than dropped.
                    await EnquiryWrites.AttachReportAsync(store, input.ConversationId, answer);
                    settled = EnquiryDurable.Read(store, input.ConversationId);
                }

                return await Finish(runtime, settled);
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

    /// <summary>Commits the terminal outcome the document already reached.</summary>
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
