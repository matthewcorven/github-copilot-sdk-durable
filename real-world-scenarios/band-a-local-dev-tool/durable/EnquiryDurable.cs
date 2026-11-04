using Durable;

namespace AgentOrchestrator.Durable;

/// <summary>
/// The durable half of the Band A scenario: the enquiry document, the enquiry task, and the
/// function that builds the task definition for one process.
/// </summary>
/// <remarks>
/// Everything in this file is durability, not application. It defines what a checkpoint is, where
/// the phase boundary sits, and how a restarted task reaches the session it belongs to.
/// </remarks>
public static class EnquiryDurable
{
    /// <summary>The document kind that holds one enquiry's committed state.</summary>
    public const string DocumentKind = "band-a.enquiry";

    /// <summary>The task name the enquiry state machine is registered under.</summary>
    public const string TaskName = "band-a.enquiry";

    /// <summary>The extension that carries the enquiry task.</summary>
    public const string ExtensionName = "band-a";

    /// <summary>
    /// The enquiry document.
    /// </summary>
    /// <remarks>
    /// <see cref="DocumentHistory.Latest"/> is the right history for this document because the UI
    /// only ever shows the current state, and every committed phase is also a transcript entry.
    /// <see cref="ForkPolicy.Current"/> keeps a fork's copy independent of its parent's later
    /// writes, which is the property a forked enquiry needs.
    /// </remarks>
    public static readonly DocumentDefinition<EnquiryState> Enquiry =
        new(DocumentKind, DocumentHistory.Latest, ForkPolicy.Current, () => new EnquiryState());

    /// <summary>
    /// Builds the enquiry task for one process.
    /// </summary>
    /// <param name="store">The store the task reads and writes documents through.</param>
    /// <param name="send">
    /// Sends a phase prompt to the enquiry's conversation and returns the model's answer. It is
    /// supplied by the host because a live session is this process's resource, not durable state:
    /// a task restored in a new process reaches its session through this delegate, which is the
    /// process that resumed it.
    /// </param>
    /// <returns>The task definition to register.</returns>
    /// <remarks>
    /// The definition is built per process rather than once, because the closure is the whole
    /// point: the same task name, the same phases, and the same checkpoint resolve in a new
    /// process, and only the session it talks to is new.
    /// </remarks>
    public static DurableTaskDefinition Task(
        DurableStore store,
        Func<EnquiryInput, string, CancellationToken, Task<string>> send) =>
        DurableTask.Define<EnquiryInput, EnquiryCheckpoint, EnquiryOutcome>(
            TaskName,
            () => new EnquiryCheckpoint { Phase = EnquiryPhases.Run, EnquiryPhase = EnquiryPhases.Queued },
            (input, checkpoint, runtime) => EnquiryTask.Run(store, send, input, checkpoint, runtime),
            (_, checkpoint, runtime) => runtime.CommitAsync(() => TaskCommit.Aborted()));

    /// <summary>
    /// Reads an enquiry's committed state, or an empty one when it has never been written.
    /// </summary>
    public static EnquiryState Read(DurableStore store, string conversationId) =>
        store.Document(DocumentKind, conversationId)?.Latest is { } latest
            ? latest.Deserialize<EnquiryState>(DurableJson.Options) ?? new EnquiryState()
            : new EnquiryState();
}
