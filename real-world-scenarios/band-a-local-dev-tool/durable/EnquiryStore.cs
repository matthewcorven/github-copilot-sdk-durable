using Durable;
using GitHub.Copilot;
using TaskStatus = Durable.TaskStatus;

namespace AgentOrchestrator.Durable;

/// <summary>
/// The durable half of one running process: the store, the durable client, the enquiry task, and
/// the mapping from a live session back to the enquiry it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// This is where the baseline's <c>AppState</c> split in two. <c>AppState</c> stayed the
/// application object — the list the UI reads. This is the durable object underneath it: it owns
/// the store file, resumes the conversations the previous process left behind, and provides the
/// commit gate every write goes through.
/// </para>
/// <para>
/// Two sessions cannot interleave their writes because there is one serializer for the store:
/// <see cref="DurableStore.CommitAsync{T}"/> holds the store's gate for the whole transaction, so
/// two enquiries commit one after the other and each transaction's read-then-write is atomic with
/// respect to the other. The two enquiries keep separate documents, so serialization is also all
/// the isolation they need.
/// </para>
/// </remarks>
public sealed class EnquiryStore : IAsyncDisposable
{
    private readonly Dictionary<string, DurableConversation> _bySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _conversationByEnquiry = new(StringComparer.Ordinal);
    private readonly object _mapGate = new();

    private EnquiryStore(DurableHarness harness, DurableTaskDefinition definition)
    {
        Harness = harness;
        Definition = definition;
    }

    /// <summary>The durable host every enquiry write goes through.</summary>
    public DurableHarness Harness { get; }

    /// <summary>The durable store behind the harness.</summary>
    public DurableStore Store => Harness.Store;

    /// <summary>The enquiry task registered with the client's registry.</summary>
    public DurableTaskDefinition Definition { get; }

