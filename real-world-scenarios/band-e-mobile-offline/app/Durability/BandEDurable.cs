using System.Globalization;
using Durable;

namespace BandEMobile.Durability;

/// <summary>
/// Every read and write of durable state on the device, in one place.
/// </summary>
/// <remarks>
/// <para>
/// This is the E2 analogue of <c>band-c-ci-repo-agent/durable/TriageStore.cs</c>, and it follows the
/// same shape: the store keys and document definitions live here, the reads that feed the UI return
/// typed state, and the writes make a finished turn a fact. The page calls these methods; it never
/// touches <see cref="DurableStore"/> itself.
/// </para>
/// <para>
/// <b>Nothing here names an Android type.</b> The store is opened over a path string that
/// <c>Chat/DeviceProfile</c> computed, and every other input is plain .NET. That is deliberate and
/// mechanical: <c>verify-android-boundary.sh</c> fails the build if anything under this directory
/// names the platform, and <c>Chat/BridgeShim.cs</c> is the app's single platform-aware file.
/// </para>
/// <para>
/// The three durable facts, and why each is here rather than in a field:
/// </para>
/// <list type="bullet">
/// <item><b>The transcript</b> — one rewinding document holding every turn. This is the thing the
/// on-device SDK does not provide: it takes the whole history on every request and keeps none of it,
/// so without this document the conversation stops existing when the process does.</item>
/// <item><b>The document the agent edits</b> — a second rewinding document. Rewinding matters:
/// after a kill the page can show not just the current body but the revision history that produced
/// it, and the demonstration's "document intact" claim is checkable from the store rather than
/// asserted from the screen.</item>
/// <item><b>Turn checkpoints</b> — one durable task per turn, parked on an external event between
/// "requested" and "completed". A turn that is killed mid-stream stays visible as
/// <c>waiting</c> instead of vanishing, which is the difference between "the store knows a turn was
/// interrupted" and "the app forgets it ever asked".</item>
/// </list>
/// </remarks>
public sealed class BandEDurable : IAsyncDisposable
{
    /// <summary>The document kind holding the transcript.</summary>
    public const string TranscriptKind = "band-e.transcript";

    /// <summary>The document kind holding the agent-edited document.</summary>
    public const string ArtifactKind = "band-e.artifact";


    /// <summary>The task definition name a turn runs under.</summary>
    public const string TurnTaskName = "band-e.turn";

    /// <summary>The external event that finishes a turn, prefixed as the layer requires.</summary>
    public const string TurnCompletedEvent = "external:turn-completed";

    /// <summary>The owner of both documents: one app, one device, one conversation.</summary>
    public const string DeviceOwner = "device:band-e";

    /// <summary>
    /// The model this variant pins.
    /// </summary>
    /// <remarks>
    /// <c>qwen2.5-0.5b</c> rather than <c>phi-3.5-mini</c>, and the reason is measured, not
    /// preferred: on the physical device E1 turned, <c>phi-3.5-mini</c> killed the inference process
    /// before any answer arrived (evidence/e1-completion.txt, defect 3), so it is not a model this
    /// band can demonstrate the offline story on. The declared cost of that swap — accuracy, and the
    /// size numbers in README.md — is stated rather than glossed. The issue's "do not quietly swap"
    /// note is why this comment and that README section exist.
    /// </remarks>
    public const string ModelAlias = "qwen2.5-0.5b";

    private readonly DurableStore _store;
    private readonly ExtensionRegistry _registry;

    private BandEDurable(DurableStore store, ExtensionRegistry registry)
    {
        _store = store;
        _registry = registry;
    }

    /// <summary>The transcript document's definition.</summary>
    public static readonly DocumentDefinition<TranscriptState> Transcript =
        new(TranscriptKind, DocumentHistory.Rewindable, ForkPolicy.Initial, () => new TranscriptState());

    /// <summary>The agent-edited document's definition.</summary>
    public static readonly DocumentDefinition<ArtifactState> Artifact =
        new(ArtifactKind, DocumentHistory.Rewindable, ForkPolicy.Initial, () => new ArtifactState());

