using Durable;

namespace AgentOrchestrator.Durable;

/// <summary>
/// The application object: the enquiries the UI shows, read from the store.
/// </summary>
/// <remarks>
/// <para>
/// The baseline's <c>AppState</c> held a <c>Dictionary&lt;string, Agent&gt;</c> and each
/// <c>Agent</c> held its own phase, intent, and transcript. Here the dictionary is gone: the
/// store is the only place an enquiry's state exists, and this class is a read-only projection of
/// it for the renderer. That is the difference the scenario exists to show — there is nothing to
/// lose on restart because there is nothing held in memory.
/// </para>
/// <para>
/// Nothing here is authoritative. A renderer read that raced a commit misses the commit; the next
/// notification — or the next poll, or a restart — reads the store again and sees it. That is the
/// opposite of the baseline, where the dictionary <em>was</em> the state and a lost write was a
/// lost enquiry.
/// </para>
/// </remarks>
public sealed class AppState
{
    private readonly EnquiryStore _store;

    public AppState(EnquiryStore store)
    {
        _store = store;
    }

    /// <summary>Raised when the store changed and the renderer should re-read it.</summary>
    public event Action? UpdateUi;

    /// <summary>The durable store the UI reads.</summary>
    public DurableStore Store => _store.Store;

    /// <summary>Every enquiry the store holds, oldest first.</summary>
    public List<EnquiryState> Enquiries() =>
        Store.Documents
            .Where(document => document.Kind == EnquiryDurable.DocumentKind)
            .Select(document => EnquiryDurable.Read(Store, document.OwnerId))
            .Where(state => state.Enquiry.Length > 0)
            .OrderBy(state => state.StartedAt, StringComparer.Ordinal)
            .ToList();

    /// <summary>One enquiry by its conversation id.</summary>
    public EnquiryState? Find(string conversationId) =>
        EnquiryDurable.Read(Store, conversationId) is { Enquiry.Length: > 0 } state ? state : null;

    /// <summary>Starts a new enquiry and returns the conversation id it was created under.</summary>
    public async Task<string> RunAgentAsync(string enquiry)
    {
        var enquiryId = Guid.NewGuid().ToString("N")[..8];
        await _store.CreateEnquiryAsync(enquiryId, enquiry);
        NotifyChanged();
        Console.WriteLine($"created enquiry {enquiryId}");

        var conversationId = _store.ConversationOf(enquiryId)!;
        Watch(conversationId);
        return conversationId;
    }

    /// <summary>
    /// Resumes the enquiries the last process left unfinished and watches each one.
    /// </summary>
    /// <remarks>
    /// A resumed enquiry is not restarted: its task record already carries the committed phase,
    /// and the task runner re-enters from that checkpoint. This method only makes sure the
    /// renderer learns about it and that the harness exit seam applies to it too.
    /// </remarks>
    public IReadOnlyList<string> ResumeUnfinished()
    {
        var taskIds = _store.ResumeUnfinished();
        foreach (var taskId in taskIds)
        {
            var record = Store.Task(taskId);
            if (record?.OwnerConversationId is { } conversationId)
            {
                Watch(conversationId);
            }
        }

        if (taskIds.Count > 0)
        {
            Console.WriteLine($"resumed {taskIds.Count} unfinished enquir{(taskIds.Count == 1 ? "y" : "ies")}");
            NotifyChanged();
        }

        return taskIds;
    }

    /// <summary>Notifies the renderer that the store changed.</summary>
    public void NotifyChanged() => UpdateUi?.Invoke();

    /// <summary>
    /// Watches one enquiry until it settles, so the renderer is told when the store changed.
    /// </summary>
    /// <remarks>
    /// The watch is a polling read of the store rather than an in-memory callback, so an enquiry
    /// resumed in this process and one created in it take exactly the same path.
    /// </remarks>
    private void Watch(string conversationId)
    {
        _ = Task.Run(async () =>
        {
            var seen = EnquiryPhases.Queued;
            while (true)
            {
                var state = EnquiryDurable.Read(Store, conversationId);
                var phase = EnquiryPhases.Name(state.Phase);
                if (phase != seen)
                {
                    seen = phase;
                    NotifyChanged();
                }

                if (state.IsTerminal)
                {
                    NotifyChanged();
                    Console.WriteLine($"enquiry {state.EnquiryId} settled at {phase}");
                    return;
                }

                await Task.Delay(250);
            }
        });
    }
}