    /// <summary>
    /// Opens the store at <paramref name="storePath"/> and re-enters the enquiries the previous
    /// process left unfinished.
    /// </summary>
    /// <param name="storePath">The store file. Its directory is created on first write.</param>
    /// <param name="database">The property listings the search tool reads.</param>
    /// <param name="options">How to reach the Copilot CLI.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <remarks>
    /// <see cref="DurableHarness.ResumeAsync"/> is deliberately <em>not</em> used here.
    /// It awaits each task in turn, which is right for a host that wants a barrier but wrong for a
    /// server: one slow resumed enquiry would hold the others. The tasks are started instead, and
    /// each one's completion is what advances the store.
    /// </remarks>
    public static async Task<EnquiryStore> OpenAsync(
        string storePath,
        PropertyDatabase database,
        DurableHarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = storePath,
            Format = DurableStoreFormat.Jsonl,
        });

        // The harness, not the facade: it resolves the runtime path, so DURABLE_RUNTIME_PATH
        // selects a recorded runtime, and it owns the store's one task runner.
        var registry = new ExtensionRegistry();

        // The task closes over the conversation lookup, so it is built before the registry is
        // populated. The only durable input is the store; the session is this process's.
        EnquiryStore? self = null;
        var definition = EnquiryDurable.Task(
            store,
            (input, prompt, token) => self!.SendAsync(input.ConversationId, prompt, token),
            conversationId => self!.LastCheckpoint(conversationId));

        var harness = await DurableHarness.OpenAsync(store, registry, options, cancellationToken);
        self = new EnquiryStore(harness, definition);

        registry.Install(EnquiryTools.Build(store, database, self.ConversationFor, definition));
        return self;
    }

    /// <summary>Starts every enquiry the last process left unfinished, and returns their ids.</summary>
    /// <remarks>
    /// A task whose record is <see cref="Durable.TaskStatus.Pending"/> or
    /// <see cref="Durable.TaskStatus.Running"/> is one that never reached a terminal state, which
    /// is exactly what a process dying mid-enquiry leaves behind. A terminal task is not started:
    /// its outcome is already committed.
    /// </remarks>
    public IReadOnlyList<string> ResumeUnfinished()
    {
        var unfinished = Store.Tasks
            .Where(task => task.Name == EnquiryDurable.TaskName)
            .Where(task => task.Status is TaskStatus.Pending or TaskStatus.Running)
            .Select(task => task.Id)
            .ToList();

        foreach (var taskId in unfinished)
        {
            // The document records that this process picked the enquiry up, so the restart is
            // visible in the same place the rest of the enquiry is.
            if (Store.Task(taskId)?.OwnerConversationId is { } conversationId)
            {
                EnquiryWrites.NoteRestartAsync(Store, conversationId).GetAwaiter().GetResult();
            }

            _ = Harness.WaitForTaskAsync(taskId);
        }

        return unfinished;
    }

    /// <summary>Returns the conversation that owns a live session, if this process opened one.</summary>
    public string? ConversationFor(string sessionId)
    {
        lock (_mapGate)
        {
            return _bySession.GetValueOrDefault(sessionId)?.Id;
        }
    }

    /// <summary>Remembers which enquiry a conversation belongs to.</summary>
    public void Remember(string enquiryId, string conversationId)
    {
        lock (_mapGate)
        {
            _conversationByEnquiry[enquiryId] = conversationId;
        }
    }

    /// <summary>The conversation id a short enquiry id was created under.</summary>
    public string? ConversationOf(string enquiryId)
    {
        lock (_mapGate)
        {
            return _conversationByEnquiry.GetValueOrDefault(enquiryId);
        }
    }

    /// <summary>
    /// Creates an enquiry: a durable conversation, its first committed state, and the task that
    /// works it.
    /// </summary>
    /// <remarks>
    /// The order is the durability argument. The conversation exists before the document, the
    /// document exists before the task, and the task exists before it is started, so a process that
    /// dies at any point leaves a record the next one can finish. The reverse order would leave a
    /// task with no document to write.
    /// </remarks>
    public async Task<EnquiryState> CreateEnquiryAsync(
        string enquiryId,
        string enquiry,
        CancellationToken cancellationToken = default)
    {
        var conversation = await Harness.CreateConversationAsync(
            new AgentChoices
            {
                Extensions = [EnquiryDurable.ExtensionName],
                Tools = ["set_current_phase", "report_intent", "search_properties"],
                Permissions =
                [
                    "custom-tool:set_current_phase",
                    "custom-tool:report_intent",
                    "custom-tool:search_properties",
                ],
            },
            cancellationToken: cancellationToken);

        Remember(enquiryId, conversation.Id);
        Bind(conversation);

        var state = await Store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversation.Id);
            draft.Value = new EnquiryState
            {
                EnquiryId = enquiryId,
                Enquiry = enquiry,
                Phase = Phase.Queued,
                StartedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            draft.Save(tx.AppendEntry(conversation.Id, "enquiry", enquiry));
            await Task.CompletedTask;
            return draft.Value;
        });

        var record = await Store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(
                Definition,
                DurableJson.ToElement(new EnquiryInput
                {
                    ConversationId = conversation.Id,
                    EnquiryId = enquiryId,
                    Enquiry = enquiry,
                }),
                ownerConversationId: conversation.Id)));

        await Store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(EnquiryDurable.Enquiry, conversation.Id);
            draft.Value.TaskId = record.Id;
            draft.Save(tx.AppendEntry(conversation.Id, "task", record.Id));
            await Task.CompletedTask;
        });

        // Started after the record exists, and not awaited: the enquiry is a background task whose
        // progress the UI reads from the store.
        _ = Harness.WaitForTaskAsync(record.Id, cancellationToken);
        return state;
    }

    /// <summary>Sends a phase prompt to an enquiry's conversation and returns the answer.</summary>
    /// <remarks>
    /// This is the one place a resumed task reaches a live session. The conversation is looked up
    /// in this process's live handles first and resumed from the store otherwise;
    /// <c>ResumeConversationAsync</c> rebinds the recorded native session, so the resumed enquiry
    /// continues the conversation the previous process started rather than a new one.
    /// </remarks>
    public async Task<string> SendAsync(string conversationId, string prompt, CancellationToken cancellationToken)
    {
        var conversation = Harness.FindConversation(conversationId)
            ?? await Harness.ResumeConversationAsync(conversationId, cancellationToken);

        Bind(conversation);
        var message = await conversation.SubmitAsync(prompt, cancellationToken);
        return message?.Data.Content ?? string.Empty;
    }

    /// <summary>
    /// The conversation's most recent checkpoint, read from the durable store.
    /// </summary>
    /// <remarks>
    /// This is the layer's own record of what the model did last — a <c>turn:&lt;messageId&gt;</c>
    /// entry when an answer committed — and it is the only evidence that separates a turn that
    /// finished from one that had just been sent. It is read from the store rather than from a live
    /// session so a resumed task can consult it before it has reconnected to anything.
    /// </remarks>
    public ConversationCheckpoint? LastCheckpoint(string conversationId) =>
        Store.Document(CheckpointKind, conversationId)?.Latest is { } latest
            ? latest.Deserialize<ConversationCheckpoint>(DurableJson.Options)
            : null;

    /// <summary>The document kind the durable layer commits a conversation's checkpoints under.</summary>
    private const string CheckpointKind = "durable.checkpoint";

    /// <summary>Waits for an enquiry's task to finish and returns what it committed.</summary>
    public Task<TaskOutcome> WaitAsync(string taskId, CancellationToken cancellationToken = default) =>
        Harness.WaitForTaskAsync(taskId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The harness owns the client and borrows the store, so it disposes the first and leaves the
    /// second; the store is disposed here, after the harness, which is also the order the store's
    /// exclusive writer lock requires. The store reference is captured before disposal because a
    /// disposed runtime refuses to report it.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var store = Store;
        await Harness.DisposeAsync();
        store.Dispose();
    }

    private void Bind(DurableConversation conversation)
    {
        lock (_mapGate)
        {
            _bySession[conversation.SessionId] = conversation;
        }
    }
}