    /// <summary>
    /// Opens the store over <paramref name="storePath"/> and installs the turn task definition.
    /// </summary>
    /// <remarks>
    /// The format is stated rather than sniffed: <see cref="DurableStoreFormat.Jsonl"/> is the
    /// append-per-commit format, which is what makes a kill mid-write a truncated last line the store
    /// discards on open rather than a corrupt file. The path's directory is created by the provider.
    /// </remarks>
    public static BandEDurable Open(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = storePath,
            Format = DurableStoreFormat.Jsonl,
            FileName = Path.GetFileName(storePath),
        });

        var registry = new ExtensionRegistry();
        registry.Install(new Extension("band-e.turns", tasks: [TurnTask()]));

        return new BandEDurable(store, registry);
    }

    /// <summary>The underlying store, for the evidence exporter. Not used by the page.</summary>
    public DurableStore Store => _store;

    public bool IsPersistent => _store.IsPersistent;

    // ─── Reads ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything the page needs after a process start: transcript, document, interrupted turns.
    /// </summary>
    /// <remarks>
    /// Read-only and pure: no document is created by reading one. A pristine install therefore reads
    /// as "no document yet" and the page says so, instead of materialising an empty state that looks
    /// like a restored conversation of length zero.
    /// </remarks>
    public SessionSnapshot ReadSession()
    {
        var turns = _store.Document(TranscriptKind, DeviceOwner)?.Latest is { } stored
            ? DurableJson.FromElement<TranscriptState>(stored)?.Turns ?? []
            : [];

        var document = _store.Document(ArtifactKind, DeviceOwner)?.Latest is { } artifact
            ? DurableJson.FromElement<ArtifactState>(artifact) ?? new ArtifactState()
            : new ArtifactState();

        var interrupted = _store.Tasks
            .Where(task => task.Name == TurnTaskName && task.Status != Durable.TaskStatus.Terminal)
            .OrderBy(task => SequenceOf(task.Id))
            .Select(task => DurableJson.FromElement<TurnCheckpoint>(task.Checkpoint) ?? new TurnCheckpoint())
            .ToList();

        var conversationTurns = _store.Conversations.Count == 0 ? 0 : _store.Conversations.Count;

        return new SessionSnapshot(turns, document, interrupted, conversationTurns);
    }

    /// <summary>Whether the store holds a transcript or a document yet.</summary>
    /// <remarks>
    /// Read before seeding, because "no document" and "an empty document" have to stay different: the
    /// page seeds only a pristine install, and a store whose transcript was deliberately reset is not
    /// pristine.
    /// </remarks>
    public bool HasState =>
        _store.Document(TranscriptKind, DeviceOwner) is not null || _store.Document(ArtifactKind, DeviceOwner) is not null;

    /// <summary>
    /// The still-running task for a turn index, or null.
    /// </summary>
    /// <remarks>
    /// Used by the page to finish a turn whose stream ended without delivering an answer — the one
    /// path where a turn can be left parked while the app is still alive. A resume from a previous
    /// process does not call this: the transcript entry and the document revision are the facts there.
    /// </remarks>
    public DurableTaskRecord? WaitingTurnTask(int turnIndex) =>
        _store.Tasks
            .Where(task => task.Name == TurnTaskName && task.Status != Durable.TaskStatus.Terminal)
            .OrderBy(task => SequenceOf(task.Id))
            .FirstOrDefault(task =>
                (DurableJson.FromElement<TurnCheckpoint>(task.Checkpoint)?.TurnIndex ?? -1) == turnIndex);

    /// <summary>The committed versions of the document, oldest first, for the revision list.</summary>
    public IReadOnlyList<DocumentVersion> ArtifactVersions =>
        _store.Document(ArtifactKind, DeviceOwner)?.Versions ?? [];

    // ─── Writes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Commits the seed document and an empty transcript. Idempotent: a second call on a store that
    /// already holds either document writes nothing.
    /// </summary>
    /// <remarks>
    /// One transaction, because a seed that lands without a transcript (or the reverse) would make
    /// the page show a document with no conversation behind it. <c>TryCreateDocument</c> is the
    /// create-if-absent primitive, so two callers cannot both believe they seeded the store.
    /// </remarks>
    public Task SeedAsync(ArtifactState seed, string? journalEntryKind = null) =>
        _store.CommitAsync(tx =>
        {
            if (tx.TryCreateDocument(Transcript, DeviceOwner))
            {
                var transcript = tx.Doc(Transcript, DeviceOwner);
                transcript.Value = new TranscriptState();
                transcript.Save(tx.AppendEntry(ConversationId(tx), journalEntryKind ?? "seed-transcript"));
            }

            if (tx.TryCreateDocument(Artifact, DeviceOwner))
            {
                var artifact = tx.Doc(Artifact, DeviceOwner);
                artifact.Value = seed;
                artifact.Save(tx.AppendEntry(ConversationId(tx), journalEntryKind ?? "seed-document"));
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// Creates the durable task for a turn and parks it on <see cref="TurnCompletedEvent"/>.
    /// </summary>
    /// <remarks>
    /// This is the checkpoint that makes an interrupted turn visible. It is created <em>before</em>
    /// the model is asked anything, so a process killed during streaming leaves a task the next start
    /// can find and name. The task is left <c>waiting</c>, holding no invocation and no timer.
    /// </remarks>
    public Task<string> BeginTurnAsync(int turnIndex, string request) =>
        _store.CommitAsync(tx =>
        {
            var record = tx.CreateTask(
                TurnTask(),
                DurableJson.ToElement(new TurnInput { TurnIndex = turnIndex, Request = request }),
                ownerConversationId: ConversationId(tx));

            // The task's first checkpoint names itself and when it started. `CreateTask` writes the
            // definition's initial checkpoint, which is a blank one; leaving it there is what made an
            // interrupted turn read back as "turn 0, started (empty)".
            var checkpoint = new TurnCheckpoint
            {
                Phase = TurnPhases.Requested,
                TurnIndex = turnIndex,
                Request = request,
                StartedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                TaskId = record.Id,
            };
            record.Checkpoint = DurableJson.ToElement(checkpoint);

            tx.AppendEntry(ConversationId(tx), "turn-requested", $"#{turnIndex}");
            return Task.FromResult(record.Id);
        });

    /// <summary>
    /// Makes a finished turn a fact: transcript, document revision, and task completion in one
    /// transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One transaction on purpose. A reply that is in the transcript but not in the document revision
    /// list, or a completed task whose reply never reached either, is exactly the half-state the
    /// demonstration must not show after a kill: the next reader would see a conversation that claims
    /// a turn happened and a document that denies it.
    /// </para>
    /// <para>
    /// The reply is committed only here, at the end. A process killed mid-stream commits nothing, so
    /// the transcript still ends at the last complete turn and the waiting task from
    /// <see cref="BeginTurnAsync"/> is the whole record of the interruption.
    /// </para>
    /// </remarks>
    public Task CompleteTurnAsync(
        string taskId,
        int turnIndex,
        StoredTurn turn,
        ArtifactState? document,
        string journalEntryKind = "turn")
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        turn.Index = turnIndex;
        turn.CompletedAt ??= now;

        return _store.CommitAsync(tx =>
        {
            var conversation = ConversationId(tx);
            var entry = tx.AppendEntry(conversation, journalEntryKind, $"#{turnIndex}");

            var transcript = tx.Doc(Transcript, DeviceOwner);
            transcript.Value.Turns.Add(turn);
            transcript.Save(entry);

            if (document is not null)
            {
                var artifact = tx.Doc(Artifact, DeviceOwner);
                artifact.Value = document;
                artifact.Save(entry);
            }

            var task = _store.Task(taskId);
            if (task is not null)
            {
                // The turn is finished, so its task becomes terminal in the same transaction that
                // commits the reply. Leaving it Running would make the next start report a turn that
                // completed as interrupted — the exact bug this line fixes.
                task.Status = Durable.TaskStatus.Terminal;
                task.Outcome = DurableJson.ToElement(new TaskOutcome("completed"));
                task.WaitingOn.RemoveAll(name => string.Equals(name, TurnCompletedEvent, StringComparison.Ordinal));
                task.Memos[DurableStore.ExternalEventMemoKey] = DurableJson.ToElement(new TurnOutcomeMemo
                {
                    Reply = turn.Reply,
                    TurnIndex = turnIndex,
                    FinishedAt = turn.CompletedAt,
                });
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Records that a parked turn was abandoned rather than completed, so a kill does not leave a
    /// task waiting forever.
    /// </summary>
    /// <remarks>
    /// Only for a turn the app knows is dead: the page calls it when the reader chooses to discard an
    /// interrupted turn. The task becomes terminal <c>aborted</c> with the reason, which is a durably
    /// recorded fact rather than a guess about what happened.
    /// </remarks>
    public Task AbandonTurnAsync(string taskId, string reason) =>
        _store.CommitAsync(tx =>
        {
            var task = _store.Task(taskId);
            if (task is null || task.Status == Durable.TaskStatus.Terminal)
            {
                return Task.CompletedTask;
            }

            task.Status = Durable.TaskStatus.Terminal;
            task.WaitingOn.Clear();
            task.Outcome = DurableJson.ToElement(new TaskOutcome("aborted", Error: reason));
            tx.AppendEntry(ConversationId(tx), "turn-abandoned", reason);
            return Task.CompletedTask;
        });

    /// <summary>
    /// Resets the demonstration: a fresh transcript and a fresh seed document, as new revisions.
    /// </summary>
    /// <remarks>
    /// A document's history is append-only by design, so this does not delete anything. What it does
    /// is make the <em>latest</em> version an empty transcript and the seed document, which is what
    /// the page reads. The previous run's versions stay in the file and are still walkable through
    /// <see cref="ArtifactVersions"/> — the honest outcome, and the reason a reset does not also
    /// wipe the model cache (that is <c>pm clear</c>'s job, and it costs a re-download).
    /// </remarks>
    public Task ResetAsync(ArtifactState seed) =>
        _store.CommitAsync(tx =>
        {
            var conversation = ConversationId(tx);
            var entry = tx.AppendEntry(conversation, "reset");

            // A reset abandons any turn still parked: the reader asked for a clean conversation, so
            // leaving stale "no reply was committed" rows would be reporting interruptions the new
            // conversation never had. They become terminal `aborted` tasks, not deletions.
            foreach (var task in _store.Tasks.Where(task =>
                         task.Name == TurnTaskName && task.Status != Durable.TaskStatus.Terminal).ToList())
            {
                task.Status = Durable.TaskStatus.Terminal;
                task.WaitingOn.Clear();
                task.Outcome = DurableJson.ToElement(new TaskOutcome("aborted", Error: "reset"));
            }


            var transcript = tx.Doc(Transcript, DeviceOwner);
            transcript.Value = new TranscriptState();
            transcript.Save(entry);

            var artifact = tx.Doc(Artifact, DeviceOwner);
            artifact.Value = seed;
            artifact.Save(entry);

            return Task.CompletedTask;
        });

    /// <summary>Closes the store, releasing the exclusive lock on its file.</summary>
    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        return ValueTask.CompletedTask;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The conversation entries are appended to: the only one, created on first write.
    /// </summary>
    /// <remarks>
    /// Events (an entry per turn) need a conversation to hang off, and an entry is what a document
    /// version points at. A store that has never been written has no conversation, so the first
    /// transaction creates it. The <c>Conversations</c> list is read back from the provider rather
    /// than cached, so two callers in one transaction see the same id.
    /// </remarks>
    private string ConversationId(Transaction tx) =>
        _store.Conversations.FirstOrDefault(conversation => conversation.ParentConversationId is null)?.Id
        ?? tx.CreateConversation().Id;

    private static long SequenceOf(string taskId) =>
        taskId.Length > 1 && long.TryParse(taskId.AsSpan(1), out var value) ? value : 0;

    /// <summary>
    /// The one-phase turn task. Its handler is a no-op that reports "still waiting": the work a turn
    /// does is a model call and a commit, both of which the page drives, and a handler that ran the
    /// model would need the transport inside the durable layer — the coupling this variant exists to
    /// avoid.
    /// </summary>
    /// <remarks>
    /// The definition exists so the parked tasks have a name and a checkpoint shape the store can
    /// deserialize, and so a resumed task is not "unknown task definition". Its phase re-parks on the
    /// same event with the same checkpoint, which is correct: a turn that has not been delivered an
    /// outcome has not finished.
    /// </remarks>
    public static DurableTaskDefinition TurnTask() =>
        DurableTask.Define<TurnInput, TurnCheckpoint, TurnOutcomeMemo>(
            TurnTaskName,
            () => new TurnCheckpoint(),
            (input, checkpoint, runtime) => Task.FromResult(
                TaskCommit.WaitingForEvent(
                    DurableJson.ToElement(new TurnCheckpoint
                    {
                        Phase = checkpoint.Phase == TurnPhases.Completed ? TurnPhases.Completed : TurnPhases.Requested,
                        TurnIndex = input.TurnIndex,
                        Request = input.Request,
                        Reply = checkpoint.Reply,
                        StartedAt = checkpoint.StartedAt,
                        FinishedAt = checkpoint.FinishedAt,
                    }),
                    TurnCompletedEvent)));

    /// <summary>The task's input: which turn, and what was asked.</summary>
    public sealed class TurnInput
    {
        public int TurnIndex { get; set; }

        public string Request { get; set; } = string.Empty;
    }

    /// <summary>The memo an answer is delivered under.</summary>
    public sealed class TurnOutcomeMemo
    {
        public string? Reply { get; set; }

        public int TurnIndex { get; set; }

        public string? FinishedAt { get; set; }
    }
}
