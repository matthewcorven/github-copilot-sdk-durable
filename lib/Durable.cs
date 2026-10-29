// Durable support layer for GitHub Copilot SDK agents.
//
// The GitHub Copilot SDK already provides a durable conversation: sessions persist to
// COPILOT_HOME/session-state/{id}/, can be resumed, forked, and streamed, and the runtime
// persists tool calls and results.
//
// Pi Durable adds a layer on top of that: typed documents with rewindable history and fork
// policies, checkpointed task state machines with idempotency memos, an extension registry
// with reload semantics, and a submission inbox.
//
// This file provides exactly that missing layer. It is shared by every example in this
// repository via `#:include`.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
#pragma warning disable IL2026, IL3050 // Durable state is reflection-serialized by design.
using System.Text.Json.Serialization.Metadata;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Durable;

/// <summary>How a document behaves when a conversation is forked.</summary>
public enum ForkPolicy
{
    /// <summary>The fork starts with the value the document had at the fork entry.</summary>
    AsOf,

    /// <summary>The fork starts with the parent's current value.</summary>
    Current,

    /// <summary>The fork starts with the document's initial value.</summary>
    Initial,
}

/// <summary>How a document's history is retained.</summary>
public enum DocumentHistory
{
    /// <summary>Only the latest value is retained.</summary>
    Latest,

    /// <summary>Every committed value is retained and can be read as of a past entry.</summary>
    Rewindable,
}

/// <summary>Defines a typed document: durable JSON state attached to a conversation.</summary>
public sealed class DocumentDefinition<T>
{
    /// <summary>Creates a document definition.</summary>
    public DocumentDefinition(
        string kind,
        DocumentHistory history = DocumentHistory.Rewindable,
        ForkPolicy fork = ForkPolicy.AsOf,
        Func<T>? initial = null)
    {
        Kind = kind;
        History = history;
        Fork = fork;
        Initial = initial ?? (() => default!);
    }

    /// <summary>The persisted document kind, e.g. <c>example.notes</c>.</summary>
    public string Kind { get; }

    /// <summary>How the document's history is retained.</summary>
    public DocumentHistory History { get; }

    /// <summary>What a forked conversation starts with.</summary>
    public ForkPolicy Fork { get; }

    /// <summary>The value a document starts with when first written.</summary>
    public Func<T> Initial { get; }
}

/// <summary>One committed value of a document, with the entry that produced it.</summary>
public sealed record DocumentVersion(string EntryId, JsonElement Value, long Sequence);

/// <summary>One immutable transcript entry: a kind, an optional payload, and the conversation it belongs to.</summary>
public sealed record EntryRecord(string Id, string ConversationId, string Kind, JsonElement? Data, long Sequence);

/// <summary>The durable state of one document.</summary>
public sealed class DocumentState
{
    /// <summary>Creates an empty document state.</summary>
    public DocumentState(string kind, string ownerId, ForkPolicy fork, JsonElement? initial = null)
    {
        Kind = kind;
        OwnerId = ownerId;
        Fork = fork;
        Initial = initial;
    }

    /// <summary>The document kind.</summary>
    public string Kind { get; }

    /// <summary>The conversation the document belongs to.</summary>
    public string OwnerId { get; }

    /// <summary>The fork policy captured when the document was first written.</summary>
    public ForkPolicy Fork { get; }

    /// <summary>The document's initial value, used when a fork starts from the initial state.</summary>
    public JsonElement? Initial { get; }

    /// <summary>The committed versions, oldest first.</summary>
    public List<DocumentVersion> Versions { get; } = [];

    /// <summary>The latest committed value, or <see langword="null"/> if never written.</summary>
    public JsonElement? Latest => Versions.Count == 0 ? null : Versions[^1].Value;

    /// <summary>The value committed in the same transaction as the given entry.</summary>
    public JsonElement? AsOf(string entryId)
    {
        for (var i = Versions.Count - 1; i >= 0; i--)
        {
            if (Versions[i].EntryId == entryId)
            {
                return Versions[i].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// The value as of the given entry: the last version committed at or before that entry.
    /// A version committed in the same transaction as the entry counts as "at" it.
    /// </summary>
    public JsonElement? AsOfOrBefore(string entryId, Func<string, long> sequenceOf)
    {
        var boundary = sequenceOf(entryId);
        for (var i = Versions.Count - 1; i >= 0; i--)
        {
            var version = Versions[i];
            if (version.EntryId == entryId)
            {
                return version.Value;
            }

            if (sequenceOf(version.EntryId) <= boundary)
            {
                return version.Value;
            }
        }

        return null;
    }

    /// <summary>The value as of the given entry: the value committed with it, or the one before it.</summary>
    public JsonElement? AsOfOrBefore(string entryId)
    {
        for (var i = Versions.Count - 1; i >= 0; i--)
        {
            if (Versions[i].EntryId == entryId)
            {
                return Versions[i].Value;
            }
        }

        return Versions.Count == 0 ? null : Versions[0].Value;
    }
}

/// <summary>The lifecycle status of a durable task.</summary>
public enum TaskStatus
{
    /// <summary>Created but not yet started.</summary>
    Pending,

    /// <summary>Currently executing a phase.</summary>
    Running,

    /// <summary>Waiting on child tasks.</summary>
    Waiting,

    /// <summary>Sleeping until a durable deadline.</summary>
    Sleeping,

    /// <summary>Finished, successfully or not.</summary>
    Terminal,
}

/// <summary>The durable record of one task.</summary>
public sealed class DurableTaskRecord
{
    /// <summary>Creates a task record.</summary>
    public DurableTaskRecord(string id, string name, JsonElement input, JsonElement checkpoint)
    {
        Id = id;
        Name = name;
        Input = input;
        Checkpoint = checkpoint;
        Status = TaskStatus.Pending;
    }

    /// <summary>The task identifier.</summary>
    public string Id { get; set; }

    /// <summary>The task definition name.</summary>
    public string Name { get; set; }

    /// <summary>The task's immutable input.</summary>
    public JsonElement Input { get; set; }

    /// <summary>The last saved checkpoint.</summary>
    public JsonElement Checkpoint { get; set; }

    /// <summary>The current lifecycle status.</summary>
    public TaskStatus Status { get; set; }

    /// <summary>The terminal outcome, once the task finishes.</summary>
    public JsonElement? Outcome { get; set; }

    /// <summary>How many times the task has been restarted after an interruption.</summary>
    public int Restarts { get; set; }

    /// <summary>The owning conversation, when the task is conversation-owned.</summary>
    public string? OwnerConversationId { get; set; }

    /// <summary>The owning task, when the task is task-owned.</summary>
    public string? OwnerTaskId { get; set; }

    /// <summary>Whether the task is background work that does not block its owner.</summary>
    public bool Background { get; set; }

    /// <summary>The child tasks this task waits on.</summary>
    public List<string> WaitingOn { get; set; } = [];

    /// <summary>Idempotency memos, so a rerun after a crash does not repeat an effect.</summary>
    public Dictionary<string, JsonElement> Memos { get; set; } = [];
}

/// <summary>The result of a durable task.</summary>
public sealed record TaskOutcome(string Status, JsonElement? Result = null, string? Error = null)
{
    /// <summary>Whether the task completed successfully.</summary>
    public bool IsCompleted => Status == "completed";

    /// <summary>Whether the task was aborted.</summary>
    public bool IsAborted => Status == "aborted";

    /// <summary>Whether the task failed.</summary>
    public bool IsFailed => Status == "failed";
}

/// <summary>A commit that advances a task's durable state.</summary>
public sealed class TaskCommit
{
    private TaskCommit(TaskStatus status, JsonElement? checkpoint, TaskOutcome? outcome)
    {
        Status = status;
        Checkpoint = checkpoint;
        Outcome = outcome;
    }

    /// <summary>The status to move to.</summary>
    public TaskStatus Status { get; }

    /// <summary>The next checkpoint, when the task continues.</summary>
    public JsonElement? Checkpoint { get; }

    /// <summary>The terminal outcome, when the task finishes.</summary>
    public TaskOutcome? Outcome { get; }

    /// <summary>The child tasks this commit waits on.</summary>
    public IReadOnlyList<string> WaitingOn { get; init; } = [];

    /// <summary>Creates a commit that continues the task with a new checkpoint.</summary>
    public static TaskCommit Running(JsonElement checkpoint) => new(TaskStatus.Running, checkpoint, null);

    /// <summary>Creates a commit that continues the task, waiting on child tasks.</summary>
    public static TaskCommit Waiting(JsonElement checkpoint, IEnumerable<string> on) =>
        new(TaskStatus.Waiting, checkpoint, null) { WaitingOn = on.ToList() };

    /// <summary>Creates a commit that finishes the task successfully.</summary>
    public static TaskCommit Completed(JsonElement result) =>
        new(TaskStatus.Terminal, null, new TaskOutcome("completed", result));

    /// <summary>Creates a commit that finishes the task with a failure.</summary>
    public static TaskCommit Failed(string error) =>
        new(TaskStatus.Terminal, null, new TaskOutcome("failed", Error: error));

    /// <summary>Creates a commit that finishes the task as aborted.</summary>
    public static TaskCommit Aborted() =>
        new(TaskStatus.Terminal, null, new TaskOutcome("aborted"));
}

/// <summary>The context a task phase runs with.</summary>
public sealed class TaskRuntime
{
    private readonly DurableStore _store;

    /// <summary>Creates a task runtime.</summary>
    public TaskRuntime(
        DurableStore store,
        DurableTaskRecord task,
        DurableTaskDefinition definition,
        CancellationToken cancellationToken = default)
    {
        _store = store;
        Task = task;
        Definition = definition;
        CancellationToken = cancellationToken;
    }

    /// <summary>The task record being executed.</summary>
    public DurableTaskRecord Task { get; }

    /// <summary>The task definition being executed.</summary>
    public DurableTaskDefinition Definition { get; }

    /// <summary>
    /// Cancelled when the task should stop. A task that is cancelled mid-phase leaves
    /// its last checkpoint in place, so a later run continues from there.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The current wall-clock time.</summary>
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>Saves a checkpoint, or finishes the task with an outcome.</summary>
    public Task<TaskCommit> CommitAsync(Func<TaskCommit> commit) => _store.CommitTaskAsync(Task.Id, commit);

    /// <summary>
    /// Saves a checkpoint, or finishes the task with an outcome. The callback may
    /// create child tasks: they are written in the same transaction as the checkpoint
    /// that records them.
    /// </summary>
    public Task<TaskCommit> CommitAsync(Func<Transaction, Task<TaskCommit>> commit) =>
        _store.CommitTaskAsync(Task.Id, commit);

    /// <summary>
    /// Creates a child task owned by this task. The child is written in the same
    /// transaction as the checkpoint that records it, so a crash leaves either both
    /// the child and the checkpoint, or neither.
    /// </summary>
    public DurableTaskRecord CreateChildTask(Transaction tx, DurableTaskDefinition definition, JsonElement input) =>
        tx.CreateTask(definition, input, ownerTaskId: Task.Id);

    /// <summary>Schedules a task to run.</summary>
    public void ScheduleTask(string taskId, CancellationToken cancellationToken = default) =>
        _store.ScheduleTask(taskId, cancellationToken);

    /// <summary>Reads an idempotency memo, or writes one when <paramref name="value"/> is supplied.</summary>
    public Task<JsonElement?> MemoAsync(string key, JsonElement? value = null) =>
        _store.MemoAsync(Task.Id, key, value);

    /// <summary>Sleeps until a durable deadline, saving the wake time in the checkpoint.</summary>
    public Task<TaskCommit> SleepUntilAsync(DateTimeOffset deadline, Func<TaskCommit> nextCheckpoint) =>
        _store.SleepUntilAsync(Task.Id, deadline, nextCheckpoint, CancellationToken);

    /// <summary>Reads the outcomes of child tasks.</summary>
    public Task<IReadOnlyList<TaskOutcome>> OutcomesAsync(IEnumerable<string> taskIds) =>
        _store.OutcomesAsync(taskIds);
}

/// <summary>The definition of a durable task: a checkpointed state machine.</summary>
public sealed class DurableTaskDefinition
{
    /// <summary>Creates a task definition.</summary>
    public DurableTaskDefinition(
        string name,
        Func<JsonElement> initial,
        IReadOnlyDictionary<string, Func<TaskRuntime, Task<TaskCommit>>> phases,
        Func<TaskRuntime, Task<TaskCommit>> abort)
    {
        Name = name;
        Initial = initial;
        Phases = phases;
        Abort = abort;
    }

    /// <summary>The task name, used to find the definition in the registry.</summary>
    public string Name { get; }

    /// <summary>The starting checkpoint.</summary>
    public Func<JsonElement> Initial { get; }

    /// <summary>One handler per phase.</summary>
    public IReadOnlyDictionary<string, Func<TaskRuntime, Task<TaskCommit>>> Phases { get; }

    /// <summary>Runs instead of the phases after an abort.</summary>
    public Func<TaskRuntime, Task<TaskCommit>> Abort { get; }
}

/// <summary>Builds durable task definitions with typed input, checkpoint, and outcome.</summary>
public static class DurableTask
{
    /// <summary>Defines a single-phase task whose checkpoint and outcome are typed.</summary>
    public static DurableTaskDefinition Define<TInput, TCheckpoint, TResult>(
        string name,
        Func<TCheckpoint> initial,
        Func<TInput, TCheckpoint, TaskRuntime, Task<TaskCommit>> phase,
        Func<TInput, TCheckpoint, TaskRuntime, Task<TaskCommit>>? abort = null)
    {
        var phases = new Dictionary<string, Func<TaskRuntime, Task<TaskCommit>>>
        {
            ["run"] = runtime => phase(
                runtime.Task.Input.Deserialize<TInput>(DurableJson.Options)!,
                runtime.Task.Checkpoint.Deserialize<TCheckpoint>(DurableJson.Options)!,
                runtime),
        };

        return new DurableTaskDefinition(
            name,
            () => DurableJson.ToElement(initial()),
            phases,
            abort is null
                ? _ => Task.FromResult(TaskCommit.Aborted())
                : runtime => abort(
                    runtime.Task.Input.Deserialize<TInput>(DurableJson.Options)!,
                    runtime.Task.Checkpoint.Deserialize<TCheckpoint>(DurableJson.Options)!,
                    runtime));
    }

    /// <summary>Defines a multi-phase task from a phase-name to handler map.</summary>
    public static DurableTaskDefinition DefinePhases<TInput, TCheckpoint>(
        string name,
        Func<TCheckpoint> initial,
        IReadOnlyDictionary<string, Func<TInput, TCheckpoint, TaskRuntime, Task<TaskCommit>>> phases,
        Func<TInput, TCheckpoint, TaskRuntime, Task<TaskCommit>>? abort = null)
    {
        var handlers = phases.ToDictionary(
            pair => pair.Key,
            pair => (Func<TaskRuntime, Task<TaskCommit>>)(runtime => pair.Value(
                runtime.Task.Input.Deserialize<TInput>(DurableJson.Options)!,
                runtime.Task.Checkpoint.Deserialize<TCheckpoint>(DurableJson.Options)!,
                runtime)));

        return new DurableTaskDefinition(
            name,
            () => DurableJson.ToElement(initial()),
            handlers,
            abort is null
                ? _ => Task.FromResult(TaskCommit.Aborted())
                : runtime => abort(
                    runtime.Task.Input.Deserialize<TInput>(DurableJson.Options)!,
                    runtime.Task.Checkpoint.Deserialize<TCheckpoint>(DurableJson.Options)!,
                    runtime));
    }
}

/// <summary>JSON helpers shared by the durable layer.</summary>
public static class DurableJson
{
    /// <summary>The serializer options used for durable state.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a value to a <see cref="JsonElement"/>.</summary>
    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);

    /// <summary>Deserializes a <see cref="JsonElement"/> to a value.</summary>
    public static T? FromElement<T>(JsonElement element) => element.Deserialize<T>(Options);
}

/// <summary>The record of one conversation.</summary>
public sealed class ConversationRecord
{
    /// <summary>Creates a conversation record.</summary>
    public ConversationRecord(string id, string? parentConversationId = null, string? forkEntryId = null)
    {
        Id = id;
        ParentConversationId = parentConversationId;
        ForkEntryId = forkEntryId;
    }

    /// <summary>The conversation identifier.</summary>
    public string Id { get; set; }

    /// <summary>The conversation this one was forked from.</summary>
    public string? ParentConversationId { get; set; }

    /// <summary>The entry the fork was taken at.</summary>
    public string? ForkEntryId { get; set; }

    /// <summary>The owning task, when the conversation is task-owned.</summary>
    public string? OwnerTaskId { get; set; }

    /// <summary>The owning conversation, when the conversation is conversation-owned.</summary>
    public string? OwnerConversationId { get; set; }
}

/// <summary>How far a fork has got.</summary>
public enum ForkIntentState
{
    /// <summary>
    /// The fork's durable half exists — the conversation record, the inherited transcript, and
    /// the inherited documents — but the native session has not been created yet. A fork found
    /// in this state was interrupted, and is completed or abandoned on the next open.
    /// </summary>
    Pending,

    /// <summary>The fork is complete: its native session exists and is recorded here.</summary>
    Completed,

    /// <summary>
    /// The fork was abandoned. Its durable half is kept — a conversation record cannot be
    /// removed from a store — but it is never given a session, so it is inert.
    /// </summary>
    Abandoned,
}

/// <summary>
/// The durable record of a fork in progress, owned by the fork itself.
/// </summary>
/// <remarks>
/// It is written in the same transaction that creates the fork's conversation record and
/// before the native fork RPC, so a crash between the two is detectable: a fork whose intent
/// is still <see cref="ForkIntentState.Pending"/> has no session, and reopening the store
/// either completes it or abandons it. The document uses <see cref="ForkPolicy.Initial"/> so a
/// fork of a fork starts with a fresh intent rather than inheriting its parent's.
/// </remarks>
public sealed class ForkIntent
{
    /// <summary>The conversation the fork was taken from.</summary>
    public string ParentConversationId { get; set; } = string.Empty;

    /// <summary>The parent's durable entry the fork was taken at.</summary>
    public string ForkEntryId { get; set; } = string.Empty;

    /// <summary>The native session the fork was given, once it has one.</summary>
    public string? SessionId { get; set; }

    /// <summary>How far the fork has got.</summary>
    public ForkIntentState State { get; set; }
}

/// <summary>
/// The durable record binding a conversation to the native SDK session that serves it.
/// </summary>
/// <remarks>
/// The binding is a document, not a store column: it is written in the same transaction as the
/// entry that records it, so a conversation and the session it was bound to are never observed
/// apart. It is declared with <see cref="ForkPolicy.Initial"/>, so a fork does not inherit its
/// parent's session identifier — a fork is a new conversation and gets its own session.
/// </remarks>
public sealed class SessionBinding
{
    /// <summary>The native SDK session identifier.</summary>
    public string? SessionId { get; set; }

    /// <summary>The model the session was created with.</summary>
    public string? Model { get; set; }

    /// <summary>When the binding was recorded, as an ISO 8601 timestamp.</summary>
    public string? BoundAt { get; set; }
}

/// <summary>
/// The durable record of one prompt's admission, written before the prompt is sent.
/// </summary>
/// <remarks>
/// Admission is the durable half of a send. It is committed before the prompt reaches the
/// runtime, so a process that dies mid-send leaves a record of what it was trying to do. The
/// <see cref="State"/> then says how far it got: <c>admitted</c> (committed, not yet sent),
/// <c>completed</c> (the turn finished and its checkpoint committed), <c>failed</c> (the runtime
/// reported an error), or <c>uncertain</c> (the send was issued but its outcome is unknown).
/// An uncertain admission is never replayed automatically: the caller reconciles it.
/// </remarks>
public sealed class InputAdmission
{
    /// <summary>The admission's identifier.</summary>
    public string? AdmissionId { get; set; }

    /// <summary>The prompt that was admitted.</summary>
    public string? Prompt { get; set; }

    /// <summary>The admission's state.</summary>
    public string? State { get; set; }

    /// <summary>When the admission was committed, as an ISO 8601 timestamp.</summary>
    public string? AdmittedAt { get; set; }

    /// <summary>When the admission reached a terminal state, as an ISO 8601 timestamp.</summary>
    public string? SettledAt { get; set; }
}

/// <summary>
/// The durable record of one checkpoint: a turn that finished, or a tool call that completed.
/// </summary>
/// <remarks>
/// Each checkpoint is a new version of the conversation's checkpoint document, so the document's
/// history is the checkpoint log. <see cref="EventId"/> is the deduplication key: a checkpoint
/// whose key is already the document's latest value is not written again, so a runtime that
/// delivers the same event twice does not produce two checkpoints.
/// </remarks>
public sealed class ConversationCheckpoint
{
    /// <summary>The checkpoint's identifier.</summary>
    public string? CheckpointId { get; set; }

    /// <summary>What the checkpoint records: <c>turn</c> or <c>tool</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>The deduplication key.</summary>
    public string? EventId { get; set; }

    /// <summary>The assistant message identifier, for a turn checkpoint.</summary>
    public string? MessageId { get; set; }

    /// <summary>The tool name, for a tool checkpoint.</summary>
    public string? ToolName { get; set; }

    /// <summary>The tool arguments, for a tool checkpoint.</summary>
    public string? ToolArgs { get; set; }

    /// <summary>Whether the tool call succeeded, for a tool checkpoint.</summary>
    public bool? Success { get; set; }

    /// <summary>The failure message, for a failed tool checkpoint.</summary>
    public string? Error { get; set; }

    /// <summary>The assistant's answer, for a turn checkpoint.</summary>
    public string? Content { get; set; }

    /// <summary>When the checkpoint was recorded, as an ISO 8601 timestamp.</summary>
    public string? RecordedAt { get; set; }
}

/// <summary>
/// Thrown when a conversation cannot be resumed because the native transcript of the session it
/// was bound to is gone.
/// </summary>
/// <remarks>
/// The durable conversation is intact — its entries, documents, and choices are all in the store.
/// What is missing is the runtime's own transcript. Continuing with a fresh, empty session would
/// silently discard the model's context, so the resume fails instead and the host decides what to
/// do: re-create the session deliberately, or surface the loss.
/// </remarks>
public sealed class DurableRecoveryRequiredException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public DurableRecoveryRequiredException(string conversationId, string? sessionId, Exception? innerException = null)
        : base(
            $"Conversation '{conversationId}' cannot be resumed: the recorded SDK session "
            + $"'{sessionId ?? "(none)"}' has no recoverable native transcript. The conversation "
            + "itself is intact in the store; re-create its session deliberately rather than "
            + "continuing with an empty replacement.",
            innerException)
    {
        ConversationId = conversationId;
        SessionId = sessionId;
    }

    /// <summary>The conversation that could not be resumed.</summary>
    public string ConversationId { get; }

    /// <summary>The session identifier that was recorded, when there was one.</summary>
    public string? SessionId { get; }
}

/// <summary>
/// Thrown when a stored sandbox policy cannot be applied to a session as written.
/// </summary>
/// <remarks>
/// The alternative to failing is worse than failing. A policy that is silently narrowed, or
/// silently dropped, produces a session that looks protected and is not: the conversation runs
/// with more authority than the store says it has, and nothing in the transcript records the
/// difference. So the session is not created and not resumed, and the host decides what to do —
/// correct the stored policy, or clear it deliberately to run the conversation unsandboxed.
/// </remarks>
public sealed class DurableSandboxPolicyException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">What about the policy cannot be applied.</param>
    /// <param name="policy">The stored policy that could not be applied.</param>
    /// <param name="innerException">The failure underneath, when there is one.</param>
    public DurableSandboxPolicyException(
        string reason,
        DurableSandboxPolicy policy,
        Exception? innerException = null)
        : base(
            $"The stored sandbox policy cannot be applied: {reason} The session was not created "
            + "or resumed, because running it without the protection the policy asks for would be "
            + "a silent downgrade. Correct the stored policy, or clear it deliberately to run the "
            + "conversation unsandboxed.",
            innerException)
    {
        Reason = reason;
        Policy = policy;
    }

    /// <summary>What about the policy cannot be applied.</summary>
    public string Reason { get; }

    /// <summary>The stored policy that could not be applied.</summary>
    public DurableSandboxPolicy Policy { get; }
}

/// <summary>The on-disk format of a durable store.</summary>
public enum DurableStoreFormat
{
    /// <summary>Sniff the file at open and pick the format that matches it.</summary>
    Auto,

    /// <summary>Keep the state in memory only; the filesystem is never touched.</summary>
    InMemory,

    /// <summary>One JSON record per line, appended on every commit.</summary>
    Jsonl,

    /// <summary>The single-file JSON snapshot format the store used before the JSONL backend.</summary>
    LegacyJson,
}

/// <summary>How a durable store is opened: where it lives and in what format.</summary>
public sealed class DurableStoreOptions
{
    /// <summary>The explicit file path. When null, the per-user default path is used.</summary>
    public string? Path { get; init; }

    /// <summary>The on-disk format. Auto sniffs the file at open.</summary>
    public DurableStoreFormat Format { get; init; } = DurableStoreFormat.Auto;

    /// <summary>The file name used under the per-user default directory.</summary>
    public string FileName { get; init; } = "durable-store.jsonl";

    /// <summary>
    /// A factory for the provider that backs the store. When set, it takes precedence
    /// over <see cref="ProviderName"/> and over <see cref="Format"/>: the store is
    /// built over the provider the factory returns instead of one of the built-in
    /// file formats.
    /// </summary>
    /// <remarks>
    /// The factory receives these options, so a provider can read whatever it needs
    /// from them. It is called once per store, and the provider it returns is owned
    /// and disposed by that store.
    /// </remarks>
    public Func<DurableStoreOptions, IDurableStoreProvider>? Provider { get; init; }

    /// <summary>
    /// The name of a provider registered with <see cref="DurableStore.RegisterProvider"/>.
    /// When set and <see cref="Provider"/> is not, the store is built over the
    /// registered provider.
    /// </summary>
    public string? ProviderName { get; init; }
}

/// <summary>
/// The persistence seam of the durable store: the operations a backend has to
/// provide so the store can keep its documents, tasks, conversations, and entries.
/// The store owns the commit gate; the provider owns the state and the undo log
/// that protects it.
/// </summary>
/// <remarks>
/// A provider is disposable because a persistent backend holds a resource for the
/// store's lifetime: the exclusive lock on the store file. Disposing the store
/// releases that lock, which is what lets the next process open the same file.
/// </remarks>
public interface IDurableStoreProvider : IDisposable
{
    /// <summary>
    /// Whether the provider's state is persisted to the filesystem. A provider that
    /// is not persistent keeps its state in the process only, and that state is lost
    /// when the process ends.
    /// </summary>
    bool IsPersistent { get; }

    /// <summary>Loads the committed state, or starts empty when there is none.</summary>
    void Open(DurableStoreOptions options);

    /// <summary>Starts the undo log for the transaction in flight.</summary>
    void BeginTransaction();

    /// <summary>Undoes everything the failed transaction wrote.</summary>
    void Rollback();

    /// <summary>Flushes the committed transaction to the backend.</summary>
    void Commit();

    /// <summary>The monotonic write sequence.</summary>
    long Sequence { get; }

    /// <summary>The conversations known to the store.</summary>
    IReadOnlyCollection<ConversationRecord> Conversations { get; }

    /// <summary>The tasks known to the store.</summary>
    IReadOnlyCollection<DurableTaskRecord> Tasks { get; }

    /// <summary>The documents known to the store.</summary>
    IReadOnlyCollection<DocumentState> Documents { get; }

    /// <summary>Reads a conversation record.</summary>
    ConversationRecord? Conversation(string id);

    /// <summary>Reads a task record.</summary>
    DurableTaskRecord? Task(string id);

    /// <summary>Reads a document's state.</summary>
    DocumentState? Document(string kind, string ownerId);

    /// <summary>Reads the entry ids of a conversation, oldest first.</summary>
    IReadOnlyList<string> Entries(string conversationId);

    /// <summary>The write sequence of an entry, used to order document versions against it.</summary>
    long SequenceOf(string entryId);

    /// <summary>Advances the write sequence and returns the new value.</summary>
    long NextSequence();

    /// <summary>Adds a conversation record, replacing any record with the same id.</summary>
    void AddConversation(ConversationRecord record);

    /// <summary>Appends an entry to a conversation.</summary>
    void AddEntry(string conversationId, string entryId);

    /// <summary>Copies the entries of one conversation into another, up to a fork point.</summary>
    void CopyEntries(string fromConversationId, string toConversationId, string? forkEntryId);

    /// <summary>Reads a document's state, creating it on first use.</summary>
    DocumentState EnsureDocument(string kind, string ownerId, ForkPolicy fork);

    /// <summary>
    /// Reads a document's state, creating it on first use and remembering the document's
    /// initial value. The initial value is what a <see cref="ForkPolicy.Initial"/> fork
    /// inherits, so it has to be recorded alongside the document, not recomputed later.
    /// </summary>
    DocumentState EnsureDocument(string kind, string ownerId, ForkPolicy fork, JsonElement? initial) =>
        EnsureDocument(kind, ownerId, fork);

    /// <summary>Records a committed version of a document.</summary>
    void RecordDocumentVersion(DocumentState state, string entryId, JsonElement value);

    /// <summary>Copies a document into another owner, honouring its fork policy.</summary>
    void CopyDocument(DocumentState source, string toOwner, string? forkEntryId);

    /// <summary>Adds a task record, replacing any record with the same id.</summary>
    DurableTaskRecord AddTask(DurableTaskRecord record);

    /// <summary>
    /// Records the current state of a task that was mutated in place. Task records
    /// change after they are created -- a checkpoint, a status, a memo -- so a
    /// provider that only saw <see cref="AddTask"/> would never learn about those
    /// changes and would lose the task's progress across a reopen.
    /// </summary>
    void UpdateTask(DurableTaskRecord record);
}

/// <summary>
/// A captured copy of a task's mutable state. The provider keeps the last state it
/// was told about, so a transaction that rolls back can put the task back where it
/// was before the transaction started.
/// </summary>
internal sealed record TaskStateSnapshot(
    TaskStatus Status,
    JsonElement Checkpoint,
    JsonElement? Outcome,
    List<string> WaitingOn,
    Dictionary<string, JsonElement> Memos)
{
    /// <summary>Captures the current state of a task.</summary>
    public static TaskStateSnapshot Capture(DurableTaskRecord task) => new(
        task.Status,
        task.Checkpoint,
        task.Outcome,
        task.WaitingOn.ToList(),
        task.Memos.ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>Puts a task back into this state.</summary>
    public void ApplyTo(DurableTaskRecord task)
    {
        task.Status = Status;
        task.Checkpoint = Checkpoint;
        task.WaitingOn.Clear();
        task.WaitingOn.AddRange(WaitingOn);
        task.Outcome = Outcome;
        task.Memos.Clear();
        foreach (var (key, value) in Memos)
        {
            task.Memos[key] = value;
        }
    }
}

/// <summary>
/// The in-memory provider: the store's state lives in the process and the
/// filesystem is never touched. This is the provider behind the default
/// <c>new DurableStore()</c> path.
/// </summary>
public class InMemoryStoreProvider : IDurableStoreProvider
{
    private readonly Dictionary<string, DocumentState> _documents = [];
    private readonly Dictionary<string, DurableTaskRecord> _tasks = [];
    private readonly Dictionary<string, ConversationRecord> _conversations = [];

    // The entry state is shared with the JSONL provider, which diffs the committed
    // entry lists and records against the file at commit time.
    protected readonly Dictionary<string, List<string>> _entries = [];
    protected readonly Dictionary<string, EntryRecord> _entryRecords = [];

    private readonly Dictionary<string, TaskStateSnapshot> _taskStates = [];
    // The sequence counter is shared with the JSONL provider, which replays the
    // envelope sequences of the file's records on open.
    protected long _sequence;

    /// <summary>
    /// The undo log of the transaction in flight. A transaction writes straight into
    /// the provider's collections, so a throw has to undo what it wrote. The log is
    /// null when no transaction is running.
    /// </summary>
    private UndoLog? _undo;

    /// <summary>
    /// Whether the provider's state is persisted. The in-memory provider is the
    /// ephemeral tier: its state lives in the process and is lost when the process
    /// ends.
    /// </summary>
    public virtual bool IsPersistent => false;

    /// <summary>Starts empty. The in-memory provider never touches the filesystem.</summary>
    public virtual void Open(DurableStoreOptions options)
    {
    }

    /// <summary>
    /// Releases whatever the provider holds for the store's lifetime. The in-memory
    /// provider holds nothing, so this is a no-op; the persistent providers release
    /// the exclusive lock on the store file.
    /// </summary>
    public virtual void Dispose()
    {
    }

    /// <summary>Starts the undo log for the transaction in flight.</summary>
    public void BeginTransaction() => _undo = new UndoLog();

    /// <summary>Undoes everything the failed transaction wrote.</summary>
    public void Rollback()
    {
        _undo?.Undo(this);
        _undo = null;
    }

    /// <summary>Ends the transaction and flushes the state to the backend.</summary>
    public virtual void Commit()
    {
        _undo = null;
        Persist();
    }

    /// <summary>
    /// Ends the transaction in flight without undoing it. A provider whose
    /// <see cref="Persist"/> can fail keeps the undo log until the backend has accepted
    /// the write, then calls this to discard it; a failed write is undone with
    /// <see cref="Rollback"/> instead, so the provider is left exactly as it was.
    /// </summary>
    protected void EndTransaction() => _undo = null;

    /// <summary>The monotonic write sequence.</summary>
    public long Sequence => _sequence;

    /// <summary>The conversations known to the store.</summary>
    public IReadOnlyCollection<ConversationRecord> Conversations => _conversations.Values;

    /// <summary>The tasks known to the store.</summary>
    public IReadOnlyCollection<DurableTaskRecord> Tasks => _tasks.Values;

    /// <summary>The documents known to the store.</summary>
    public IReadOnlyCollection<DocumentState> Documents => _documents.Values;

    /// <summary>Reads a conversation record.</summary>
    public ConversationRecord? Conversation(string id) => _conversations.GetValueOrDefault(id);

    /// <summary>Reads a task record.</summary>
    public DurableTaskRecord? Task(string id) => _tasks.GetValueOrDefault(id);

    /// <summary>Reads a document's state.</summary>
    public DocumentState? Document(string kind, string ownerId) =>
        _documents.TryGetValue(DocumentKey(kind, ownerId), out var state) ? state : null;

    /// <summary>Reads the entry ids of a conversation, oldest first.</summary>
    public IReadOnlyList<string> Entries(string conversationId) =>
        _entries.TryGetValue(conversationId, out var entries) ? entries : [];

    /// <summary>The write sequence of an entry, used to order document versions against it.</summary>
    public long SequenceOf(string entryId) =>
        _entryRecords.TryGetValue(entryId, out var record) ? record.Sequence : long.MaxValue;

    /// <summary>Advances the write sequence and returns the new value.</summary>
    public long NextSequence()
    {
        var sequence = ++_sequence;
        _undo?.Sequence(() => _sequence = sequence - 1);
        return sequence;
    }

    /// <summary>Adds a conversation record, replacing any record with the same id.</summary>
    public void AddConversation(ConversationRecord record)
    {
        _undo?.Remove(_conversations, record.Id);
        _conversations[record.Id] = record;
    }

    /// <summary>Appends an entry to a conversation.</summary>
    public void AddEntry(string conversationId, string entryId)
    {
        if (!_entries.TryGetValue(conversationId, out var entries))
        {
            entries = [];
            _entries[conversationId] = entries;
            _undo?.Remove(_entries, conversationId);
        }

        _undo?.Append(entries);
        entries.Add(entryId);
        _undo?.Remove(_entryRecords, entryId);
        _entryRecords[entryId] = new EntryRecord(entryId, conversationId, "entry", null, NextSequence());
    }

    /// <summary>
    /// Copies the parent's entries up to and including <paramref name="forkEntryId"/> into the fork.
    /// A fork inherits the parent's transcript up to the entry it was taken at.
    /// </summary>
    public void CopyEntries(string fromConversationId, string toConversationId, string? forkEntryId)
    {
        var source = Entries(fromConversationId);
        var inherited = forkEntryId is null
            ? source.ToList()
            : source.TakeWhile(entry => entry != forkEntryId).Append(forkEntryId).ToList();

        _undo?.Remove(_entries, toConversationId);
        _entries[toConversationId] = inherited;
    }

    /// <summary>Reads a document's state, creating it on first use.</summary>
    public DocumentState EnsureDocument(string kind, string ownerId, ForkPolicy fork) =>
        EnsureDocument(kind, ownerId, fork, initial: null);

    /// <summary>Reads a document's state, creating it on first use and remembering its initial value.</summary>
    public DocumentState EnsureDocument(string kind, string ownerId, ForkPolicy fork, JsonElement? initial)
    {
        var key = DocumentKey(kind, ownerId);
        if (!_documents.TryGetValue(key, out var state))
        {
            state = new DocumentState(kind, ownerId, fork, initial?.Clone());
            _documents[key] = state;
            _undo?.Remove(_documents, key);
        }

        return state;
    }

    /// <summary>Records a committed version of a document.</summary>
    public void RecordDocumentVersion(DocumentState state, string entryId, JsonElement value)
    {
        _undo?.Append(state.Versions);
        state.Versions.Add(new DocumentVersion(entryId, value.Clone(), NextSequence()));
    }

    /// <summary>Copies a document into another owner, honouring its fork policy.</summary>
    public void CopyDocument(DocumentState source, string toOwner, string? forkEntryId)
    {
        var target = EnsureDocument(source.Kind, toOwner, source.Fork, source.Initial);
        JsonElement? value = source.Fork switch
        {
            ForkPolicy.AsOf => forkEntryId is null ? source.Latest : source.AsOfOrBefore(forkEntryId, SequenceOf),
            ForkPolicy.Current => source.Latest,
            ForkPolicy.Initial => source.Initial,
            _ => null,
        };

        if (value is { } element)
        {
            _undo?.Append(target.Versions);
            target.Versions.Add(new DocumentVersion(forkEntryId ?? string.Empty, element.Clone(), NextSequence()));
        }
    }

    /// <summary>Adds a task record, replacing any record with the same id.</summary>
    public DurableTaskRecord AddTask(DurableTaskRecord record)
    {
        _undo?.Remove(_tasks, record.Id);
        _tasks[record.Id] = record;
        _taskStates[record.Id] = TaskStateSnapshot.Capture(record);
        return record;
    }

    /// <summary>
    /// Records the current state of a task that was mutated in place. The record was
    /// changed after it was added, so the provider is told about the new state; a
    /// transaction that fails puts the task back where it was.
    /// </summary>
    public void UpdateTask(DurableTaskRecord record)
    {
        var current = TaskStateSnapshot.Capture(record);
        if (_undo is not null && _taskStates.TryGetValue(record.Id, out var previous))
        {
            _undo.Restore(previous, record);
        }

        _taskStates[record.Id] = current;
    }

    /// <summary>The key a document is stored under.</summary>
    internal static string DocumentKey(string kind, string ownerId) => $"{kind}@{ownerId}";

    /// <summary>Flushes the state to the backend. The in-memory provider has nothing to flush.</summary>
    protected virtual void Persist()
    {
    }

    /// <summary>Builds a snapshot of the current state, for providers that persist a whole file.</summary>
    protected virtual StoreSnapshot CreateSnapshot() => new()
    {
        Sequence = _sequence,
        Conversations = _conversations.Values.ToList(),
        Tasks = _tasks.Values.ToList(),
        Documents = _documents.Values.Select(d => new DocumentSnapshot
        {
            Kind = d.Kind,
            OwnerId = d.OwnerId,
            Fork = d.Fork,
            Initial = d.Initial?.Clone(),
            Versions = d.Versions.Select(v => new DocumentVersionSnapshot
            {
                EntryId = v.EntryId,
                Sequence = v.Sequence,
                Value = v.Value.Clone(),
            }).ToList(),
        }).ToList(),
        Entries = _entries.ToDictionary(pair => pair.Key, pair => pair.Value),
        EntryRecords = _entryRecords.Values.ToList(),
    };

    /// <summary>Restores the state from a snapshot.</summary>
    protected virtual void RestoreSnapshot(StoreSnapshot snapshot)
    {
        _sequence = snapshot.Sequence;
        foreach (var conversation in snapshot.Conversations)
        {
            _conversations[conversation.Id] = conversation;
        }

        foreach (var task in snapshot.Tasks)
        {
            _tasks[task.Id] = task;
            _taskStates[task.Id] = TaskStateSnapshot.Capture(task);
        }

        foreach (var document in snapshot.Documents)
        {
            var state = new DocumentState(document.Kind, document.OwnerId, document.Fork, document.Initial?.Clone());
            foreach (var version in document.Versions)
            {
                state.Versions.Add(new DocumentVersion(version.EntryId, version.Value.Clone(), version.Sequence));
            }

            _documents[DocumentKey(document.Kind, document.OwnerId)] = state;
        }

        foreach (var pair in snapshot.Entries)
        {
            _entries[pair.Key] = pair.Value;
        }

        foreach (var record in snapshot.EntryRecords)
        {
            _entryRecords[record.Id] = record;
        }
    }
}

/// <summary>
/// The exclusive lock a persistent provider holds on its store file for the store's
/// lifetime. The lock is a sidecar file next to the store -- <c>{path}.lock</c> --
/// opened with <see cref="FileShare.None"/>, so the operating system releases it when
/// the process exits, even after a crash. The sidecar carries the owning process id
/// and start time so a refused open can say who holds the file.
/// </summary>
internal sealed class StoreFileLock : IDisposable
{
    private readonly string _lockPath;
    private readonly string _ownerPath;
    private FileStream? _handle;

    private StoreFileLock(string lockPath, string ownerPath, FileStream handle)
    {
        _lockPath = lockPath;
        _ownerPath = ownerPath;
        _handle = handle;
    }

    /// <summary>
    /// Takes the lock for <paramref name="storePath"/>, or throws naming the store
    /// file, the lock file, and the process that already holds it.
    /// </summary>
    public static StoreFileLock Acquire(string storePath)
    {
        var fullPath = Path.GetFullPath(storePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lockPath = fullPath + ".lock";
        var ownerPath = fullPath + ".lock.owner";
        FileStream handle;
        try
        {
            handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"The durable store file '{fullPath}' is already open in another process. " +
                $"Its lock file '{lockPath}' is held{DescribeOwner(ownerPath)}. " +
                "A store file supports a single writer at a time; close the other store, " +
                "or point this one at a different path.",
                exception);
        }

        try
        {
            // The owner note lives beside the lock rather than inside it: the lock is
            // held with FileShare.None, so a refused writer could not read it back.
            File.WriteAllText(ownerPath, $"{Environment.ProcessId} {DateTimeOffset.UtcNow:O}");
        }
        catch (IOException)
        {
            // The lock is what matters; the owner note is a diagnostic nicety.
        }

        return new StoreFileLock(lockPath, ownerPath, handle);
    }

    private static string DescribeOwner(string ownerPath)
    {
        try
        {
            var text = File.ReadAllText(ownerPath).Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            var parts = text.Split(' ', 2);
            return parts.Length == 2 && int.TryParse(parts[0], out var pid)
                ? $" by process {pid} (since {parts[1]})"
                : $" (owner: {text})";
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Releases the lock and removes the sidecar.</summary>
    public void Dispose()
    {
        var handle = _handle;
        if (handle is null)
        {
            return;
        }

        _handle = null;
        handle.Dispose();

        try
        {
            File.Delete(_lockPath);
            File.Delete(_ownerPath);
        }
        catch (IOException)
        {
            // A leftover sidecar is harmless: the next open truncates and rewrites it.
        }
    }
}

/// <summary>
/// The provider that keeps the store in the single-file JSON snapshot format the
/// store used before the JSONL backend. This is the persistence behind
/// <c>new DurableStore(path)</c>, and the format <see cref="DurableStoreFormat.Auto"/>
/// selects for a file that already holds a legacy snapshot.
/// </summary>
/// <remarks>
/// The reader is backward compatible and non-destructive: opening a legacy file
/// loads it into memory and never rewrites it, so a store that is only read keeps
/// its bytes. A commit writes the whole snapshot back in the same legacy format,
/// which is what <see cref="DurableStoreFormat.Auto"/> promises -- it never
/// converts a file behind the caller's back. Converting a legacy file to JSONL is
/// the explicit opt-in of opening it with <see cref="DurableStoreFormat.Jsonl"/>.
/// A file that is not a legacy snapshot raises an <see cref="InvalidDataException"/>
/// naming the path and the reason, rather than opening as an empty store.
/// </remarks>
internal sealed class LegacyJsonStoreProvider : InMemoryStoreProvider
{
    private string? _path;
    private StoreFileLock? _lock;

    /// <summary>
    /// Whether the provider's state is persisted. The legacy provider writes the
    /// single-file JSON snapshot to the configured path.
    /// </summary>
    public override bool IsPersistent => true;

    /// <summary>
    /// Takes the exclusive lock on the store file, then loads the snapshot at the
    /// configured path, when there is one. A file that holds JSONL records is
    /// refused before the lock is taken, because reading it as a legacy snapshot
    /// would start empty and the first commit would replace the records.
    /// </summary>
    public override void Open(DurableStoreOptions options)
    {
        _path = options.Path;
        if (_path is null)
        {
            return;
        }

        if (File.Exists(_path) && DurableStore.DetectFormat(_path) is DurableStoreFormat.Jsonl)
        {
            throw new InvalidDataException(
                $"The durable store file '{_path}' holds JSONL records, not a legacy JSON snapshot. " +
                "Open it with DurableStoreFormat.Jsonl or DurableStoreFormat.Auto instead of DurableStoreFormat.LegacyJson.");
        }

        _lock = StoreFileLock.Acquire(_path);
        if (File.Exists(_path))
        {
            RestoreSnapshot(DurableStore.ReadLegacySnapshot(_path));
        }
    }

    /// <summary>Releases the exclusive lock on the store file.</summary>
    public override void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>Writes the whole snapshot to the file, atomically.</summary>
    protected override void Persist()
    {
        if (_path is null)
        {
            return;
        }

        var snapshot = CreateSnapshot();

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, DurableJson.Options));
        File.Move(temporary, _path, overwrite: true);
    }
}

/// <summary>
/// The provider that persists state as JSONL: one JSON record per line, appended
/// on every commit and flushed to disk before the commit returns. A torn
/// trailing line -- a partial write from a crash -- is discarded on open and the
/// file is truncated back to the last complete record, so recovery is silent and
/// non-fatal.
/// </summary>
internal sealed class JsonlStoreProvider : InMemoryStoreProvider
{
    private string? _path;
    private StoreFileLock? _lock;

    /// <summary>
    /// Whether the provider's state is persisted. The JSONL provider appends its
    /// records to the configured path.
    /// </summary>
    public override bool IsPersistent => true;

    /// <summary>
    /// Takes the exclusive lock on the store file, then loads the JSONL file at the
    /// configured path, replaying every complete record into memory. A torn final
    /// line is discarded and truncated away; a file that does not parse as JSONL at
    /// all is an error, because <see cref="DurableStoreFormat.Jsonl"/> is an explicit
    /// format choice, not a guess.
    /// </summary>
    /// <remarks>
    /// A path that holds a legacy single-file snapshot is the explicit "convert this
    /// store" case: the snapshot is read into memory on open, and the first commit
    /// writes JSONL over the legacy file. Opening alone never rewrites it.
    /// </remarks>
    public override void Open(DurableStoreOptions options)
    {
        _path = options.Path;
        if (_path is null)
        {
            return;
        }

        _lock = StoreFileLock.Acquire(_path);
        if (!File.Exists(_path))
        {
            return;
        }

        if (DurableStore.DetectFormat(_path) is DurableStoreFormat.LegacyJson)
        {
            // Convert on open, into memory only. The committed baseline stays empty
            // so the first commit emits the whole state as JSONL records.
            RestoreSnapshot(DurableStore.ReadLegacySnapshot(_path));
            _replaceOnCommit = true;
            return;
        }

        Replay(_path);
    }

    /// <summary>Releases the exclusive lock on the store file.</summary>
    public override void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>Appends the records of the committed transaction to the file, flushed.</summary>
    protected override void Persist()
    {
        if (_path is null)
        {
            return;
        }

        var records = TakePendingRecords();
        if (records.Count == 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(
            _path,
            _replaceOnCommit ? FileMode.Create : FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var record in records)
        {
            writer.WriteLine(JsonSerializer.Serialize(record, DurableJson.Options));
        }

        writer.Flush();
        stream.Flush(true);
        _replaceOnCommit = false;
    }

    /// <summary>
    /// Reads the file line by line, replaying every complete record. A line is
    /// complete when it is <c>\n</c>-terminated and parses as a JSON object; the
    /// torn tail is discarded and the file is truncated back to the last complete
    /// record boundary at open. A file that does not parse as JSONL at all is not
    /// a torn tail and raises a clear error.
    /// </summary>
    private void Replay(string path)
    {
        long? truncateAfter = null;
        using (var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false))
        {
            var offset = 0L;
            while (reader.ReadLine() is { } line)
            {
                // ReadLine consumes the terminator, so the byte offset of the next
                // line is the offset after this line's text plus its newline.
                var lineEnd = offset + Encoding.UTF8.GetByteCount(line) + 1;
                JsonlRecord? record = null;
                try
                {
                    record = JsonSerializer.Deserialize<JsonlRecord>(line, DurableJson.Options);
                }
                catch (JsonException)
                {
                    // Deserialization throws on unparseable content; treat that the
                    // same as a null result so the guard below can classify it.
                    record = null;
                }

                if (record is not { } parsed || parsed.Type is null)
                {
                    // A line that does not parse is a torn tail only when it is the
                    // final one; anything earlier means the file is not JSONL.
                    if (!reader.EndOfStream)
                    {
                        throw new InvalidDataException(
                            $"The file '{path}' is not a valid JSONL durable store: line at byte offset {offset} does not parse as a JSONL record.");
                    }

                    break;
                }

                Apply(record);
                offset = lineEnd;
                truncateAfter = offset;
            }
        }

        if (truncateAfter is { } boundary && new FileInfo(path).Length != boundary)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            stream.SetLength(boundary);
        }

        SeedCommittedFromReplay();
    }

    /// <summary>Applies one replayed record to the in-memory state.</summary>
    private void Apply(JsonlRecord record)
    {
        _sequence = Math.Max(_sequence, record.Seq);
        switch (record.Type)
        {
            case "conversation":
                AddConversation(record.Payload.Deserialize<ConversationRecord>(DurableJson.Options)!);
                break;
            case "entry":
                var conversationId = record.Payload.GetProperty("conversationId").GetString()!;
                var entryId = record.Payload.GetProperty("entryId").GetString()!;
                if (!_entries.TryGetValue(conversationId, out var entries))
                {
                    entries = [];
                    _entries[conversationId] = entries;
                }

                entries.Add(entryId);
                break;
            case "entry-record":
                var entry = record.Payload.Deserialize<EntryRecord>(DurableJson.Options)!;
                _entryRecords[entry.Id] = entry;
                _sequence = Math.Max(_sequence, entry.Sequence);
                break;
            case "document":
                EnsureDocument(
                    record.Payload.GetProperty("kind").GetString()!,
                    record.Payload.GetProperty("ownerId").GetString()!,
                    record.Payload.GetProperty("fork").Deserialize<ForkPolicy>(DurableJson.Options),
                    // Older records predate the initial value; a missing property is not an error.
                    record.Payload.TryGetProperty("initial", out var initial) ? initial.Clone() : null);
                break;
            case "document-version":
                // A version carries the sequence RecordDocumentVersion minted for it,
                // which is not the envelope sequence: the envelope is the store's
                // sequence at commit time, and a commit can mint several sequences.
                var state = EnsureDocument(
                    record.Payload.GetProperty("kind").GetString()!,
                    record.Payload.GetProperty("ownerId").GetString()!,
                    record.Payload.GetProperty("fork").Deserialize<ForkPolicy>(DurableJson.Options));
                state.Versions.Add(new DocumentVersion(
                    record.Payload.GetProperty("entryId").GetString()!,
                    record.Payload.GetProperty("value").Clone(),
                    record.Payload.TryGetProperty("sequence", out var versionSequence)
                        ? versionSequence.GetInt64()
                        : record.Seq));
                break;
            case "task":
                AddTask(record.Payload.Deserialize<DurableTaskRecord>(DurableJson.Options)!);
                break;
        }
    }

    /// <summary>
    /// Seeds the committed baseline from the replayed file state, so the first
    /// commit after a reopen diffs against what is already persisted instead of
    /// re-emitting every existing record as a duplicate append.
    /// </summary>
    private void SeedCommittedFromReplay()
    {
        RememberCommitted();
    }

    /// <summary>
    /// Captures the records this transaction wrote, in write order. The base class
    /// wrote straight into its collections, so the writes are read back off the
    /// state and diffed against what the last commit already persisted.
    /// </summary>
    private List<JsonlRecord> TakePendingRecords()
    {
        var records = new List<JsonlRecord>();
        foreach (var conversation in Conversations)
        {
            if (!_emittedConversations.Add(conversation.Id))
            {
                continue;
            }

            records.Add(JsonlRecord.For("conversation", Sequence, conversation));
        }

        foreach (var (conversationId, entryIds) in _entries)
        {
            var knownCount = _committedEntries.GetValueOrDefault(conversationId)?.Count ?? 0;
            foreach (var entryId in entryIds.Skip(knownCount))
            {
                records.Add(JsonlRecord.For("entry", Sequence, new JsonlEntryRef(conversationId, entryId)));
                if (_entryRecords.TryGetValue(entryId, out var record))
                {
                    records.Add(JsonlRecord.For("entry-record", Sequence, record));
                }
            }
        }

        foreach (var document in Documents)
        {
            var key = DocumentKey(document.Kind, document.OwnerId);
            if (_emittedDocuments.Add(key))
            {
                records.Add(JsonlRecord.For("document", Sequence, new JsonlDocumentRef(
                    document.Kind, document.OwnerId, document.Fork, document.Initial?.Clone())));
            }

            var committed = _committedVersions.GetValueOrDefault(key);
            foreach (var version in document.Versions.Skip(committed))
            {
                records.Add(JsonlRecord.For("document-version", Sequence, new JsonlDocumentVersion(
                    document.Kind, document.OwnerId, document.Fork, version.EntryId, version.Sequence, version.Value.Clone())));
            }
        }

        foreach (var task in Tasks)
        {
            var current = JsonSerializer.Serialize(task, DurableJson.Options);
            if (!_emittedTasks.Add(task.Id) && _committedTasks.GetValueOrDefault(task.Id) == current)
            {
                continue;
            }

            _committedTasks[task.Id] = current;
            records.Add(JsonlRecord.For("task", Sequence, task));
        }

        RememberCommitted();
        return records;
    }

    /// <summary>Records the state the next commit diffs against.</summary>
    private void RememberCommitted()
    {
        _emittedConversations.Clear();
        _emittedConversations.UnionWith(Conversations.Select(c => c.Id));
        _emittedTasks.Clear();
        _emittedTasks.UnionWith(Tasks.Select(t => t.Id));
        _committedTasks = Tasks.ToDictionary(
            task => task.Id,
            task => JsonSerializer.Serialize(task, DurableJson.Options));
        _committedEntries = _entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        _committedVersions = Documents.ToDictionary(
            document => DocumentKey(document.Kind, document.OwnerId),
            document => document.Versions.Count);
    }

    private readonly HashSet<string> _emittedConversations = [];
    private readonly HashSet<string> _emittedTasks = [];
    private readonly HashSet<string> _emittedDocuments = [];
    private Dictionary<string, List<string>> _committedEntries = [];
    private Dictionary<string, int> _committedVersions = [];
    private Dictionary<string, string> _committedTasks = [];

    /// <summary>
    /// Whether the next commit replaces the file instead of appending to it. Set
    /// when the path held a legacy snapshot that was converted into memory on open.
    /// </summary>
    private bool _replaceOnCommit;

    /// <summary>One JSONL record: the envelope plus its payload.</summary>
    private sealed record JsonlRecord(long V, long Seq, string Type, JsonElement Payload)
    {
        public static JsonlRecord For(string type, long seq, object payload) => new(
            1, seq, type, DurableJson.ToElement(payload));
    }

    private sealed record JsonlEntryRef(string ConversationId, string EntryId);

    private sealed record JsonlDocumentRef(string Kind, string OwnerId, ForkPolicy Fork, JsonElement? Initial);

    private sealed record JsonlDocumentVersion(
        string Kind, string OwnerId, ForkPolicy Fork, string EntryId, long Sequence, JsonElement Value);
}

/// <summary>
/// The durable store: documents, tasks, conversations, and entries, persisted atomically.
/// </summary>
/// <remarks>
/// A persistent store holds an exclusive lock on its file for the store's lifetime, so
/// dispose the store to release the file for the next process. A store file supports a
/// single writer at a time; a second open of the same path fails at open rather than
/// interleaving writes.
/// </remarks>
public sealed class DurableStore : IDisposable
{
    private static readonly ConcurrentDictionary<string, Func<DurableStoreOptions, IDurableStoreProvider>> ProviderFactories =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IDurableStoreProvider _provider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _path;
    private readonly DurableStoreFormat _format;

    /// <summary>
    /// Registers a provider factory under a name, so a store can be opened over that
    /// provider with <see cref="DurableStoreOptions.ProviderName"/>. Registering the
    /// same name again replaces the factory.
    /// </summary>
    /// <remarks>
    /// This is additive surface: the built-in formats keep the arms they had, and a
    /// store that names no provider is created exactly as before. Registration is
    /// process-wide, so a host that embeds the durable layer registers once at startup
    /// — or references a package that registers on the host's behalf — and thereafter
    /// selects the provider by name in the options it already passes to
    /// <see cref="Create(DurableStoreOptions)"/>.
    /// <para>
    /// The factory is called once per store and receives the options the store was
    /// created with. The provider it returns is owned and disposed by that store.
    /// </para>
    /// </remarks>
    public static void RegisterProvider(string name, Func<DurableStoreOptions, IDurableStoreProvider> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        ProviderFactories[name] = factory;
    }

    /// <summary>
    /// Removes a provider factory registered with <see cref="RegisterProvider"/>.
    /// </summary>
    /// <returns><c>true</c> when a factory was registered under the name.</returns>
    public static bool UnregisterProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ProviderFactories.TryRemove(name, out _);
    }

    /// <summary>Whether a provider is registered under the name.</summary>
    public static bool IsProviderRegistered(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ProviderFactories.ContainsKey(name);
    }

    /// <summary>Creates a store. When <paramref name="path"/> is given, state is persisted to that file.</summary>
    public DurableStore(string? path = null)
    {
        _provider = path is null
            ? new InMemoryStoreProvider()
            : new LegacyJsonStoreProvider();

        _path = path;
        _format = path is null ? DurableStoreFormat.InMemory : DurableStoreFormat.LegacyJson;

        _provider.Open(new DurableStoreOptions
        {
            Path = path,
            Format = _format,
        });
    }

    /// <summary>
    /// Creates a store over the provider the options select. This is the entry point
    /// for the pluggable backends; the constructor keeps the original single-file
    /// behaviour. When the caller asks for a persistent store and supplies no path,
    /// the per-user default path is used.
    /// </summary>
    /// <remarks>
    /// The default options select the durable tier: <c>Create(new DurableStoreOptions())</c>
    /// opens the per-user default path and persists there. The ephemeral tier is
    /// opt-in through <see cref="CreateEphemeral"/> or an explicit
    /// <see cref="DurableStoreFormat.InMemory"/> format.
    /// </remarks>
    public static DurableStore Create(DurableStoreOptions options)
    {
        if (options.Path is null && options.Format is not DurableStoreFormat.InMemory)
        {
            options = new DurableStoreOptions
            {
                Path = DefaultStorePath(options.FileName),
                Format = options.Format,
                FileName = options.FileName,
                Provider = options.Provider,
                ProviderName = options.ProviderName,
            };
        }

        IDurableStoreProvider provider = SelectProvider(options);

        provider.Open(options);
        return new DurableStore(provider, options.Path, options.Format);
    }

    /// <summary>
    /// Picks the provider the options select. An explicit factory wins, then a
    /// registered provider name, then the built-in formats — so the seam only ever
    /// adds a way to name a provider, and every existing caller keeps the provider
    /// it had.
    /// </summary>
    private static IDurableStoreProvider SelectProvider(DurableStoreOptions options)
    {
        if (options.Provider is not null)
        {
            return options.Provider(options);
        }

        if (options.ProviderName is not null)
        {
            if (!ProviderFactories.TryGetValue(options.ProviderName, out var factory))
            {
                throw new InvalidOperationException(
                    $"No durable store provider is registered under the name '{options.ProviderName}'. " +
                    "Register one with DurableStore.RegisterProvider before creating the store.");
            }

            return factory(options);
        }

        return options.Format switch
        {
            DurableStoreFormat.InMemory => new InMemoryStoreProvider(),
            DurableStoreFormat.LegacyJson => new LegacyJsonStoreProvider(),
            DurableStoreFormat.Jsonl => new JsonlStoreProvider(),
            _ => CreateProviderForAuto(options.Path),
        };
    }

    /// <summary>
    /// Creates an ephemeral store: the in-memory tier. Its state lives in the
    /// process, the filesystem is never touched, and the state is lost when the
    /// process ends. This is the named counterpart of the persistent tier that
    /// <see cref="Create(DurableStoreOptions)"/> selects by default.
    /// </summary>
    public static DurableStore CreateEphemeral() =>
        Create(new DurableStoreOptions { Format = DurableStoreFormat.InMemory });

    /// <summary>
    /// Whether the store's state is persisted. A persistent store survives the
    /// process; an ephemeral one does not. The tier is a property of the provider
    /// behind the store, so this reports what the store actually does rather than
    /// what the caller asked for.
    /// </summary>
    public bool IsPersistent => _provider.IsPersistent;

    /// <summary>
    /// Picks the provider for <see cref="DurableStoreFormat.Auto"/>. A file that
    /// does not exist yet is written in the canonical JSONL format; a file that
    /// does exist is read in the format it was written in.
    /// </summary>
    private static IDurableStoreProvider CreateProviderForAuto(string? path)
    {
        // Auto without a path means the in-memory tier. <see cref="Create"/>
        // resolves the per-user default path for every non-InMemory format, so
        // this branch is a guard rather than a reachable case.
        if (path is null)
        {
            return new InMemoryStoreProvider();
        }

        return DetectFormat(path) is DurableStoreFormat.Jsonl
            ? new JsonlStoreProvider()
            : new LegacyJsonStoreProvider();
    }

    /// <summary>
    /// Detects the format of the file at the path. A file that does not exist, or
    /// that holds no content, is written in the canonical JSONL format. A file
    /// whose first non-empty line is a JSON object with a string <c>type</c>
    /// property is a JSONL record envelope; a file that parses as a single JSON
    /// object is the legacy single-file snapshot. Anything else raises an
    /// <see cref="InvalidDataException"/> naming the path and the reason, so a
    /// corrupt file is reported instead of opening as an empty store.
    /// </summary>
    /// <remarks>
    /// The discriminator is the JSONL envelope's top-level <c>type</c> property,
    /// which the legacy snapshot never carries. It is what lets a store written
    /// by <see cref="DurableStoreFormat.Auto"/> reopen in the format it was
    /// written in.
    /// </remarks>
    internal static DurableStoreFormat DetectFormat(string path)
    {
        if (!File.Exists(path))
        {
            return DurableStoreFormat.Jsonl;
        }

        string? firstContentLine = null;
        foreach (var line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                firstContentLine = line;
                break;
            }
        }

        // An empty file holds no legacy snapshot, so there is nothing to read and
        // the canonical format is the one to write.
        if (firstContentLine is null)
        {
            return DurableStoreFormat.Jsonl;
        }

        try
        {
            using var document = JsonDocument.Parse(firstContentLine);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String)
            {
                return DurableStoreFormat.Jsonl;
            }
        }
        catch (JsonException)
        {
            // Not a JSONL record envelope; the whole-file check below decides
            // whether it is a legacy snapshot or not a durable store at all.
        }

        // The legacy writer emits exactly one JSON object for the whole file, so a
        // file that does not parse that way is neither format.
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return DurableStoreFormat.LegacyJson;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The file '{path}' is not a durable store: it is neither JSONL records nor a legacy JSON snapshot ({exception.Message}).",
                exception);
        }

        throw new InvalidDataException(
            $"The file '{path}' is not a durable store: it is neither JSONL records nor a legacy JSON snapshot.");
    }

    /// <summary>
    /// Reads the legacy single-file snapshot at the path. A file that does not
    /// parse as a <see cref="StoreSnapshot"/> raises an
    /// <see cref="InvalidDataException"/> naming the path and the reason.
    /// </summary>
    internal static StoreSnapshot ReadLegacySnapshot(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException(
                $"The legacy durable store file '{path}' could not be read: {exception.Message}",
                exception);
        }

        StoreSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<StoreSnapshot>(text, DurableJson.Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The legacy durable store file '{path}' is not a valid JSON snapshot: {exception.Message}",
                exception);
        }

        return snapshot ?? throw new InvalidDataException(
            $"The legacy durable store file '{path}' is not a valid JSON snapshot: the file holds no JSON object.");
    }

    /// <summary>
    /// Resolves the per-user default store path: <c>LocalApplicationData</c> on
    /// Windows; <c>$XDG_STATE_HOME</c> when set and non-empty, otherwise
    /// <c>~/.local/state</c>, elsewhere. The store file lives in a <c>durable</c>
    /// subdirectory under that root. The directory itself is created on first
    /// write, not by this method.
    /// </summary>
    public static string DefaultStorePath(string fileName = "durable-store.jsonl")
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg
                ? xdg
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");

        return System.IO.Path.Combine(root, "durable", fileName);
    }

    private DurableStore(IDurableStoreProvider provider, string? path, DurableStoreFormat format)
    {
        _provider = provider;
        _path = path;
        _format = format;
    }

    /// <summary>The conversations known to this store.</summary>
    public IReadOnlyCollection<ConversationRecord> Conversations => _provider.Conversations;

    /// <summary>The tasks known to this store.</summary>
    public IReadOnlyCollection<DurableTaskRecord> Tasks => _provider.Tasks;

    /// <summary>The documents known to this store.</summary>
    public IReadOnlyCollection<DocumentState> Documents => _provider.Documents;

    /// <summary>Runs a transaction. Everything written inside is saved together, or discarded if it throws.</summary>
    public async Task<T> CommitAsync<T>(Func<Transaction, Task<T>> body)
    {
        await _gate.WaitAsync();
        try
        {
            var transaction = new Transaction(this);
            _provider.BeginTransaction();
            T result;
            try
            {
                result = await body(transaction);
            }
            catch
            {
                // The transaction failed: undo everything it wrote, so the store is
                // left exactly as it was before the transaction started.
                _provider.Rollback();
                throw;
            }

            _provider.Commit();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs a transaction that produces no value.</summary>
    public Task CommitAsync(Func<Transaction, Task> body) =>
        CommitAsync<object?>(async tx =>
        {
            await body(tx);
            return null;
        });

    /// <summary>Reads a document's state.</summary>
    public DocumentState? Document(string kind, string ownerId) => _provider.Document(kind, ownerId);

    /// <summary>Reads a task record.</summary>
    public DurableTaskRecord? Task(string id) => _provider.Task(id);

    /// <summary>Reads a conversation record.</summary>
    public ConversationRecord? Conversation(string id) => _provider.Conversation(id);

    /// <summary>Reads the entry ids of a conversation, oldest first.</summary>
    public IReadOnlyList<string> Entries(string conversationId) => _provider.Entries(conversationId);

    /// <summary>
    /// Advances a task's durable state, letting the callback write other records in
    /// the same transaction. Everything the callback writes is saved together with the
    /// checkpoint, or discarded if the callback throws.
    /// </summary>
    internal async Task<TaskCommit> CommitTaskAsync(string taskId, Func<Transaction, Task<TaskCommit>> commit)
    {
        await _gate.WaitAsync();
        try
        {
            var task = _provider.Task(taskId);
            if (task is null)
            {
                return TaskCommit.Failed("unknown task");
            }

            // A terminal task's outcome is final. A commit that arrives after the
            // task finished -- a durable timer waking up, say -- must not overwrite
            // it.
            if (task.Status == TaskStatus.Terminal)
            {
                return task.Outcome is { } existing
                    ? TaskCommit.Completed(existing)
                    : TaskCommit.Completed(DurableJson.ToElement(new TaskOutcome("completed")));
            }

            var transaction = new Transaction(this);
            _provider.BeginTransaction();
            TaskCommit next;
            try
            {
                next = await commit(transaction);
            }
            catch
            {
                // The phase failed: undo everything it wrote, including the task's
                // own state, so the checkpoint stays where it was and the task can
                // be resumed.
                _provider.Rollback();
                throw;
            }

            // The task's own state is part of the transaction: the checkpoint, the
            // status, and the outcome are written together with everything the
            // phase wrote, or none of it is.
            _provider.UpdateTask(task);
            task.Status = next.Status;
            if (next.Checkpoint is { } checkpoint)
            {
                task.Checkpoint = checkpoint;
            }

            if (next.WaitingOn.Count > 0)
            {
                task.WaitingOn.Clear();
                task.WaitingOn.AddRange(next.WaitingOn);
            }

            if (next.Outcome is { } outcome)
            {
                task.Outcome = DurableJson.ToElement(outcome);
            }

            _provider.UpdateTask(task);
            _provider.Commit();
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<TaskCommit> CommitTaskAsync(string taskId, Func<TaskCommit> commit)
    {
        await _gate.WaitAsync();
        try
        {
            var task = _provider.Task(taskId);
            if (task is null)
            {
                return TaskCommit.Failed("unknown task");
            }

            // A terminal task's outcome is final.
            if (task.Status == TaskStatus.Terminal)
            {
                return task.Outcome is { } existing
                    ? TaskCommit.Completed(existing)
                    : TaskCommit.Completed(DurableJson.ToElement(new TaskOutcome("completed")));
            }

            var next = commit();
            task.Status = next.Status;
            if (next.Checkpoint is { } checkpoint)
            {
                task.Checkpoint = checkpoint;
            }

            if (next.Outcome is { } outcome)
            {
                task.Outcome = DurableJson.ToElement(outcome);
            }

            _provider.UpdateTask(task);
            _provider.Commit();
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads or writes an idempotency memo.</summary>
    internal async Task<JsonElement?> MemoAsync(string taskId, string key, JsonElement? value)
    {
        await _gate.WaitAsync();
        try
        {
            var task = _provider.Task(taskId);
            if (task is null)
            {
                return null;
            }

            if (value is not null)
            {
                task.Memos[key] = value.Value.Clone();
                // A memo is written outside any transaction, so the provider is told
                // about the task's new state directly.
                _provider.UpdateTask(task);
                _provider.Commit();
                return value;
            }

            return task.Memos.TryGetValue(key, out var existing) ? existing : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sleeps until a deadline, saving the wake time in the checkpoint.</summary>
    internal async Task<TaskCommit> SleepUntilAsync(
        string taskId,
        DateTimeOffset deadline,
        Func<TaskCommit> nextCheckpoint,
        CancellationToken cancellationToken = default)
    {
        var delay = deadline - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await System.Threading.Tasks.Task.Delay(delay, cancellationToken);
        }

        return await CommitTaskAsync(taskId, nextCheckpoint);
    }

    /// <summary>Reads the outcomes of child tasks.</summary>
    internal async Task<IReadOnlyList<TaskOutcome>> OutcomesAsync(IEnumerable<string> taskIds)
    {
        // The gate may already be held by the transaction that is committing this
        // task's checkpoint, so read without taking it.
        return taskIds
            .Select(id => _provider.Task(id))
            .Where(task => task is not null)
            .Select(task => task!.Outcome is { } outcome
                ? outcome.Deserialize<TaskOutcome>(DurableJson.Options)!
                : new TaskOutcome("pending"))
            .ToList();
    }

    /// <summary>
    /// Schedules a task to run. The runtime owns the runner, so the store asks it to
    /// start the task rather than running it itself.
    /// </summary>
    /// <remarks>
    /// A store has at most one runner. The slot is claimed by the runtime that drives the
    /// store and released when that runtime is disposed, so two runtimes over one store
    /// cannot both schedule the same task: the second claim fails instead of silently
    /// replacing the first.
    /// </remarks>
    internal Action<string, CancellationToken>? TaskScheduler { get; private set; }

    private readonly object _schedulerGate = new();
    private object? _schedulerOwner;

    /// <summary>Claims the store's single task-runner slot for <paramref name="owner"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// Another runtime already drives this store. A store has one task runner, so a second
    /// runtime over the same store would compete with the first for the same tasks.
    /// </exception>
    internal void ClaimTaskScheduler(object owner, Action<string, CancellationToken> scheduler)
    {
        lock (_schedulerGate)
        {
            if (_schedulerOwner is not null && !ReferenceEquals(_schedulerOwner, owner))
            {
                throw new InvalidOperationException(
                    "This store is already driven by another durable runtime. A store has one task "
                    + "runner, so share the runtime instead of opening a second one over the same store.");
            }

            _schedulerOwner = owner;
            TaskScheduler = scheduler;
        }
    }

    /// <summary>Releases the task-runner slot, if <paramref name="owner"/> still holds it.</summary>
    internal void ReleaseTaskScheduler(object owner)
    {
        lock (_schedulerGate)
        {
            if (!ReferenceEquals(_schedulerOwner, owner))
            {
                return;
            }

            _schedulerOwner = null;
            TaskScheduler = null;
        }
    }

    internal void ScheduleTask(string taskId, CancellationToken cancellationToken) =>
        TaskScheduler?.Invoke(taskId, cancellationToken);

    /// <summary>
    /// The provider behind this store. The harness reaches for it to write the
    /// records a transaction owns -- a fork's inherited entries, a copied document.
    /// </summary>
    internal IDurableStoreProvider Provider => _provider;

    /// <summary>
    /// Re-reads the store file into memory, discarding the in-memory state. A store
    /// reads its file once, at open, and then serves from memory; call this to pick up
    /// writes another process made after this store was opened. The store must not be
    /// mid-transaction.
    /// </summary>
    /// <remarks>
    /// A persistent store holds the file's exclusive lock for its lifetime, so it is
    /// the only writer and its memory is already current: for such a store this is a
    /// no-op. The method exists for the ephemeral tier and for a future read-only
    /// tier, where a store can observe a file another process is writing.
    /// </remarks>
    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_provider.IsPersistent)
            {
                return;
            }

            _provider.Open(new DurableStoreOptions
            {
                Path = null,
                Format = DurableStoreFormat.InMemory,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases the store's hold on its file. A persistent store keeps an exclusive
    /// lock on the file for its lifetime, so dispose the store before another process
    /// opens the same path.
    /// </summary>
    public void Dispose()
    {
        _provider.Dispose();
        _gate.Dispose();
    }
}

/// <summary>A transaction: the only way to write durable state.</summary>
public sealed class Transaction
{
    private readonly IDurableStoreProvider _provider;

    internal Transaction(DurableStore store) => _provider = store.Provider;

    /// <summary>Creates a conversation and returns its record.</summary>
    public ConversationRecord CreateConversation(
        string? ownerTaskId = null,
        string? ownerConversationId = null,
        string? parentConversationId = null,
        string? forkEntryId = null)
    {
        var id = $"c{_provider.NextSequence()}";
        var record = new ConversationRecord(id, parentConversationId, forkEntryId)
        {
            OwnerTaskId = ownerTaskId,
            OwnerConversationId = ownerConversationId,
        };
        _provider.AddConversation(record);
        return record;
    }

    /// <summary>Appends an entry to a conversation and returns its id.</summary>
    public string AppendEntry(string conversationId, string kind, string? data = null)
    {
        var entryId = $"e{_provider.NextSequence()}";
        _provider.AddEntry(conversationId, entryId);
        return entryId;
    }

    /// <summary>Reads an editable copy of a document, creating it on first use.</summary>
    public DocumentDraft<T> Doc<T>(DocumentDefinition<T> definition, string ownerId)
    {
        var state = EnsureDocumentWithInitial(definition, ownerId);
        var current = state.Latest is { } latest
            ? latest.Deserialize<T>(DurableJson.Options)!
            : definition.Initial();
        return new DocumentDraft<T>(this, definition, ownerId, current);
    }

    /// <summary>
    /// Ensures the document exists, recording the definition's initial value the first time
    /// it is seen. A <see cref="ForkPolicy.Initial"/> fork inherits that value, so it has to
    /// be captured while the definition is in hand.
    /// </summary>
    private DocumentState EnsureDocumentWithInitial<T>(DocumentDefinition<T> definition, string ownerId) =>
        _provider.EnsureDocument(
            definition.Kind,
            ownerId,
            definition.Fork,
            DurableJson.ToElement(definition.Initial()));

    /// <summary>Creates a task and returns its record.</summary>
    public DurableTaskRecord CreateTask(
        DurableTaskDefinition definition,
        JsonElement input,
        string? ownerConversationId = null,
        string? ownerTaskId = null,
        bool background = false)
    {
        var record = new DurableTaskRecord($"t{_provider.NextSequence()}", definition.Name, input, definition.Initial())
        {
            OwnerConversationId = ownerConversationId,
            OwnerTaskId = ownerTaskId,
            Background = background,
        };
        _provider.AddTask(record);
        return record;
    }

    internal void CommitDocument<T>(DocumentDefinition<T> definition, string ownerId, T value, string entryId)
    {
        var state = EnsureDocumentWithInitial(definition, ownerId);
        _provider.RecordDocumentVersion(state, entryId, DurableJson.ToElement(value));
    }
}

/// <summary>An editable copy of a document. Assigning to it stages a write.</summary>
public sealed class DocumentDraft<T>
{
    private readonly Transaction _transaction;
    private readonly DocumentDefinition<T> _definition;
    private readonly string _ownerId;
    private T _value;

    internal DocumentDraft(Transaction transaction, DocumentDefinition<T> definition, string ownerId, T value)
    {
        _transaction = transaction;
        _definition = definition;
        _ownerId = ownerId;
        _value = value;
    }

    /// <summary>The current value.</summary>
    public T Value
    {
        get => _value;
        set => _value = value;
    }

    /// <summary>Stages the current value to be written when the transaction commits.</summary>
    public void Save(string entryId) => _transaction.CommitDocument(_definition, _ownerId, _value, entryId);
}

/// <summary>The persisted shape of the durable store.</summary>
public sealed class StoreSnapshot
{
    /// <summary>The monotonic write sequence.</summary>
    public long Sequence { get; set; }

    /// <summary>The conversations.</summary>
    public List<ConversationRecord> Conversations { get; set; } = [];

    /// <summary>The tasks.</summary>
    public List<DurableTaskRecord> Tasks { get; set; } = [];

    /// <summary>The documents.</summary>
    public List<DocumentSnapshot> Documents { get; set; } = [];

    /// <summary>The entry ids per conversation.</summary>
    public Dictionary<string, List<string>> Entries { get; set; } = [];

    /// <summary>The entry records, with their write sequence.</summary>
    public List<EntryRecord> EntryRecords { get; set; } = [];
}

/// <summary>The persisted shape of one document.</summary>
public sealed class DocumentSnapshot
{
    /// <summary>The document kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The owning conversation.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>The fork policy.</summary>
    public ForkPolicy Fork { get; set; }

    /// <summary>The document's initial value, inherited by a <see cref="ForkPolicy.Initial"/> fork.</summary>
    public JsonElement? Initial { get; set; }

    /// <summary>The committed versions.</summary>
    public List<DocumentVersionSnapshot> Versions { get; set; } = [];
}

/// <summary>The persisted shape of one document version.</summary>
public sealed class DocumentVersionSnapshot
{
    /// <summary>The entry that produced the version.</summary>
    public string EntryId { get; set; } = string.Empty;

    /// <summary>The write sequence.</summary>
    public long Sequence { get; set; }

    /// <summary>The value.</summary>
    public JsonElement Value { get; set; }
}

/// <summary>An extension: a named bundle of tools, prompt sections, and tasks.</summary>
public sealed class Extension
{
    /// <summary>Creates an extension.</summary>
    public Extension(
        string name,
        IReadOnlyList<AIFunction>? tools = null,
        IReadOnlyList<PromptSection>? sections = null,
        IReadOnlyList<DurableTaskDefinition>? tasks = null,
        IReadOnlyList<ToolWrapper>? wraps = null)
    {
        Name = name;
        Tools = tools ?? [];
        Sections = sections ?? [];
        Tasks = tasks ?? [];
        Wraps = wraps ?? [];
    }

    /// <summary>The extension name. Stored configuration references extensions by this name.</summary>
    public string Name { get; }

    /// <summary>The tools this extension contributes.</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>The prompt sections this extension contributes.</summary>
    public IReadOnlyList<PromptSection> Sections { get; }

    /// <summary>The durable tasks this extension contributes.</summary>
    public IReadOnlyList<DurableTaskDefinition> Tasks { get; }

    /// <summary>
    /// The tools this extension wraps. A wrapper never captures a base tool: it names
    /// the tool it decorates, so whichever implementation of that name wins is the one
    /// that gets wrapped.
    /// </summary>
    public IReadOnlyList<ToolWrapper> Wraps { get; }
}

/// <summary>A wrapper that decorates whichever tool with a given name wins.</summary>
public sealed class ToolWrapper
{
    /// <summary>Creates a wrapper.</summary>
    public ToolWrapper(string toolName, Func<AIFunction, AIFunction> wrap)
    {
        ToolName = toolName;
        Wrap = wrap;
    }

    /// <summary>The name of the tool this wrapper decorates.</summary>
    public string ToolName { get; }

    /// <summary>Builds the wrapper around the winning tool.</summary>
    public Func<AIFunction, AIFunction> Wrap { get; }
}

/// <summary>A named prompt section rendered into the system message.</summary>
public sealed class PromptSection
{
    /// <summary>Creates a prompt section.</summary>
    public PromptSection(string key, Func<SectionInput, Task<string?>> render, bool tag = true)
    {
        Key = key;
        Render = render;
        Tag = tag;
    }

    /// <summary>The section key.</summary>
    public string Key { get; }

    /// <summary>Renders the section's text, or <see langword="null"/> to omit it.</summary>
    public Func<SectionInput, Task<string?>> Render { get; }

    /// <summary>Whether the text is wrapped in a <c>&lt;key&gt;</c> tag.</summary>
    public bool Tag { get; }
}

/// <summary>The input available to a prompt section renderer.</summary>
public sealed class SectionInput
{
    /// <summary>Creates section input.</summary>
    public SectionInput(
        DurableConversation? conversation,
        IReadOnlyList<AIFunction> tools,
        IReadOnlyList<Extension> extensions,
        string? workingDirectory)
    {
        Conversation = conversation;
        Tools = tools;
        Extensions = extensions;
        WorkingDirectory = workingDirectory;
    }

    /// <summary>The conversation the prompt is being built for.</summary>
    public DurableConversation? Conversation { get; }

    /// <summary>The tools the agent will be offered.</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>The selected extensions.</summary>
    public IReadOnlyList<Extension> Extensions { get; }

    /// <summary>The working directory for this conversation.</summary>
    public string? WorkingDirectory { get; }
}

/// <summary>The registry of extensions. Installing the same name replaces the extension in place.</summary>
public sealed class ExtensionRegistry
{
    private readonly Dictionary<string, Extension> _extensions = [];

    /// <summary>The installed extensions, in installation order.</summary>
    public IReadOnlyList<Extension> Extensions => _extensions.Values.ToList();

    /// <summary>Installs an extension, replacing any extension with the same name.</summary>
    public void Install(Extension extension) => _extensions[extension.Name] = extension;

    /// <summary>Removes an extension by name.</summary>
    public void Uninstall(string name) => _extensions.Remove(name);

    /// <summary>Finds an extension by name.</summary>
    public Extension? Find(string name) => _extensions.GetValueOrDefault(name);

    /// <summary>Finds a task definition by name across all installed extensions.</summary>
    public DurableTaskDefinition? FindTask(string name) =>
        _extensions.Values.SelectMany(extension => extension.Tasks).FirstOrDefault(task => task.Name == name);
}

/// <summary>
/// A declared permission grant: a request kind, optionally narrowed to a scope.
/// </summary>
/// <remarks>
/// Grants are written as <c>kind</c> or <c>kind:scope</c> — for example <c>custom-tool:read_file</c>,
/// <c>read:/workspace</c>, <c>shell:git status</c>, or <c>url:https://example.com</c>. A grant with no
/// scope allows every request of that kind; <c>*</c> in either position is a wildcard.
/// <para>
/// A scope is a prefix, but only at a boundary: <c>read:/tmp/foo</c> grants <c>/tmp/foo/bar</c> and
/// not <c>/tmp/foobar</c>, and <c>url:https://example.com</c> does not grant
/// <c>https://example.com.evil.test</c>.
/// </para>
/// </remarks>
public sealed record PermissionGrant(string Kind, string? Scope = null)
{
    /// <summary>Parses the <c>kind</c> or <c>kind:scope</c> form.</summary>
    public static PermissionGrant Parse(string text)
    {
        var separator = text.IndexOf(':');
        return separator < 0
            ? new PermissionGrant(text.Trim())
            : new PermissionGrant(text[..separator].Trim(), text[(separator + 1)..].Trim());
    }

    /// <inheritdoc />
    public override string ToString() => Scope is null ? Kind : $"{Kind}:{Scope}";
}

/// <summary>
/// The fail-closed permission policy: a request is denied unless a declared grant covers it.
/// </summary>
/// <remarks>
/// The Copilot SDK's own default is <see cref="PermissionHandler.ApproveAll"/>, which approves every
/// tool call, shell command, file write, and URL fetch the model asks for. That is the wrong default
/// for a durable agent, whose whole point is that its behaviour is reproducible from stored state:
/// an approval that is not written down is not reproducible. So a durable conversation starts with
/// no grants at all and denies everything it was not explicitly given.
/// <para>
/// Denial uses <see cref="PermissionDecisionReject"/> with a feedback string. The SDK's
/// <c>PermissionDecisionDeniedByRules</c> and
/// <c>PermissionDecisionDeniedNoApprovalRuleAndCouldNotRequestFromUser</c> variants serialize to wire
/// values the Copilot CLI's permission host does not accept, so they fail the tool call with a
/// malformed-payload error instead of a clean denial.
/// </para>
/// </remarks>
public static class PermissionPolicy
{
    /// <summary>Describes a request as a grant kind and an optional scope.</summary>
    public static (string Kind, string? Scope) Describe(PermissionRequest request) => request switch
    {
        PermissionRequestCustomTool tool => ("custom-tool", tool.ToolName),
        PermissionRequestRead read => ("read", read.ResolvedPath ?? read.Path),
        PermissionRequestWrite write => ("write", write.ResolvedPath ?? write.FileName),
        PermissionRequestShell shell => ("shell", shell.FullCommandText),
        PermissionRequestUrl url => ("url", url.Url),
        PermissionRequestMcp mcp => ("mcp", $"{mcp.ServerName}/{mcp.ToolName}"),
        PermissionRequestHook hook => ("hook", hook.ToolName),
        PermissionRequestWorkflow workflow => ("workflow", workflow.Name),
        PermissionRequestExtensionManagement management => ("extension-management", management.ExtensionName),
        PermissionRequestExtensionPermissionAccess access => ("extension-permission-access", access.ExtensionName),
        PermissionRequestExtensionEnvAccess env => ("extension-env-access", env.ExtensionName),
        PermissionRequestMemory => ("memory", null),
        _ => (request.Kind, null),
    };

    /// <summary>Whether a declared grant set covers a request.</summary>
    public static bool IsGranted(IReadOnlyList<string>? grants, string kind, string? scope)
    {
        if (grants is null)
        {
            return false;
        }

        foreach (var text in grants)
        {
            var grant = PermissionGrant.Parse(text);
            if (grant.Kind != "*" && !string.Equals(grant.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (grant.Scope is null or "*" or "")
            {
                return true;
            }

            if (scope is not null && ScopeMatches(grant.Scope, scope))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the permission handler for a declared grant set.</summary>
    public static Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> Handler(
        IReadOnlyList<string>? grants) =>
        (request, _) =>
        {
            var (kind, scope) = Describe(request);
            if (IsGranted(grants, kind, scope))
            {
                return Task.FromResult<PermissionDecision>(new PermissionDecisionApproveOnce());
            }

            var target = scope is null ? kind : $"{kind} ({scope})";
            return Task.FromResult<PermissionDecision>(new PermissionDecisionReject
            {
                Feedback =
                    $"denied by durable policy: no grant for {target}. " +
                    $"Add \"{kind}\" or \"{kind}:{scope ?? "*"}\" to the conversation's Permissions to allow it.",
            });
        };

    private static bool ScopeMatches(string grant, string scope)
    {
        if (string.Equals(grant, scope, StringComparison.Ordinal))
        {
            return true;
        }

        if (!scope.StartsWith(grant, StringComparison.Ordinal))
        {
            return false;
        }

        // A prefix only counts at a boundary, so "/tmp/foo" does not grant "/tmp/foobar".
        return scope[grant.Length] is '/' or '\\' or ' ' or ':' or ';' or ',';
    }
}

/// <summary>
/// The durable sandbox policy for a conversation.
/// </summary>
/// <remarks>
/// This is the durable layer's own shape, deliberately not the SDK's experimental
/// <c>GitHub.Copilot.Rpc.SandboxConfig</c> wire type. The wire type is what the runtime
/// accepts today; the stored type is what a conversation keeps meaning. A field the SDK
/// renames or removes cannot change what a stored conversation means, and a mapping that
/// drops a field is a visible bug in this file rather than a silent change in the store.
/// <para>
/// <c>null</c> on <see cref="AgentChoices.Sandbox"/> means "no sandbox": the session is
/// created with no sandbox configuration at all, which is what every example before this
/// one did.
/// </para>
/// <para>
/// Paths in <see cref="Filesystem"/> may contain the <see cref="WorkspaceToken"/> token
/// (<c>{workspace}</c>). The token is expanded when the session is built, against the
/// conversation's working directory, so a fork keeps the policy's shape but not the
/// parent's absolute grants.
/// </para>
/// </remarks>
public sealed class DurableSandboxPolicy : IEquatable<DurableSandboxPolicy>
{
    /// <summary>The token that stands for the conversation's working directory.</summary>
    public const string WorkspaceToken = "{workspace}";

    /// <summary>Whether the sandbox is enabled for the session.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether the working directory is added to the readwrite paths automatically.</summary>
    public bool? AddCurrentWorkingDirectory { get; set; }

    /// <summary>
    /// Whether the agent may ask for an individual command to run outside the sandbox.
    /// Fail-closed: absent or <c>false</c> means no bypass.
    /// </summary>
    public bool? AllowBypass { get; set; }

    /// <summary>
    /// Whether the agent may read the dev tool directories. Fail-closed: absent or
    /// <c>false</c> means no dev tool access.
    /// </summary>
    public bool? AllowDevToolAccess { get; set; }

    /// <summary>Whether MCP servers are sandboxed. Only an explicit <c>false</c> opts out.</summary>
    public bool? SandboxMcpServers { get; set; }

    /// <summary>Whether LSP servers are sandboxed. Only an explicit <c>false</c> opts out.</summary>
    public bool? SandboxLspServers { get; set; }

    /// <summary>
    /// Whether the working directory is trusted for the session. <c>null</c> leaves the
    /// existing folder trust unchanged.
    /// </summary>
    public bool? TrustWorkingDirectory { get; set; }

    /// <summary>GitHub and git credential forwarding.</summary>
    public DurableSandboxAuth? Auth { get; set; }

    /// <summary>Credentials injected into the sandboxed process.</summary>
    public DurableSandboxCredentials? Credentials { get; set; }

    /// <summary>The filesystem policy.</summary>
    public DurableSandboxFilesystem? Filesystem { get; set; }

    /// <summary>The network policy.</summary>
    public DurableSandboxNetwork? Network { get; set; }

    /// <summary>Whether the keychain is reachable. Seatbelt only.</summary>
    public bool? KeychainAccess { get; set; }

    /// <summary>
    /// Expands the <see cref="WorkspaceToken"/> token in a policy path against a working
    /// directory. A path without the token is returned unchanged, so an absolute grant
    /// stays absolute.
    /// </summary>
    public static string ExpandPath(string path, string? workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.Contains(WorkspaceToken, StringComparison.Ordinal))
        {
            return path;
        }

        return path.Replace(
            WorkspaceToken,
            workingDirectory ?? Environment.CurrentDirectory,
            StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(DurableSandboxPolicy? other) =>
        other is not null
        && Enabled == other.Enabled
        && AddCurrentWorkingDirectory == other.AddCurrentWorkingDirectory
        && AllowBypass == other.AllowBypass
        && AllowDevToolAccess == other.AllowDevToolAccess
        && SandboxMcpServers == other.SandboxMcpServers
        && SandboxLspServers == other.SandboxLspServers
        && TrustWorkingDirectory == other.TrustWorkingDirectory
        && KeychainAccess == other.KeychainAccess
        && Equals(Auth, other.Auth)
        && Equals(Credentials, other.Credentials)
        && Equals(Filesystem, other.Filesystem)
        && Equals(Network, other.Network);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DurableSandboxPolicy);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Enabled,
        AddCurrentWorkingDirectory,
        AllowBypass,
        AllowDevToolAccess,
        SandboxMcpServers,
        SandboxLspServers,
        TrustWorkingDirectory,
        KeychainAccess);
}

/// <summary>GitHub and git credential forwarding for a sandboxed session.</summary>
public sealed class DurableSandboxAuth : IEquatable<DurableSandboxAuth>
{
    /// <summary>Whether the GitHub CLI's credentials are forwarded.</summary>
    public bool? Gh { get; set; }

    /// <summary>Whether git credentials are forwarded.</summary>
    public bool? Git { get; set; }

    /// <inheritdoc />
    public bool Equals(DurableSandboxAuth? other) => other is not null && Gh == other.Gh && Git == other.Git;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DurableSandboxAuth);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Gh, Git);
}

/// <summary>Credentials injected into a sandboxed session.</summary>
public sealed class DurableSandboxCredentials : IEquatable<DurableSandboxCredentials>
{
    /// <summary>
    /// The environment variables to inject, and for each one the hosts it is injected into.
    /// A value is a list of HTTPS hostnames or <c>*.example.com</c> patterns; a bare
    /// <c>*</c> is not accepted, and an empty list is not a valid injection.
    /// </summary>
    public Dictionary<string, List<string>>? EnvVars { get; set; }

    /// <inheritdoc />
    public bool Equals(DurableSandboxCredentials? other) => other is not null && EnvVarsEqual(EnvVars, other.EnvVars);

    private static bool EnvVarsEqual(Dictionary<string, List<string>>? left, Dictionary<string, List<string>>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var hosts) || !(pair.Value ?? []).SequenceEqual(hosts ?? []))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DurableSandboxCredentials);

    /// <inheritdoc />
    public override int GetHashCode() => EnvVars?.Count ?? 0;
}

/// <summary>The filesystem policy of a sandbox.</summary>
public sealed class DurableSandboxFilesystem : IEquatable<DurableSandboxFilesystem>
{
    /// <summary>Whether the policy is cleared when the session exits.</summary>
    public bool? ClearPolicyOnExit { get; set; }

    /// <summary>Paths the sandboxed process may not read at all.</summary>
    public List<string>? DeniedPaths { get; set; }

    /// <summary>Paths the sandboxed process may read but not write.</summary>
    public List<string>? ReadonlyPaths { get; set; }

    /// <summary>Paths the sandboxed process may read and write.</summary>
    public List<string>? ReadwritePaths { get; set; }

    /// <inheritdoc />
    public bool Equals(DurableSandboxFilesystem? other) =>
        other is not null
        && ClearPolicyOnExit == other.ClearPolicyOnExit
        && (DeniedPaths ?? []).SequenceEqual(other.DeniedPaths ?? [])
        && (ReadonlyPaths ?? []).SequenceEqual(other.ReadonlyPaths ?? [])
        && (ReadwritePaths ?? []).SequenceEqual(other.ReadwritePaths ?? []);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DurableSandboxFilesystem);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        ClearPolicyOnExit,
        DeniedPaths?.Count,
        ReadonlyPaths?.Count,
        ReadwritePaths?.Count);
}

/// <summary>The network policy of a sandbox.</summary>
public sealed class DurableSandboxNetwork : IEquatable<DurableSandboxNetwork>
{
    /// <summary>Hosts the sandboxed process may reach.</summary>
    public List<string>? AllowedHosts { get; set; }

    /// <summary>Hosts the sandboxed process may not reach.</summary>
    public List<string>? BlockedHosts { get; set; }

    /// <summary>Whether outbound network access is allowed. Fail-closed.</summary>
    public bool? AllowOutbound { get; set; }

    /// <summary>Whether the local network is reachable. Fail-closed.</summary>
    public bool? AllowLocalNetwork { get; set; }

    /// <summary>The proxy the sandboxed process is routed through.</summary>
    public DurableSandboxProxy? Proxy { get; set; }

    /// <inheritdoc />
    public bool Equals(DurableSandboxNetwork? other) =>
        other is not null
        && AllowOutbound == other.AllowOutbound
        && AllowLocalNetwork == other.AllowLocalNetwork
        && (AllowedHosts ?? []).SequenceEqual(other.AllowedHosts ?? [])
        && (BlockedHosts ?? []).SequenceEqual(other.BlockedHosts ?? [])
        && Equals(Proxy, other.Proxy);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DurableSandboxNetwork);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        AllowOutbound,
        AllowLocalNetwork,
        AllowedHosts?.Count,
        BlockedHosts?.Count);
}

/// <summary>The proxy a sandboxed network is routed through.</summary>
public sealed class DurableSandboxProxy : IEquatable<DurableSandboxProxy>
{
    /// <summary>The proxy URL.</summary>
    public string? Url { get; set; }

    /// <summary>The proxy username.</summary>
    public string? Username { get; set; }

    /// <summary>The proxy password.</summary>
    public string? Password { get; set; }

    /// <inheritdoc />
    public bool Equals(DurableSandboxProxy? other) =>
        other is not null && Url == other.Url && Username == other.Username && Password == other.Password;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DurableSandboxProxy);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Url, Username, Password);
}

/// <summary>The per-conversation agent choices. Stored by name, so they outlive the code.</summary>
public sealed class AgentChoices
{
    /// <summary>The selected extension names, in order.</summary>
    public List<string>? Extensions { get; set; }

    /// <summary>The selected tool names, in order.</summary>
    public List<string>? Tools { get; set; }

    /// <summary>The model identifier.</summary>
    public string? Model { get; set; }

    /// <summary>The reasoning effort level.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>The working directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Extra instructions appended to the system prompt.</summary>
    public string? Instructions { get; set; }

    /// <summary>Whether the session streams assistant message deltas.</summary>
    public bool? Streaming { get; set; }

    /// <summary>
    /// Native session compaction settings. The Copilot SDK compacts the model context on its
    /// own when it grows past the configured thresholds, so a long conversation keeps a small
    /// context without the app summarizing anything itself.
    /// </summary>
    public InfiniteSessionConfig? InfiniteSessions { get; set; }

    /// <summary>
    /// The permission grants this conversation is allowed to use, as <c>kind</c> or
    /// <c>kind:scope</c> strings. Empty or absent means the conversation may use no gated
    /// capability at all: the policy is fail-closed.
    /// </summary>
    /// <remarks>
    /// Grants are persisted with the choices, so a conversation that was allowed to run a tool
    /// before a restart is still allowed to run it after, and one that was denied stays denied.
    /// A fork copies the parent's choices, so it inherits the parent's grants as a snapshot and
    /// can never silently widen them.
    /// <para>
    /// Grants take effect when a session is created. The SDK has no per-session permission API,
    /// so <see cref="DurableConversation.ConfigureAsync"/> cannot change them on a live session;
    /// the new grants apply to the next conversation, restore, or fork.
    /// </para>
    /// </remarks>
    public List<string>? Permissions { get; set; }

    /// <summary>
    /// The sandbox policy for this conversation, or <c>null</c> for no sandbox.
    /// </summary>
    /// <remarks>
    /// The policy is persisted with the choices, so a conversation that ran sandboxed before a
    /// restart still runs sandboxed after, and a fork inherits the policy as a snapshot. Paths
    /// may use the <see cref="DurableSandboxPolicy.WorkspaceToken"/> token, which is expanded
    /// against the conversation's working directory when the session is built — so a fork
    /// inherits the policy's shape but not the parent's absolute grants.
    /// <para>
    /// <c>null</c> is the default and means the session is created with no sandbox
    /// configuration at all, which is what every example before this one did.
    /// </para>
    /// </remarks>
    public DurableSandboxPolicy? Sandbox { get; set; }

    /// <summary>
    /// Session hooks. Hooks are code-level configuration, so they are not persisted with the
    /// document; they are supplied when the conversation is created.
    /// </summary>
    [JsonIgnore]
    public SessionHooks? Hooks { get; set; }
}

/// <summary>The resolved agent: stored choices bound to live registry code.</summary>
public sealed record ResolvedAgent(
    string? Model,
    string? ReasoningEffort,
    string? WorkingDirectory,
    string? Instructions,
    bool? Streaming,
    SessionHooks? Hooks,
    InfiniteSessionConfig? InfiniteSessions,
    IReadOnlyList<Extension> Extensions,
    IReadOnlyList<AIFunction> Tools,
    IReadOnlyList<string>? Permissions,
    DurableSandboxPolicy? Sandbox);

/// <summary>
/// The model a runtime reported for a session or for one turn of it.
/// </summary>
/// <remarks>
/// This is the model the runtime <i>reported</i>, not the model the host asked for. A runtime
/// that substitutes a model — because the requested one is unavailable, because the request
/// was routed, or because a recorded run was produced against another model — reports the
/// substitute here, so the substitution is visible rather than hidden behind the request.
/// <para>
/// When the runtime reports no model at all, <see cref="Model"/> is <c>null</c> and
/// <see cref="HasModel"/> is <c>false</c>. There is no fallback to
/// <see cref="DurableHarness.DefaultModel"/>: "the runtime said nothing" and "the runtime said
/// <c>gpt-6-luna</c>" are different facts, and silently turning the first into the second is
/// exactly what this type exists to prevent.
/// </para>
/// </remarks>
public sealed class DurableModelReport
{
    /// <summary>The report for a runtime that has not reported a model.</summary>
    public static DurableModelReport NotReported { get; } = new(null, null);

    private DurableModelReport(string? model, string? source)
    {
        Model = model;
        Source = source;
    }

    /// <summary>The model identifier the runtime reported, or <c>null</c> when it reported none.</summary>
    public string? Model { get; }

    /// <summary>
    /// Where the report came from: <c>session</c>, <c>turn</c>, or <c>usage</c>. It names the
    /// event that carried the value, so a diagnosis can tell a session-level report from a
    /// turn-level one. <c>null</c> when nothing was reported.
    /// </summary>
    public string? Source { get; }

    /// <summary>Whether the runtime reported a model.</summary>
    public bool HasModel => !string.IsNullOrEmpty(Model);

    /// <summary>
    /// The reported model, or an error when the runtime reported none. Use this where a model
    /// is required and its absence should stop the run rather than pass silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The runtime reported no model.</exception>
    public string RequiredModel => HasModel
        ? Model!
        : throw new InvalidOperationException(
            "The runtime reported no model. Check the runtime's transcript: a deterministic " +
            "runtime only reports a model when its recording carries one.");

    /// <summary>Creates a report from a runtime event's model value, or the not-reported one.</summary>
    internal static DurableModelReport From(string? model, string source) =>
        string.IsNullOrWhiteSpace(model) ? NotReported : new DurableModelReport(model, source);

    /// <inheritdoc />
    public override string ToString() => HasModel ? Model! : "(no model reported)";
}

/// <summary>
/// Accumulates the model reports a single conversation observes: the session-level report and
/// one report per assistant turn.
/// </summary>
/// <remarks>
/// The tracker is deliberately separate from <see cref="DurableConversation"/> so the
/// sequencing rules can be exercised directly: a session can report a model when it starts or
/// when it changes model, an assistant turn can report one through <c>assistant.turn_start</c>
/// and again through <c>assistant.usage</c>, and a turn whose runtime names no model must stay
/// visible as a gap rather than borrowing a neighbour's model.
/// </remarks>
internal sealed class DurableModelTracker
{
    private readonly List<DurableModelReport> _turns = [];
    private DurableModelReport _effective = DurableModelReport.NotReported;
    private bool _passOpen;

    /// <summary>The most recent model the runtime reported, or not-reported.</summary>
    internal DurableModelReport Effective
    {
        get
        {
            lock (_turns)
            {
                return _effective;
            }
        }
    }

    /// <summary>The model reported for each turn, oldest first.</summary>
    internal IReadOnlyList<DurableModelReport> Turns
    {
        get
        {
            lock (_turns)
            {
                return _turns.ToList();
            }
        }
    }

    /// <summary>Records a session-level report from <c>session.start</c> or a model change.</summary>
    /// <remarks>
    /// A session-level report is not a turn: it describes the session, so it updates the
    /// effective value but leaves the per-turn list alone.
    /// </remarks>
    internal DurableModelReport RecordSession(string? model, string source)
    {
        var report = DurableModelReport.From(model, source);
        lock (_turns)
        {
            if (report.HasModel)
            {
                _effective = report;
            }
        }

        return report;
    }

    /// <summary>Records a turn-level report from <c>assistant.turn_start</c> or usage.</summary>
    /// <remarks>
    /// A turn is one assistant pass, and a single pass can report its model twice — once at
    /// <c>assistant.turn_start</c>, once with its usage — so a second report for the pass that
    /// is still open refines the entry instead of adding another. A pass whose runtime reports
    /// no model is still recorded, as <see cref="DurableModelReport.NotReported"/>, so the list
    /// stays aligned with the turn sequence and silence never borrows a neighbour's model.
    /// </remarks>
    internal DurableModelReport RecordTurn(string? model, string source)
    {
        var report = DurableModelReport.From(model, source);
        lock (_turns)
        {
            if (_passOpen)
            {
                // Refine the open pass. A pass that has already named a model keeps it: the
                // later report belongs to the same pass, so swapping in a not-reported value
                // would erase a fact the runtime did state.
                if (report.HasModel)
                {
                    _turns[^1] = report;
                }
            }
            else
            {
                _turns.Add(report);
                _passOpen = true;
            }

            if (report.HasModel)
            {
                _effective = report;
            }
        }

        return report;
    }

    /// <summary>
    /// Closes the pass in flight. A pass that reported nothing stays recorded as not-reported.
    /// </summary>
    internal void CloseTurn()
    {
        lock (_turns)
        {
            _passOpen = false;
        }
    }

    /// <summary>
    /// Routes a session event's model report into this tracker.
    /// </summary>
    /// <remarks>
    /// The runtime reports the model on the events it already emits: <c>session.start</c> and
    /// <c>session.model_change</c> describe the session, <c>assistant.turn_start</c> and
    /// <c>assistant.usage</c> describe a turn. Nothing here reads the requested model, so a
    /// runtime that substitutes one is visible.
    /// </remarks>
    /// <returns>The report the event produced, or <c>null</c> when the event carries no model.</returns>
    internal DurableModelReport? Record(SessionEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        return evt switch
        {
            SessionStartEvent start => RecordSession(start.Data.SelectedModel, "session"),
            SessionModelChangeEvent change => RecordSession(change.Data.NewModel, "session"),
            AssistantTurnStartEvent turnStart => RecordTurn(turnStart.Data.Model, "turn"),
            AssistantUsageEvent usage => RecordTurn(usage.Data.Model, "usage"),
            _ => null,
        };
    }
}

/// <summary>A conversation handle: a Copilot session bound to durable app state.</summary>
public sealed class DurableConversation
{
    private readonly DurableRuntime _runtime;
    private readonly List<string> _answers = [];
    private readonly List<SandboxDecisionEvent> _sandboxDecisions = [];
    private readonly List<(Func<SandboxDecisionEvent, bool> Match, TaskCompletionSource<SandboxDecisionEvent> Completion)> _sandboxWaiters = [];
    private readonly DurableModelTracker _modelTracker = new();
    private readonly object _checkpointGate = new();
    private readonly ConcurrentDictionary<string, (string? Name, JsonElement? Arguments)> _toolCalls = [];

    private Task _checkpointChain = Task.CompletedTask;
    private Exception? _checkpointFailure;

    internal DurableConversation(DurableRuntime runtime, CopilotSession session, ConversationRecord record)
    {
        _runtime = runtime;
        Session = session;
        Record = record;
    }

    /// <summary>The underlying Copilot session.</summary>
    public CopilotSession Session { get; private set; }

    /// <summary>
    /// Attaches the Copilot session. The handle exists before the session does, so
    /// prompt sections can read the conversation's durable documents while the
    /// session configuration is being built.
    /// </summary>
    internal void Attach(CopilotSession session)
    {
        Session = session;
        // A permanent subscription records every answer the session produces, so a
        // review loop can look at the passes it has already made.
        _subscription = session.On<SessionEvent>(Observe);
    }

    /// <summary>
    /// Records what one session event says about this conversation. This is the body of the
    /// permanent subscription <see cref="Attach"/> installs, kept separate so the observation
    /// rules can be exercised without a live session.
    /// </summary>
    internal void Observe(SessionEvent evt)
    {
        if (evt is AssistantMessageEvent message)
        {
            if (!string.IsNullOrEmpty(message.Data.Content))
            {
                RecordAnswer(message.Data.Content);
            }

            // The answer ends the pass being recorded, so the next model report starts a
            // new turn instead of refining this one.
            CloseTurn();
        }

        // Sandbox decisions ride the same subscription: the runtime reports what the
        // sandbox resolved, whether it engaged, and what it denied as ordinary session
        // events, so a durable conversation can record them without a second stream.
        if (evt is SandboxDecisionEvent decision)
        {
            RecordSandboxDecision(decision);
        }

        // The model the runtime actually used rides the same subscription too. It is
        // recorded from the runtime's own reports, never from the requested model, so a
        // substitution is visible.
        if (_modelTracker.Record(evt) is { } report)
        {
            _runtime.PublishModel(report);
        }
    }

    private IDisposable? _subscription;

    /// <summary>
    /// Removes the permanent subscription installed by <see cref="Attach"/>. Called when the
    /// runtime is disposed, so a session event that arrives during disposal cannot write into a
    /// store that is going away. Idempotent.
    /// </summary>
    internal void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    /// <summary>
    /// The sandbox decisions this conversation has observed, oldest first.
    /// </summary>
    /// <remarks>
    /// The list is fed by the permanent subscription installed in <see cref="Attach"/>, so it
    /// holds every decision the runtime reported for this session — the resolved policy, the
    /// spawn result, the enforcement state, and each access denial. It is in-memory: a decision
    /// is an observation about a live session, not durable state, and a restored conversation
    /// starts with an empty list.
    /// </remarks>
    public IReadOnlyList<SandboxDecisionEvent> SandboxDecisions
    {
        get
        {
            lock (_sandboxDecisions)
            {
                return _sandboxDecisions.ToList();
            }
        }
    }

    /// <summary>
    /// Waits for a sandbox decision that matches a predicate.
    /// </summary>
    /// <param name="match">The predicate a decision must satisfy.</param>
    /// <param name="timeout">How long to wait. Defaults to 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The first matching decision, including one already observed.</returns>
    /// <exception cref="TimeoutException">
    /// No decision matched within the timeout. The message names the decisions that were
    /// observed, so a caller can tell "the sandbox never reported" from "it reported something
    /// else".
    /// </exception>
    public async Task<SandboxDecisionEvent> WaitForSandboxDecisionAsync(
        Func<SandboxDecisionEvent, bool> match,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);

        TaskCompletionSource<SandboxDecisionEvent> completion;
        lock (_sandboxDecisions)
        {
            // A decision that already arrived satisfies the wait immediately: the caller may
            // be looking at a turn that has already finished.
            foreach (var observed in _sandboxDecisions)
            {
                if (match(observed))
                {
                    return observed;
                }
            }

            completion = new TaskCompletionSource<SandboxDecisionEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _sandboxWaiters.Add((match, completion));
        }

        try
        {
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (TimeoutException)
        {
            lock (_sandboxDecisions)
            {
                _sandboxWaiters.RemoveAll(waiter => ReferenceEquals(waiter.Completion, completion));
            }

            var observed = SandboxDecisions;
            var summary = observed.Count == 0
                ? "no sandbox decisions were reported at all"
                : $"observed: {string.Join(", ", observed.Select(DescribeSandboxDecision))}";
            throw new TimeoutException(
                $"No sandbox decision matched within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds:0.###}s ({summary}). " +
                "A sandbox that is not engaged reports no decisions; check the backend for this platform.");
        }
    }

    private static string DescribeSandboxDecision(SandboxDecisionEvent decision) =>
        decision.Data is null ? "(no data)" : $"{decision.Data.Kind ?? "?"}";

    /// <summary>
    /// The model the runtime reported for this conversation: the session-level report when the
    /// session started or changed model, otherwise the latest report from a turn or a usage
    /// record.
    /// </summary>
    /// <remarks>
    /// The value is the runtime's, not the host's request, so a runtime that substitutes a
    /// model is visible here. A conversation whose runtime has not reported a model returns
    /// <see cref="DurableModelReport.NotReported"/> — <see cref="DurableModelReport.HasModel"/>
    /// is <c>false</c> and the value is <c>null</c>; it is never silently replaced with
    /// <see cref="DurableHarness.DefaultModel"/>.
    /// </remarks>
    public DurableModelReport EffectiveModel => _modelTracker.Effective;

    /// <summary>
    /// The model the runtime reported for each completed turn, oldest first.
    /// </summary>
    /// <remarks>
    /// A turn is one assistant pass: a turn that calls tools produces several, and each
    /// records the model the runtime named for it. A pass whose runtime named no model is
    /// recorded as <see cref="DurableModelReport.NotReported"/> rather than being dropped, so
    /// the list stays aligned with the turn sequence.
    /// <para>
    /// The list is in-memory: a model report is an observation about a live session, so a
    /// restored conversation starts with an empty list.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DurableModelReport> TurnModels => _modelTracker.Turns;

    /// <summary>
    /// Closes the turn in flight. Called when an assistant message arrives: the next model
    /// report belongs to the next pass, and a pass that reported nothing stays recorded as
    /// not-reported instead of borrowing the previous turn's model.
    /// </summary>
    private void CloseTurn() => _modelTracker.CloseTurn();

    private void RecordSandboxDecision(SandboxDecisionEvent decision)
    {
        List<(Func<SandboxDecisionEvent, bool> Match, TaskCompletionSource<SandboxDecisionEvent> Completion)>? matched = null;
        lock (_sandboxDecisions)
        {
            _sandboxDecisions.Add(decision);
            for (var i = _sandboxWaiters.Count - 1; i >= 0; i--)
            {
                var waiter = _sandboxWaiters[i];
                bool isMatch;
                try
                {
                    isMatch = waiter.Match(decision);
                }
                catch (Exception)
                {
                    isMatch = false;
                }

                if (!isMatch)
                {
                    continue;
                }

                (matched ??= []).Add(waiter);
                _sandboxWaiters.RemoveAt(i);
            }
        }

        foreach (var waiter in matched ?? [])
        {
            waiter.Completion.TrySetResult(decision);
        }
    }

    /// <summary>The durable conversation record.</summary>
    public ConversationRecord Record { get; }

    /// <summary>
    /// The assistant's answers in this conversation, oldest first. A turn that calls
    /// tools produces one answer per pass, so a review loop shows every pass.
    /// </summary>
    public IReadOnlyList<string> Answers => _answers;

    /// <summary>Records an assistant answer.</summary>
    internal void RecordAnswer(string answer)
    {
        lock (_answers)
        {
            _answers.Add(answer);
        }
    }

    /// <summary>The conversation identifier.</summary>
    public string Id => Record.Id;

    /// <summary>The Copilot session identifier.</summary>
    public string SessionId => Session.SessionId;

    /// <summary>
    /// Whether the conversation is busy: it has a run in flight, from an input until
    /// its final answer.
    /// </summary>
    public bool Busy => _busy;

    private bool _busy;

    /// <summary>Tracks whether a run is in flight, so a caller can tell busy from idle.</summary>
    internal void SetBusy(bool busy) => _busy = busy;

    /// <summary>Reads the latest value of a document.</summary>
    public T? Snapshot<T>(DocumentDefinition<T> definition) =>
        _runtime.Store.Document(definition.Kind, Id)?.Latest is { } latest
            ? latest.Deserialize<T>(DurableJson.Options)
            : default;

    /// <summary>Reads the value a document had in the same transaction as the given entry.</summary>
    public T? SnapshotAsOf<T>(DocumentDefinition<T> definition, string entryId) =>
        _runtime.Store.Document(definition.Kind, Id)?.AsOf(entryId) is { } value
            ? value.Deserialize<T>(DurableJson.Options)
            : default;

    /// <summary>Runs a transaction against the durable store.</summary>
    public Task<T> CommitAsync<T>(Func<Transaction, Task<T>> body) => _runtime.Store.CommitAsync(body);

    /// <summary>Runs a transaction against the durable store.</summary>
    public Task CommitAsync(Func<Transaction, Task> body) => _runtime.Store.CommitAsync(body);

    /// <summary>Appends an entry to this conversation's transcript.</summary>
    public Task<string> AppendEntryAsync(string kind, string? data = null) =>
        CommitAsync(async tx =>
        {
            var entryId = tx.AppendEntry(Id, kind, data);
            await Task.CompletedTask;
            return entryId;
        });

    /// <summary>The binding recorded for this conversation, or null before one is written.</summary>
    public SessionBinding? Binding() => Snapshot(_runtime.SessionBindingDocument);

    /// <summary>The most recent input admission, or null before one is written.</summary>
    public InputAdmission? Admission() => Snapshot(_runtime.AdmissionDocument);

    /// <summary>The most recent checkpoint, or null before one is written.</summary>
    public ConversationCheckpoint? Checkpoint() => Snapshot(_runtime.CheckpointDocument);

    /// <summary>
    /// Writes the binding that records which native session serves this conversation.
    /// </summary>
    /// <remarks>
    /// The binding is written in one transaction with the entry that records it, so the
    /// conversation's transcript and the session it names are never observed apart. Writing the
    /// same binding twice is a no-op, so a re-attach does not grow the log.
    /// </remarks>
    internal async Task BindAsync(string? sessionId, string? model, CancellationToken cancellationToken = default)
    {
        await CommitAsync(async tx =>
        {
            var current = tx.Doc(_runtime.SessionBindingDocument, Id);
            if (current.Value?.SessionId == sessionId)
            {
                await Task.CompletedTask;
                return;
            }

            current.Value = new SessionBinding
            {
                SessionId = sessionId,
                Model = model,
                BoundAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            current.Save(tx.AppendEntry(Id, "session-binding", sessionId));
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// Commits an admission for a prompt before the prompt is sent.
    /// </summary>
    /// <remarks>
    /// The commit happens before the send is issued, so a failure here surfaces to the caller
    /// and nothing is sent: the durable record and the send are ordered, not concurrent. The
    /// returned identifier settles the admission once the outcome is known.
    /// </remarks>
    internal async Task<string> AdmitAsync(string prompt)
    {
        var admissionId = $"a{Guid.NewGuid():N}";
        await CommitAsync(async tx =>
        {
            var draft = tx.Doc(_runtime.AdmissionDocument, Id);
            draft.Value = new InputAdmission
            {
                AdmissionId = admissionId,
                Prompt = prompt,
                State = "admitted",
                AdmittedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            draft.Save(tx.AppendEntry(Id, "admission", admissionId));
            await Task.CompletedTask;
        });

        return admissionId;
    }

    /// <summary>Settles an admission to its terminal state.</summary>
    internal Task SettleAsync(string admissionId, string state) =>
        CommitAsync(async tx =>
        {
            var draft = tx.Doc(_runtime.AdmissionDocument, Id);
            var current = draft.Value;
            if (current is null || current.AdmissionId != admissionId)
            {
                await Task.CompletedTask;
                return;
            }

            draft.Value = new InputAdmission
            {
                AdmissionId = current.AdmissionId,
                Prompt = current.Prompt,
                State = state,
                AdmittedAt = current.AdmittedAt,
                SettledAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            draft.Save(tx.AppendEntry(Id, "admission-settled", state));
            await Task.CompletedTask;
        });

    /// <summary>
    /// Queues a checkpoint write on this conversation's serial chain and returns the write.
    /// </summary>
    /// <remarks>
    /// Checkpoint writes are serialized per conversation, so two events that arrive together
    /// cannot interleave their read-then-write and lose one another's version. The chain is a
    /// composition of tasks rather than a fire-and-forget continuation: the caller holds the
    /// returned task, so a failed write is observed by whoever awaits the barrier instead of
    /// being swallowed.
    /// </remarks>
    private Task QueueCheckpointAsync(Func<Task> write)
    {
        lock (_checkpointGate)
        {
            var next = _checkpointChain.ContinueWith(
                async previous =>
                {
                    try
                    {
                        await previous;
                    }
                    catch (Exception error)
                    {
                        // A failed earlier write must not stop later checkpoints from being
                        // attempted; the failure is kept for the barrier to report.
                        _checkpointFailure ??= error;
                    }

                    await write();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();

            _checkpointChain = next.ContinueWith(
                previous =>
                {
                    try
                    {
                        previous.GetAwaiter().GetResult();
                    }
                    catch (Exception error)
                    {
                        _checkpointFailure ??= error;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return next;
        }
    }

    /// <summary>
    /// Writes a checkpoint unless the document's latest version already carries the same event.
    /// </summary>
    /// <remarks>
    /// <see cref="ConversationCheckpoint.EventId"/> is the deduplication key. The comparison is
    /// made inside the write's own transaction, on the same serial chain, so a duplicate
    /// delivered twice concurrently is still written once.
    /// </remarks>
    internal Task CheckpointAsync(ConversationCheckpoint checkpoint) =>
        QueueCheckpointAsync(() => CommitAsync(async tx =>
        {
            var draft = tx.Doc(_runtime.CheckpointDocument, Id);
            if (draft.Value?.EventId == checkpoint.EventId)
            {
                await Task.CompletedTask;
                return;
            }

            draft.Value = checkpoint;
            draft.Save(tx.AppendEntry(Id, "checkpoint", checkpoint.EventId));
            await Task.CompletedTask;
        }));

    /// <summary>
    /// Awaits every checkpoint write queued so far and reports the first failure.
    /// </summary>
    /// <remarks>
    /// This is the completion barrier: a durable submission does not report success until the
    /// checkpoints its turn produced have committed. Failures are reported, never retried
    /// silently, because a checkpoint that did not commit means the durable record is behind
    /// what the caller was told.
    /// </remarks>
    internal async Task CheckpointBarrierAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        lock (_checkpointGate)
        {
            pending = _checkpointChain;
        }

        await pending.WaitAsync(cancellationToken);

        if (_checkpointFailure is { } failure)
        {
            throw new InvalidOperationException(
                $"Conversation '{Id}' could not commit a checkpoint: {failure.Message}",
                failure);
        }
    }

    /// <summary>Sends a prompt and waits for the assistant's answer.</summary>
    public async Task<AssistantMessageEvent?> SubmitAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<AssistantMessageEvent?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        AssistantMessageEvent? last = null;
        string? answer = null;
        SetBusy(true);

        // Admission is durable before the prompt is sent. A store failure here means the send
        // never happens, so the caller sees the persistence failure rather than an unrecorded
        // prompt.
        var admissionId = await AdmitAsync(prompt);

        // A turn that calls tools emits an assistant message *before* the tool runs, and
        // the final answer comes after the tool result. Waiting for the session to go
        // idle therefore waits for the whole turn, not just its first message.
        using var subscription = Session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent message:
                    last = message;
                    if (!string.IsNullOrEmpty(message.Data.Content))
                    {
                        answer = message.Data.Content;
                    }

                    break;
                case ToolExecutionStartEvent start:
                    _toolCalls[start.Data.ToolCallId] = (start.Data.ToolName, start.Data.Arguments);
                    break;
                case ToolExecutionCompleteEvent complete:
                    RecordToolCheckpoint(complete);
                    break;
                case SessionIdleEvent:
                    completion.TrySetResult(last);
                    break;
                case SessionErrorEvent error:
                    // A host hook the sandbox denied is a failure of the host's own
                    // configuration, not of the conversation. When the host asked for that to
                    // be tolerated, the turn's answer is returned and the denial stays visible
                    // in SandboxDecisions.
                    if (_runtime.Options.TolerateHostHookFailures && IsHostHookFailure(error.Data.Message))
                    {
                        completion.TrySetResult(last);
                        break;
                    }

                    completion.TrySetException(new InvalidOperationException(error.Data.Message));
                    break;
            }
        });

        try
        {
            try
            {
                await Session.SendAsync(new MessageOptions { Prompt = prompt }, cancellationToken);
            }
            catch
            {
                // The send itself failed, so the prompt was not delivered: the admission is
                // settled as failed rather than left looking like it might have run.
                await SettleQuietlyAsync(admissionId, "failed");
                throw;
            }

            var result = await completion.Task.WaitAsync(cancellationToken);

            // The turn checkpoint is queued from the event that completed the turn, and the
            // barrier below is what makes the submission durable: success is reported only
            // after the checkpoint commits.
            if (result is not null)
            {
                await CheckpointAsync(TurnCheckpoint(result, answer));
            }

            await CheckpointBarrierAsync(cancellationToken);
            await SettleAsync(admissionId, "completed");
            return result;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The send was issued and its outcome is not a clean failure: the turn may have
            // run. The admission is marked uncertain and is never replayed automatically.
            await SettleQuietlyAsync(admissionId, "uncertain");
            throw;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Records a completed tool call as a checkpoint, deduplicated on the tool call identifier.
    /// </summary>
    /// <remarks>
    /// The write is queued on the conversation's serial chain and observed by the completion
    /// barrier, so it is awaited before the turn reports success rather than being a
    /// fire-and-forget continuation.
    /// </remarks>
    private void RecordToolCheckpoint(ToolExecutionCompleteEvent complete)
    {
        _toolCalls.TryRemove(complete.Data.ToolCallId, out var started);

        var checkpoint = new ConversationCheckpoint
        {
            CheckpointId = $"c{Guid.NewGuid():N}",
            Kind = "tool",
            EventId = $"tool:{complete.Data.ToolCallId}",
            ToolName = started.Name ?? complete.Data.ToolDescription?.Name,
            ToolArgs = started.Arguments?.GetRawText(),
            Success = complete.Data.Success,
            Error = complete.Data.Error?.Message,
            Content = complete.Data.Result?.Content,
            RecordedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        _ = CheckpointAsync(checkpoint);
    }

    private static ConversationCheckpoint TurnCheckpoint(AssistantMessageEvent message, string? answer) => new()
    {
        CheckpointId = $"c{Guid.NewGuid():N}",
        Kind = "turn",
        EventId = message.Data.MessageId is { Length: > 0 } messageId
            ? $"turn:{messageId}"
            : $"turn:{message.Id:N}",
        MessageId = message.Data.MessageId,
        Content = answer ?? message.Data.Content,
        RecordedAt = DateTimeOffset.UtcNow.ToString("O"),
    };

    private async Task SettleQuietlyAsync(string admissionId, string state)
    {
        try
        {
            await SettleAsync(admissionId, state);
        }
        catch (Exception)
        {
            // The failure that brought us here is the one worth reporting; a second failure
            // while settling must not replace it. The admission stays at its committed state,
            // which is exactly the "unknown outcome" the record exists to preserve.
        }
    }

    /// <summary>
    /// Whether a session error is the runtime reporting that a host hook was denied by the
    /// sandbox, rather than a failure of the turn itself.
    /// </summary>
    internal static bool IsHostHookFailure(string? message) =>
        message is not null
        && message.Contains("hook", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("code 126", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the stored transcript entries, newest first.</summary>
    /// <remarks>
    /// The transcript is the conversation's own entries: the ones the app wrote, plus the
    /// inherited prefix a fork was taken at. The entries the durable layer writes to anchor its
    /// own bookkeeping — the fork intent, and the agent choices a conversation was created or
    /// reconfigured with — are not part of it, so a fork's transcript is the parent's transcript
    /// up to the fork entry plus whatever the fork wrote itself.
    /// <para>
    /// The exclusion is derived from the store's document state, not from an entry's kind: the
    /// kind is caller-supplied, so an app may legitimately write a <c>"fork"</c> entry of its
    /// own. An entry is bookkeeping when every document version anchored to it belongs to an
    /// internal document kind and it is not an entry a fork in the conversation's ancestry was
    /// taken at. Because documents are persisted, the answer is the same after the store is
    /// reopened.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Entries(int limit = 20)
    {
        var entries = _runtime.Store.Entries(Id);
        var bookkeeping = _runtime.BookkeepingEntries(Id, entries);
        return entries
            .AsEnumerable()
            .Reverse()
            .Where(entry => !bookkeeping.Contains(entry))
            .Take(limit)
            .ToList();
    }

    /// <summary>Reads the effective model context: the transcript as the model sees it.</summary>
    public Task<IReadOnlyList<SessionEvent>> ContextAsync(CancellationToken cancellationToken = default) =>
        Session.GetEventsAsync(cancellationToken);

    /// <summary>Stores the agent choices for this conversation.</summary>
    public async Task ConfigureAsync(AgentChoices choices, CancellationToken cancellationToken = default)
    {
        await CommitAsync(async tx =>
        {
            var draft = tx.Doc(_runtime.AgentDocument, Id);
            draft.Value = choices;
            draft.Save(tx.AppendEntry(Id, "agent"));
            await Task.CompletedTask;
        });

        // The live session follows the stored choices: switching a conversation into
        // plan mode changes the tools it offers right away.
        var resolved = Agent();
        if (Session is not null)
        {
            await Session.SetToolsAsync(resolved.Tools.Cast<AIFunctionDeclaration>().ToList(), cancellationToken);
        }
    }

    /// <summary>Reads the stored agent choices.</summary>
    public AgentChoices? Choices() => Snapshot(_runtime.AgentDocument);

    /// <summary>Resolves the stored choices against the registry.</summary>
    public ResolvedAgent Agent()
    {
        var choices = Choices() ?? new AgentChoices();
        var extensions = _runtime.ResolveExtensions(choices.Extensions);
        var tools = _runtime.ResolveTools(extensions, choices.Tools);
        return new ResolvedAgent(
            choices.Model,
            choices.ReasoningEffort,
            choices.WorkingDirectory,
            choices.Instructions,
            choices.Streaming,
            choices.Hooks,
            choices.InfiniteSessions,
            extensions,
            tools,
            choices.Permissions,
            choices.Sandbox);
    }

    /// <summary>Forks this conversation at the given entry.</summary>
    /// <param name="entryId">
    /// This conversation's <b>durable entry id</b> — the value <see cref="Transaction.AppendEntry"/>
    /// returns, e.g. <c>e7</c> — never an SDK event id. The fork holds that entry and everything
    /// before it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The forked conversation, with its session attached.</returns>
    public Task<DurableConversation> ForkAsync(string entryId, CancellationToken cancellationToken = default) =>
        _runtime.ForkAsync(this, entryId, cancellationToken);
}

/// <summary>Options for opening a <see cref="DurableHarness"/>.</summary>
public sealed class DurableHarnessOptions
{
    /// <summary>
    /// The permission handler installed on every session the harness creates, or <c>null</c>
    /// to keep the durable default.
    /// </summary>
    /// <remarks>
    /// The durable default is <see cref="PermissionPolicy.Handler"/>, which is fail-closed
    /// against the conversation's stored grants. Supplying a handler here replaces that
    /// decision for every conversation the harness opens, so it is the escape hatch for a host
    /// that has its own approval UI — not a way to widen a single conversation's authority.
    /// <para>
    /// The SDK's own default is <see cref="PermissionHandler.ApproveAll"/>, which approves
    /// everything. Passing it here is possible and is exactly as unsafe as it sounds: the
    /// agent's authority then lives outside the store.
    /// </para>
    /// </remarks>
    public Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>? PermissionHandler { get; set; }

    /// <summary>
    /// Whether repository-origin file hooks run for the sessions this harness creates, or
    /// <c>null</c> to leave the SDK default.
    /// </summary>
    /// <remarks>
    /// A host hook is host code, not sandboxed code: the runtime runs it inside the session's
    /// sandbox, so a sandbox policy that does not grant the hook's own command denies it and
    /// the tool call fails after it succeeded. A host that runs sandboxed conversations and
    /// does not want to widen the policy for its own hooks turns them off here.
    /// <para>
    /// This flag only controls repository-origin hooks (<c>.github/hooks/</c> and trusted
    /// repository-configured roots). A hook installed in the user's own configuration
    /// directory — <c>~/.copilot/hooks/</c> — is not repository-origin, so it still runs with
    /// this set to <c>false</c>. Turning that off is a host configuration decision outside
    /// this harness; see <c>docs/SANDBOX_MATRIX.md</c>.
    /// </para>
    /// <para>
    /// This is host configuration, not durable state: it is not persisted with the
    /// conversation, so it never travels with a fork.
    /// </para>
    /// </remarks>
    public bool? EnableFileHooks { get; set; }

    /// <summary>
    /// Whether a turn that fails only because a host hook was denied by the sandbox is
    /// reported as an answer instead of an exception.
    /// </summary>
    /// <remarks>
    /// The runtime runs host hooks inside the session's sandbox. A hook the policy does not
    /// grant is denied, and the runtime reports the turn as a session error even though the
    /// tool call itself succeeded. That failure is about the host's own configuration, not
    /// about the conversation, so a host that runs sandboxed conversations can choose to
    /// tolerate it: the turn's answer is returned and the denial is still visible in
    /// <see cref="DurableConversation.SandboxDecisions"/>.
    /// <para>
    /// The default is <c>false</c>: a session error is an error. Only a failure the runtime
    /// attributes to a hook is tolerated; a genuine tool or model failure still throws.
    /// </para>
    /// </remarks>
    public bool TolerateHostHookFailures { get; set; }

    /// <summary>
    /// The name of the environment variable that selects the runtime executable when
    /// <see cref="RuntimePath"/> is not set. Its value is a path, exactly like
    /// <see cref="RuntimePath"/>.
    /// </summary>
    public const string RuntimePathEnvironmentVariable = "DURABLE_RUNTIME_PATH";

    /// <summary>
    /// The runtime executable the harness connects to over stdio, or <c>null</c> to spawn the
    /// bundled runtime.
    /// </summary>
    /// <remarks>
    /// When this is set, <see cref="DurableHarness.OpenAsync(DurableStore, ExtensionRegistry, DurableHarnessOptions, CancellationToken)"/>
    /// connects to that executable with the SDK's
    /// <see cref="RuntimeConnection.ForStdio"/> transport instead of the bundled runtime. When
    /// it is <c>null</c>, the <see cref="RuntimePathEnvironmentVariable"/> environment variable
    /// (<c>DURABLE_RUNTIME_PATH</c>) is consulted instead; when that is unset or empty too, the
    /// harness spawns the bundled runtime exactly as it always has.
    /// <para>
    /// The environment variable exists so a script can select a runtime without editing the
    /// examples: <c>DURABLE_RUNTIME_PATH=/path/to/runtime scripts/validate.sh</c>. A value set
    /// here wins over the environment variable.
    /// </para>
    /// <para>
    /// A path that names no existing file fails the open with an exception that names the path,
    /// before any process is started.
    /// </para>
    /// <para>
    /// This is host configuration, not durable state: it is not persisted with the
    /// conversation, so it never travels with a fork.
    /// </para>
    /// </remarks>
    public string? RuntimePath { get; set; }

    /// <summary>
    /// Arguments passed to the runtime executable named by <see cref="RuntimePath"/>.
    /// </summary>
    /// <remarks>
    /// A runtime selected by path is started with these arguments, so a host can point the
    /// harness at a runtime that needs its own configuration — a recorded transcript, for
    /// instance. The bundled runtime is started with none.
    /// </remarks>
    public IList<string> RuntimeArguments { get; set; } = Array.Empty<string>();
}

/// <summary>
/// The shared durable runtime: the one place that binds a store, an SDK client, a registry, and
/// the conversations over them.
/// </summary>
/// <remarks>
/// <see cref="DurableHarness"/> and <see cref="DurableCopilotClient"/> are two front doors onto
/// this type, not two implementations. Everything that makes a conversation durable — the
/// conversation binding, the session configuration, extension and tool resolution, task
/// coordination, and the lifecycle — lives here, so a conversation created through either door
/// is the same object with the same behaviour.
/// <para>
/// <b>Binding.</b> The runtime is constructed with the registry and the host options, which are
/// known before anything is opened, and is bound to a store and a client by
/// <see cref="Attach"/>. Binding is not I/O: it records two references, decides ownership, and
/// claims the store's single task-scheduler slot. The harness binds in its constructor because
/// it has already created both; the facade binds during
/// <see cref="DurableCopilotClient.InitializeAsync"/> because creating them is the I/O.
/// </para>
/// <para>
/// <b>One runner per store.</b> A store has at most one task scheduler, and the runtime claims
/// it. A second runtime over the same store fails the claim instead of silently replacing the
/// first, which is what keeps two hosts from running the same task twice.
/// </para>
/// </remarks>
internal sealed class DurableRuntime : IAsyncDisposable
{
    /// <summary>
    /// The model used when a conversation does not name one. It is a cheap,
    /// reasoning-capable model: the examples are about durable mechanics, not model
    /// quality, and a fixed model makes an example's behaviour reproducible.
    /// </summary>
    internal const string DefaultModel = "gpt-6-luna";

    /// <summary>
    /// JSON-RPC 2.0's reserved "method not found" code. The SDK exposes it as
    /// <c>RemoteRpcException.MethodNotFoundErrorCode</c>, but that member is not public in the
    /// referenced package, so the value is repeated here.
    /// </summary>
    private const int MethodNotFoundErrorCode = -32601;

    private readonly Dictionary<string, DurableConversation> _conversations = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TaskOutcome>> _taskCompletions = [];
    private readonly ConcurrentDictionary<string, byte> _runningTasks = [];
    private readonly ConcurrentDictionary<string, Task> _inFlight = [];
    private readonly object _lifecycleGate = new();

    private CopilotClient? _client;
    private DurableStore? _store;
    private Dictionary<string, ModelInfo>? _models;
    private bool _disposed;
    private DurableModelReport _effectiveModel = DurableModelReport.NotReported;
    private readonly object _modelGate = new();

    internal DurableRuntime(ExtensionRegistry registry, DurableHarnessOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Registry = registry;
        Options = options ?? new DurableHarnessOptions();
        // Rewindable with "asOf", so a fork starts with the agent choices its parent had at
        // the fork entry.
        AgentDocument = new DocumentDefinition<AgentChoices>("durable.agent", DocumentHistory.Rewindable, ForkPolicy.AsOf);
        // "initial", so a fork of a fork starts with a fresh intent instead of inheriting the
        // parent's completed one.
        ForkDocument = new DocumentDefinition<ForkIntent>("durable.fork", DocumentHistory.Rewindable, ForkPolicy.Initial);
        // Initial, so a fork does not inherit its parent's SDK session id: the fork has its own
        // native transcript, and resuming the parent's would replay the wrong conversation.
        SessionBindingDocument = new DocumentDefinition<SessionBinding>("durable.session-binding", DocumentHistory.Rewindable, ForkPolicy.Initial);
        AdmissionDocument = new DocumentDefinition<InputAdmission>("durable.admission", DocumentHistory.Rewindable, ForkPolicy.AsOf);
        CheckpointDocument = new DocumentDefinition<ConversationCheckpoint>("durable.checkpoint", DocumentHistory.Rewindable, ForkPolicy.AsOf);
    }

    /// <summary>The document that records which SDK session a durable conversation is bound to.</summary>
    internal DocumentDefinition<SessionBinding> SessionBindingDocument { get; }

    /// <summary>The document that records each prompt's admission, before it is sent.</summary>
    internal DocumentDefinition<InputAdmission> AdmissionDocument { get; }

    /// <summary>The document that records a checkpoint per completed turn or tool call.</summary>
    internal DocumentDefinition<ConversationCheckpoint> CheckpointDocument { get; }

    /// <summary>The Copilot client, once the runtime is bound.</summary>
    internal CopilotClient Client => _client
        ?? throw new InvalidOperationException(
            "The SDK client is created during InitializeAsync; await that first.");

    /// <summary>The durable store, once the runtime is bound.</summary>
    internal DurableStore Store => _store
        ?? throw new InvalidOperationException(
            "The store is created during InitializeAsync; await that first.");

    /// <summary>The extension registry. Never unavailable: an empty registry is a valid one.</summary>
    internal ExtensionRegistry Registry { get; }

    /// <summary>The host configuration applied to every session this runtime configures.</summary>
    internal DurableHarnessOptions Options { get; }

    /// <summary>The document holding per-conversation agent choices.</summary>
    internal DocumentDefinition<AgentChoices> AgentDocument { get; }

    /// <summary>The document holding a fork's intent, owned by the fork.</summary>
    internal DocumentDefinition<ForkIntent> ForkDocument { get; }

    /// <summary>The conversations this runtime knows about.</summary>
    internal IReadOnlyCollection<DurableConversation> Conversations => _conversations.Values;

    /// <summary>
    /// The most recent model any conversation over this runtime reported.
    /// </summary>
    /// <remarks>
    /// It is <see cref="DurableModelReport.NotReported"/> until a session or a turn reports a
    /// model. It never falls back to <see cref="DefaultModel"/>: the default is what the host
    /// asked for, and this property reports only what the runtime said.
    /// </remarks>
    internal DurableModelReport EffectiveModel
    {
        get
        {
            lock (_modelGate)
            {
                return _effectiveModel;
            }
        }
    }

    /// <summary>
    /// Records the model a conversation observed. Conversations report here as well as in their
    /// own list, so the runtime-level value follows the most recent session or turn.
    /// </summary>
    internal void PublishModel(DurableModelReport report)
    {
        if (!report.HasModel)
        {
            return;
        }

        lock (_modelGate)
        {
            _effectiveModel = report;
        }
    }

    /// <summary>Whether the runtime is bound to a store and a client.</summary>
    internal bool IsAttached => _store is not null && _client is not null;

    /// <summary>Whether disposal disposes the SDK client.</summary>
    internal bool OwnsClient { get; private set; }

    /// <summary>Whether disposal disposes the store.</summary>
    internal bool OwnsStore { get; private set; }

    /// <summary>
    /// Binds the runtime to a store and a client, deciding what disposal owns.
    /// </summary>
    /// <remarks>
    /// Binding performs no I/O. It claims the store's task-scheduler slot, so a store that
    /// another runtime already drives is rejected here rather than being silently taken over.
    /// </remarks>
    internal void Attach(CopilotClient client, DurableStore store, bool ownsClient, bool ownsStore)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_store is not null || _client is not null)
            {
                throw new InvalidOperationException("The runtime is already bound to a store and a client.");
            }

            _client = client;
            _store = store;
            OwnsClient = ownsClient;
            OwnsStore = ownsStore;
        }

        // A task created by another task asks the runtime to schedule it.
        store.ClaimTaskScheduler(this, (taskId, cancellationToken) => _ = RunTaskAsync(taskId, cancellationToken));
    }

    /// <summary>
    /// Whether the given model accepts a reasoning effort. The runtime rejects a session
    /// outright when it does not, so this is checked before the session is created.
    /// </summary>
    private bool SupportsReasoningEffort(string? model)
    {
        if (model is null || model == "auto")
        {
            return false;
        }

        _models ??= Client.ListModelsAsync().GetAwaiter().GetResult().ToDictionary(info => info.Id);
        return _models.TryGetValue(model, out var info) && info.SupportedReasoningEfforts is { Count: > 0 };
    }

    /// <summary>
    /// Finds a conversation by id, restoring it from the store if the runtime has not
    /// opened it yet. A conversation that was created before a restart is still there.
    /// </summary>
    internal DurableConversation? FindConversation(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var existing))
        {
            return existing;
        }

        var record = Store.Conversation(conversationId);
        if (record is null)
        {
            return null;
        }

        var choices = Store.Document(AgentDocument.Kind, conversationId)?.Latest is { } latest
            ? latest.Deserialize<AgentChoices>(DurableJson.Options)
            : null;

        var session = Client.CreateSessionAsync(BuildSessionConfig(choices, null)).GetAwaiter().GetResult();
        var conversation = new DurableConversation(this, null!, record);
        conversation.Attach(session);
        conversation.BindAsync(session.SessionId, choices?.Model ?? DefaultModel).GetAwaiter().GetResult();
        _conversations[conversationId] = conversation;
        return conversation;
    }

    /// <summary>Creates a conversation and its Copilot session.</summary>
    internal async Task<DurableConversation> CreateConversationAsync(
        AgentChoices? choices = null,
        string? ownerTaskId = null,
        string? ownerConversationId = null,
        CancellationToken cancellationToken = default)
    {
        // Checked before the record is committed: a conversation whose policy cannot be applied
        // must not be written at all, or the store would hold a conversation that can never be
        // opened while claiming the protection it does not have.
        if (choices?.Sandbox is { } requested)
        {
            ValidateSandboxPolicy(requested);
        }

        var record = await Store.CommitAsync(tx =>
        {
            var created = tx.CreateConversation(ownerTaskId, ownerConversationId);
            if (choices is not null)
            {
                var draft = tx.Doc(AgentDocument, created.Id);
                draft.Value = choices;
                draft.Save(tx.AppendEntry(created.Id, "agent"));
            }

            return Task.FromResult(created);
        });

        // The conversation handle exists before the session is created, so prompt
        // sections can read the conversation's durable documents.
        var conversation = new DurableConversation(this, null!, record);
        var session = await Client.CreateSessionAsync(BuildSessionConfig(choices, conversation), cancellationToken);
        conversation.Attach(session);
        await conversation.BindAsync(session.SessionId, choices?.Model ?? DefaultModel, cancellationToken);
        _conversations[record.Id] = conversation;
        return conversation;
    }

    /// <summary>Returns the root conversation, creating it on first use.</summary>
    internal async Task<DurableConversation> RootAsync(
        AgentChoices? choices = null,
        CancellationToken cancellationToken = default)
    {
        var existing = _conversations.Values.FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        // A store reopened from disk already holds its conversations. Restore them
        // before creating a new one, so the root is the conversation that was
        // there before, not a second one.
        await RestoreConversationsAsync(cancellationToken);

        existing = _conversations.Values.FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        return await CreateConversationAsync(choices, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Restores the conversations a reopened store already holds, giving each one a
    /// Copilot session again.
    /// </summary>
    /// <remarks>
    /// Parents are restored before forks, because completing an interrupted fork forks its
    /// parent's session and so needs the parent to have one.
    /// </remarks>
    private async Task RestoreConversationsAsync(CancellationToken cancellationToken)
    {
        var records = Store.Conversations.ToList();

        foreach (var record in records.Where(record => ForkIntentOf(record.Id) is null))
        {
            await RestoreConversationAsync(record, cancellationToken);
        }

        foreach (var record in records.Where(record => ForkIntentOf(record.Id) is not null))
        {
            await RestoreForkAsync(record, cancellationToken);
        }
    }

    /// <summary>Gives a restored conversation a fresh session.</summary>
    private async Task RestoreConversationAsync(ConversationRecord record, CancellationToken cancellationToken)
    {
        if (_conversations.ContainsKey(record.Id))
        {
            return;
        }

        var choices = Store.Document(AgentDocument.Kind, record.Id)?.Latest is { } latest
            ? latest.Deserialize<AgentChoices>(DurableJson.Options)
            : null;

        var session = await Client.CreateSessionAsync(BuildSessionConfig(choices, null), cancellationToken);
        var conversation = new DurableConversation(this, null!, record);
        conversation.Attach(session);
        await conversation.BindAsync(session.SessionId, choices?.Model ?? DefaultModel, cancellationToken);
        _conversations[record.Id] = conversation;
    }

    /// <summary>
    /// Restores a fork, completing it when its creation was interrupted and abandoning it when
    /// it can no longer be completed.
    /// </summary>
    private async Task RestoreForkAsync(ConversationRecord record, CancellationToken cancellationToken)
    {
        if (_conversations.ContainsKey(record.Id))
        {
            return;
        }

        var intent = ForkIntentOf(record.Id)!;
        if (intent.State is ForkIntentState.Abandoned)
        {
            return;
        }

        if (intent.State is ForkIntentState.Completed)
        {
            // A reopened store starts every conversation on a fresh session, forks included.
            await RestoreConversationAsync(record, cancellationToken);
            return;
        }

        // Pending: the durable half exists but the native fork never finished. Complete it if
        // the parent is available; otherwise abandon it, because a fork with no session and no
        // parent can never be completed.
        if (!_conversations.TryGetValue(intent.ParentConversationId, out var parent))
        {
            await AbandonForkAsync(record.Id);
            return;
        }

        var choices = Store.Document(AgentDocument.Kind, record.Id)?.Latest is { } latest
            ? latest.Deserialize<AgentChoices>(DurableJson.Options)
            : null;

        var conversation = new DurableConversation(this, null!, record);
        var session = await ForkSessionAsync(parent, conversation, choices, cancellationToken);
        conversation.Attach(session);
        _conversations[record.Id] = conversation;
    }

    /// <summary>Forks a conversation at an entry.</summary>
    /// <param name="parent">The conversation to fork from.</param>
    /// <param name="entryId">
    /// The parent's <b>durable entry id</b> (the value <see cref="Transaction.AppendEntry"/>
    /// returns, e.g. <c>e7</c>) to fork at — never an SDK event id. The durable transcript is
    /// the boundary the store can map, so it is the one the fork is defined by.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The forked conversation, with its session attached.</returns>
    /// <remarks>
    /// <para>
    /// <b>Boundary asymmetry.</b> The durable transcript is copied <i>inclusively</i> — the fork
    /// holds <paramref name="entryId"/> and everything before it — while the native fork RPC
    /// takes an exclusive event boundary. The two are not the same coordinate space, and no
    /// durable-entry-to-event mapping exists, so the native fork is taken over the whole
    /// transcript and the durable copy is what bounds the fork. The fork's own entries are
    /// appended after the inherited ones, so the inherited prefix is exactly the parent's
    /// transcript up to <paramref name="entryId"/>.
    /// </para>
    /// <para>
    /// <b>Choices.</b> The fork is configured from the choices the parent had at
    /// <paramref name="entryId"/>, not the ones it has now, so a fork can never silently widen
    /// the parent's grants.
    /// </para>
    /// <para>
    /// <b>Recoverability.</b> The fork's durable half — record, transcript, documents, and a
    /// <see cref="ForkIntentState.Pending"/> intent — is committed before the native fork is
    /// attempted. A crash in between leaves a fork that the next open completes or abandons.
    /// </para>
    /// </remarks>
    internal async Task<DurableConversation> ForkAsync(
        DurableConversation parent,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (!Store.Entries(parent.Id).Contains(entryId))
        {
            throw new ArgumentException(
                $"'{entryId}' is not an entry of conversation '{parent.Id}'.",
                nameof(entryId));
        }

        // The choices the parent had at the boundary, not the ones it has now.
        var choices = ChoicesAsOf(parent.Id, entryId);

        var record = await Store.CommitAsync(tx =>
        {
            var created = tx.CreateConversation(parentConversationId: parent.Id, forkEntryId: entryId);
            // The fork inherits the parent's transcript up to the entry it was taken at.
            Store.Provider.CopyEntries(parent.Id, created.Id, entryId);
            // Materialize first: copying a document adds to the same collection being enumerated.
            foreach (var document in Store.Documents.Where(d => d.OwnerId == parent.Id).ToList())
            {
                Store.Provider.CopyDocument(document, created.Id, entryId);
            }

            // Durable before the native fork, so a crash between the two is detectable.
            var intent = tx.Doc(ForkDocument, created.Id);
            intent.Value = new ForkIntent
            {
                ParentConversationId = parent.Id,
                ForkEntryId = entryId,
                State = ForkIntentState.Pending,
            };
            intent.Save(tx.AppendEntry(created.Id, "fork"));

            return Task.FromResult(created);
        });

        var conversation = new DurableConversation(this, null!, record);
        var session = await ForkSessionAsync(parent, conversation, choices, cancellationToken);
        conversation.Attach(session);
        // A fork names its own session: the binding document is ForkPolicy.Initial, so a fork
        // never inherits its parent's session id, and this write records the new one.
        await conversation.BindAsync(session.SessionId, parent.Choices()?.Model ?? DefaultModel, cancellationToken);
        _conversations[record.Id] = conversation;
        return conversation;
    }

    /// <summary>Reads the agent choices a conversation had at an entry.</summary>
    private AgentChoices? ChoicesAsOf(string conversationId, string entryId) =>
        Store.Document(AgentDocument.Kind, conversationId)?.AsOfOrBefore(entryId, Store.Provider.SequenceOf) is { } value
            ? value.Deserialize<AgentChoices>(DurableJson.Options)
            : null;

    /// <summary>Reads a conversation's fork intent, or <c>null</c> when it is not a fork.</summary>
    private ForkIntent? ForkIntentOf(string conversationId) =>
        Store.Document(ForkDocument.Kind, conversationId)?.Latest is { } latest
            ? latest.Deserialize<ForkIntent>(DurableJson.Options)
            : null;

    /// <summary>
    /// The entries of a conversation that exist only to anchor the durable layer's own
    /// bookkeeping, and so are not part of the conversation's transcript.
    /// </summary>
    /// <remarks>
    /// The durable layer writes an entry whenever it commits one of its internal documents: the
    /// fork intent, and the agent choices a conversation was created or reconfigured with. Those
    /// entries are durable state, not conversation, so they are kept out of
    /// <see cref="DurableConversation.Entries"/>.
    /// <para>
    /// The set is derived from the store's document state rather than from an entry's kind,
    /// because the kind is caller-supplied: an app may write a <c>"fork"</c> entry of its own and
    /// must still see it. An entry is bookkeeping when it is anchored by at least one document
    /// version and every version anchored to it belongs to an internal document kind. The entry a
    /// fork was taken at is never bookkeeping: it is inherited transcript, and a fork's copy of
    /// the parent's agent document is anchored to it.
    /// </para>
    /// <para>
    /// A fork inherits the parent's entries verbatim, including the ones that were bookkeeping in
    /// the parent, so the answer is the union over the conversation and its ancestors. Each
    /// conversation in the chain contributes its own documents, and every fork entry in the chain
    /// is inherited transcript rather than bookkeeping.
    /// </para>
    /// <para>
    /// Documents are persisted, so a reopened store derives the same set.
    /// </para>
    /// </remarks>
    internal HashSet<string> BookkeepingEntries(string conversationId, IReadOnlyList<string> entries)
    {
        var bookkeeping = new HashSet<string>(StringComparer.Ordinal);
        var boundaries = new HashSet<string>(StringComparer.Ordinal);
        var userAnchored = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        for (var current = conversationId; current is not null && visited.Add(current);)
        {
            var record = Store.Conversation(current);
            if (record?.ForkEntryId is { } boundary)
            {
                boundaries.Add(boundary);
            }

            var anchored = new HashSet<string>(StringComparer.Ordinal);

            foreach (var document in Store.Documents.Where(document => document.OwnerId == current))
            {
                var isInternal = IsInternalDocument(document.Kind);
                foreach (var version in document.Versions)
                {
                    if (version.EntryId.Length == 0)
                    {
                        continue;
                    }

                    anchored.Add(version.EntryId);
                    if (!isInternal)
                    {
                        userAnchored.Add(version.EntryId);
                    }
                }
            }

            foreach (var entry in entries)
            {
                if (anchored.Contains(entry))
                {
                    bookkeeping.Add(entry);
                }
            }

            current = record?.ParentConversationId;
        }

        bookkeeping.ExceptWith(userAnchored);
        bookkeeping.ExceptWith(boundaries);
        return bookkeeping;
    }

    /// <summary>Whether a document kind belongs to the durable layer rather than to the app.</summary>
    /// <remarks>
    /// Derived from the documents this runtime defines rather than from a list of kind strings,
    /// so a document the durable layer adds later is covered without a second edit here.
    /// </remarks>
    private bool IsInternalDocument(string kind) =>
        InternalDocumentKinds.Contains(kind);

    /// <summary>The kinds of the documents the durable layer owns.</summary>
    private HashSet<string> InternalDocumentKinds => _internalDocumentKinds ??=
    [
        AgentDocument.Kind,
        ForkDocument.Kind,
        SessionBindingDocument.Kind,
        AdmissionDocument.Kind,
        CheckpointDocument.Kind,
    ];

    private HashSet<string>? _internalDocumentKinds;

    /// <summary>
    /// Gives a fork its native session: forks the parent's transcript, resumes the result, and
    /// records the session on the intent so a later open can find it again.
    /// </summary>
    private async Task<CopilotSession> ForkSessionAsync(
        DurableConversation parent,
        DurableConversation conversation,
        AgentChoices? choices,
        CancellationToken cancellationToken)
    {
        string? forkedSessionId = null;
        try
        {
            var forked = await Client.Rpc.Sessions.ForkAsync(
                parent.SessionId,
                toEventId: null,
                name: null,
                cancellationToken);
            forkedSessionId = forked.SessionId;
        }
        catch (IOException ex) when (ex.InnerException is RemoteRpcException { ErrorCode: MethodNotFoundErrorCode })
        {
            // A runtime without sessions.fork cannot fork natively. The durable half of the
            // fork is already committed, so it continues as a new session over the inherited
            // transcript rather than failing outright.
        }

        var session = forkedSessionId is null
            ? await Client.CreateSessionAsync(BuildSessionConfig(choices, conversation), cancellationToken)
            : await Client.ResumeSessionAsync(
                forkedSessionId,
                BuildResumeSessionConfig(choices, conversation),
                cancellationToken);

        await Store.CommitAsync(tx =>
        {
            var intent = tx.Doc(ForkDocument, conversation.Id);
            intent.Value.SessionId = session.SessionId;
            intent.Value.State = ForkIntentState.Completed;
            intent.Save(tx.AppendEntry(conversation.Id, "fork"));
            return Task.CompletedTask;
        });

        return session;
    }

    /// <summary>Marks a fork abandoned, so it is never given a session.</summary>
    private Task AbandonForkAsync(string conversationId) =>
        Store.CommitAsync(tx =>
        {
            var intent = tx.Doc(ForkDocument, conversationId);
            intent.Value.State = ForkIntentState.Abandoned;
            intent.Save(tx.AppendEntry(conversationId, "fork"));
            return Task.CompletedTask;
        });

    /// <summary>Creates a durable task and schedules it.</summary>
    internal async Task<DurableTaskRecord> CreateTaskAsync(
        DurableTaskDefinition definition,
        JsonElement input,
        string? ownerConversationId = null,
        string? ownerTaskId = null,
        bool background = false,
        CancellationToken cancellationToken = default)
    {
        var record = await Store.CommitAsync(tx => Task.FromResult(
            tx.CreateTask(definition, input, ownerConversationId, ownerTaskId, background)));

        _ = RunTaskAsync(record.Id, cancellationToken);
        return record;
    }

    /// <summary>Runs a task to completion, resuming from its last checkpoint.</summary>
    internal async Task<TaskOutcome> RunTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var completion = _taskCompletions.GetOrAdd(taskId, _ => new TaskCompletionSource<TaskOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously));

        var record = Store.Task(taskId);
        if (record is null)
        {
            var missing = new TaskOutcome("failed", Error: "unknown task");
            completion.TrySetResult(missing);
            return missing;
        }

        // A task runs once at a time. A second caller joins the run in flight.
        if (!_runningTasks.TryAdd(taskId, 0))
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }

        var run = RunTaskCoreAsync(taskId, record, completion, cancellationToken);
        // Recorded before the first await so disposal can drain a run that is mid-commit.
        _inFlight[taskId] = run;
        try
        {
            return await run;
        }
        finally
        {
            _inFlight.TryRemove(taskId, out _);
            _runningTasks.TryRemove(taskId, out _);
        }
    }

    private async Task<TaskOutcome> RunTaskCoreAsync(
        string taskId,
        DurableTaskRecord record,
        TaskCompletionSource<TaskOutcome> completion,
        CancellationToken cancellationToken)
    {
        if (record.Status == TaskStatus.Terminal)
        {
            var existing = record.Outcome is { } outcome
                ? outcome.Deserialize<TaskOutcome>(DurableJson.Options)!
                : new TaskOutcome("completed");
            completion.TrySetResult(existing);
            return existing;
        }

        var definition = Registry.FindTask(record.Name);
        if (definition is null)
        {
            var unknown = new TaskOutcome("failed", Error: $"unknown task definition {record.Name}");
            completion.TrySetResult(unknown);
            return unknown;
        }

        if (record.Status == TaskStatus.Running)
        {
            record.Restarts++;
            // The restart count is part of the task's durable state, so the provider
            // is told about it before the transaction commits.
            Store.Provider.UpdateTask(record);
            await Store.CommitAsync(_ => Task.CompletedTask);
        }

        var runtime = new TaskRuntime(Store, record, definition, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            // The task may have been aborted while it was waiting on its children.
            // An abort is durable: the record is terminal, so the run stops here
            // instead of carrying on to the next phase.
            if (Store.Task(taskId)?.Status == TaskStatus.Terminal)
            {
                break;
            }

            var phase = record.Checkpoint.ValueKind == JsonValueKind.Object
                && record.Checkpoint.TryGetProperty("phase", out var phaseElement)
                    ? phaseElement.GetString() ?? "run"
                    : "run";

            if (!definition.Phases.TryGetValue(phase, out var handler))
            {
                await Store.CommitTaskAsync(taskId, () => TaskCommit.Failed($"unknown phase {phase}"));
                break;
            }

            TaskCommit commit;
            try
            {
                commit = await handler(runtime);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The task was interrupted. Its last checkpoint stays in place, so a
                // later run continues from there.
                break;
            }
            catch (Exception exception)
            {
                await Store.CommitTaskAsync(taskId, () => TaskCommit.Failed(exception.Message));
                break;
            }

            if (commit.Outcome is { } outcome)
            {
                completion.TrySetResult(outcome);
                break;
            }

            // A task that is waiting on child tasks holds no invocation: it is
            // resumed when its children finish.
            if (commit.Status == TaskStatus.Waiting)
            {
                var children = commit.WaitingOn;
                await WaitForChildrenAsync(taskId, children, cancellationToken);
                continue;
            }
        }

        var final = Store.Task(taskId)?.Outcome is { } finalOutcome
            ? finalOutcome.Deserialize<TaskOutcome>(DurableJson.Options)!
            : new TaskOutcome("failed", Error: "task did not finish");
        completion.TrySetResult(final);
        return final;
    }

    /// <summary>
    /// Waits for child tasks to finish, then resumes the waiting parent. The parent
    /// holds no invocation while it waits: its checkpoint says which children it is
    /// waiting on.
    /// </summary>
    private async Task WaitForChildrenAsync(
        string taskId,
        IReadOnlyList<string> children,
        CancellationToken cancellationToken)
    {
        foreach (var child in children)
        {
            var outcome = await WaitForTaskAsync(child, cancellationToken);
            if (outcome.Status == "failed")
            {
                // With fail-fast, the first failure aborts the others.
                foreach (var sibling in children.Where(id => id != child))
                {
                    await AbortTaskAsync(sibling, cancellationToken);
                }

                break;
            }
        }
    }

    /// <summary>Waits for a task to finish.</summary>
    internal async Task<TaskOutcome> WaitForTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var record = Store.Task(taskId);
        if (record is null)
        {
            return new TaskOutcome("failed", Error: "unknown task");
        }

        if (record.Status == TaskStatus.Terminal)
        {
            return record.Outcome is { } outcome
                ? outcome.Deserialize<TaskOutcome>(DurableJson.Options)!
                : new TaskOutcome("completed");
        }

        var completion = _taskCompletions.GetOrAdd(taskId, _ => new TaskCompletionSource<TaskOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously));

        // A task runs once at a time. A second caller joins the run in flight
        // instead of starting a competing one.
        _ = RunTaskAsync(taskId, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Aborts a task, running its abort handler.</summary>
    internal async Task AbortTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var record = Store.Task(taskId);
        if (record is null || record.Status == TaskStatus.Terminal)
        {
            return;
        }

        var definition = Registry.FindTask(record.Name);
        if (definition is null)
        {
            return;
        }

        // The children go first: a parent that is aborted while waiting on them
        // must not leave them running.
        foreach (var child in Store.Tasks.Where(task => task.OwnerTaskId == taskId))
        {
            await AbortTaskAsync(child.Id, cancellationToken);
        }

        var runtime = new TaskRuntime(Store, record, definition, cancellationToken);
        await definition.Abort(runtime);
    }

    /// <summary>
    /// Re-binds a stored conversation to the native session it recorded, or fails explicitly.
    /// </summary>
    /// <remarks>
    /// The binding is the record of which SDK session served the conversation. Resuming reads it
    /// and asks the client for that session: a native transcript that is still there is resumed,
    /// so the conversation continues with its history. A transcript that is gone throws
    /// <see cref="DurableRecoveryRequiredException"/> instead of quietly creating a new, empty
    /// session — a replacement would answer with no memory of the conversation while looking
    /// perfectly healthy. A conversation that never recorded a binding has nothing to recover,
    /// so it is bound to a fresh session and that binding is written.
    /// </remarks>
    internal async Task<DurableConversation> ResumeConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (_conversations.TryGetValue(conversationId, out var live))
        {
            return live;
        }

        var record = Store.Conversation(conversationId)
            ?? throw new InvalidOperationException($"No conversation '{conversationId}' is stored.");

        var choices = Store.Document(AgentDocument.Kind, conversationId)?.Latest is { } latest
            ? latest.Deserialize<AgentChoices>(DurableJson.Options)
            : null;

        var binding = Store.Document(SessionBindingDocument.Kind, conversationId)?.Latest is { } bound
            ? bound.Deserialize<SessionBinding>(DurableJson.Options)
            : null;

        var conversation = new DurableConversation(this, null!, record);

        if (binding?.SessionId is { Length: > 0 } recordedSessionId)
        {
            // The configuration is built before the metadata probe, so a stored policy that
            // cannot be applied fails here — before any RPC — rather than after the runtime has
            // already been asked to continue the session.
            var resumeConfig = BuildResumeSessionConfig(BuildSessionConfig(choices, conversation));

            // A null result is the SDK saying it has no such session: the native transcript this
            // conversation's history lives in is gone, and only the host can decide what to do.
            var metadata = await Client.GetSessionMetadataAsync(recordedSessionId, cancellationToken);
            if (metadata is null)
            {
                throw new DurableRecoveryRequiredException(conversationId, recordedSessionId);
            }

            var resumed = await Client.ResumeSessionAsync(recordedSessionId, resumeConfig, cancellationToken);
            conversation.Attach(resumed);
            _conversations[conversationId] = conversation;
            return conversation;
        }

        var session = await Client.CreateSessionAsync(BuildSessionConfig(choices, conversation), cancellationToken);
        conversation.Attach(session);
        await conversation.BindAsync(session.SessionId, choices?.Model ?? DefaultModel, cancellationToken);
        _conversations[conversationId] = conversation;
        return conversation;
    }

    /// <summary>
    /// Copies a session configuration into the shape resuming takes.
    /// </summary>
    /// <remarks>
    /// Resuming accepts a <see cref="ResumeSessionConfig"/>, which carries every setting a new
    /// session does but is a separate type. Copying rather than rebuilding keeps the two paths
    /// from drifting: the resumed session runs with exactly the configuration a new session
    /// would have been given.
    /// </remarks>
    private static ResumeSessionConfig BuildResumeSessionConfig(SessionConfig config) => new()
    {
        Model = config.Model,
        ReasoningEffort = config.ReasoningEffort,
        WorkingDirectory = config.WorkingDirectory,
        Streaming = config.Streaming,
        Hooks = config.Hooks,
        InfiniteSessions = config.InfiniteSessions,
        Tools = config.Tools,
        SystemMessage = config.SystemMessage,
        OnPermissionRequest = config.OnPermissionRequest,
        SandboxConfig = config.SandboxConfig,
        EnableFileHooks = config.EnableFileHooks,
    };

    /// <summary>Resumes every task that was running when the process stopped.</summary>
    /// <remarks>
    /// The tasks are awaited, not started and forgotten: a caller that resumes work needs to
    /// know when the work is done, and a fire-and-forget start would report success before a
    /// single phase had run. Each task is re-entered from its last checkpoint, and the outcomes
    /// come back in store order so a caller can pair them with the records it read.
    /// </remarks>
    internal async Task<IReadOnlyList<TaskOutcome>> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var resumable = Store.Tasks
            .Where(t => t.Status is TaskStatus.Running or TaskStatus.Pending)
            .ToList();

        var outcomes = new List<TaskOutcome>(resumable.Count);
        foreach (var record in resumable)
        {
            outcomes.Add(await RunTaskAsync(record.Id, cancellationToken));
        }

        return outcomes;
    }

    /// <summary>Reads the task graph: tasks and their owner edges.</summary>
    internal IReadOnlyList<(DurableTaskRecord Task, string? Owner)> TaskGraph() =>
        Store.Tasks
            .Select(task => (task, Owner: task.OwnerTaskId ?? task.OwnerConversationId))
            .ToList();

    /// <summary>Resolves extension names against the registry.</summary>
    internal IReadOnlyList<Extension> ResolveExtensions(IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return Registry.Extensions;
        }

        return names.Select(Registry.Find).Where(extension => extension is not null).Select(extension => extension!).ToList();
    }

    /// <summary>
    /// Resolves tool names against the selected extensions. When two selected extensions
    /// contribute a tool with the same name, the later one replaces the earlier one in
    /// place: the tool keeps its position in the list, but the later implementation wins.
    /// Wrappers are applied last, so a wrapper decorates whichever implementation won.
    /// </summary>
    internal IReadOnlyList<AIFunction> ResolveTools(IReadOnlyList<Extension> extensions, IReadOnlyList<string>? names)
    {
        // Later extensions replace earlier ones by name, keeping the original position.
        var available = new List<AIFunction>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tool in extensions.SelectMany(extension => extension.Tools))
        {
            if (indexByName.TryGetValue(tool.Name, out var index))
            {
                available[index] = tool;
            }
            else
            {
                indexByName[tool.Name] = available.Count;
                available.Add(tool);
            }
        }

        // A wrapper names the tool it decorates, so it wraps whichever implementation
        // won rather than capturing one of them.
        foreach (var wrapper in extensions.SelectMany(extension => extension.Wraps))
        {
            var index = indexByName.GetValueOrDefault(wrapper.ToolName);
            if (index >= 0)
            {
                available[index] = wrapper.Wrap(available[index]);
            }
        }

        if (names is null)
        {
            return available;
        }

        return names
            .Select(name => available.FirstOrDefault(tool => tool.Name == name))
            .Where(tool => tool is not null)
            .Select(tool => tool!)
            .ToList();
    }

    /// <summary>
    /// Maps a durable sandbox policy onto the SDK's wire type, expanding the
    /// <see cref="DurableSandboxPolicy.WorkspaceToken"/> token against the working directory.
    /// </summary>
    /// <remarks>
    /// Every nested object is constructed explicitly. The wire type's constructor leaves every
    /// reference null, so a field this method forgets is a field the runtime never sees — which
    /// is why the mapping is written out field by field rather than copied.
    /// </remarks>
    internal static SandboxConfig BuildSandboxConfig(DurableSandboxPolicy policy, string? workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateSandboxPolicy(policy);

        var config = new SandboxConfig
        {
            Enabled = policy.Enabled,
            AddCurrentWorkingDirectory = policy.AddCurrentWorkingDirectory,
            AllowBypass = policy.AllowBypass,
            AllowDevToolAccess = policy.AllowDevToolAccess,
            SandboxMcpServers = policy.SandboxMcpServers,
            SandboxLspServers = policy.SandboxLspServers,
        };

        if (policy.Auth is { } auth)
        {
            config.Auth = new SandboxConfigAuth { Gh = auth.Gh, Git = auth.Git };
        }

        if (policy.Credentials?.EnvVars is { } envVars)
        {
            var credentials = new SandboxCredentialsConfig { EnvVars = new Dictionary<string, SandboxMaskedEnvVar>() };
            foreach (var pair in envVars)
            {
                credentials.EnvVars[pair.Key] = new SandboxMaskedEnvVar
                {
                    InjectHosts = pair.Value?.ToList() ?? [],
                };
            }

            config.Credentials = credentials;
        }

        if (policy.Filesystem is { } filesystem)
        {
            config.UserPolicy ??= new SandboxConfigUserPolicy();
            config.UserPolicy.Filesystem = new SandboxConfigUserPolicyFilesystem
            {
                ClearPolicyOnExit = filesystem.ClearPolicyOnExit,
                DeniedPaths = ExpandPaths(filesystem.DeniedPaths, workingDirectory),
                ReadonlyPaths = ExpandPaths(filesystem.ReadonlyPaths, workingDirectory),
                ReadwritePaths = ExpandPaths(filesystem.ReadwritePaths, workingDirectory),
            };
        }

        if (policy.Network is { } network)
        {
            config.UserPolicy ??= new SandboxConfigUserPolicy();
            config.UserPolicy.Network = new SandboxConfigUserPolicyNetwork
            {
                AllowedHosts = network.AllowedHosts?.ToList() ?? [],
                BlockedHosts = network.BlockedHosts?.ToList() ?? [],
                AllowOutbound = network.AllowOutbound,
                AllowLocalNetwork = network.AllowLocalNetwork,
            };

            // A proxy without a URL is not a proxy: the wire type requires one, so a policy
            // that names only credentials is dropped rather than sent as an empty URL.
            if (network.Proxy is { Url: { } proxyUrl } proxy)
            {
                var wireProxy = new SandboxConfigUserPolicyNetworkProxy { Url = proxyUrl };
                if (proxy.Username is { } username)
                {
                    wireProxy.Username = username;
                }

                if (proxy.Password is { } password)
                {
                    wireProxy.Password = password;
                }

                config.UserPolicy.Network.Proxy = wireProxy;
            }
        }

        if (policy.KeychainAccess is { } keychainAccess)
        {
            config.UserPolicy ??= new SandboxConfigUserPolicy();
            config.UserPolicy.Seatbelt = new SandboxConfigUserPolicySeatbelt { KeychainAccess = keychainAccess };
        }

        return config;
    }

    /// <summary>
    /// Rejects a stored policy the wire type cannot express faithfully.
    /// </summary>
    /// <remarks>
    /// Each rule here guards a field the mapping would otherwise have to narrow. Narrowing is
    /// the failure mode this method exists to prevent: a policy that asks for one thing and a
    /// session that gets another is a silent downgrade, and the store would go on claiming the
    /// protection the session does not have.
    /// </remarks>
    private static void ValidateSandboxPolicy(DurableSandboxPolicy policy)
    {
        // A disabled sandbox enforces nothing, so a policy that disables it while still naming
        // restrictions describes a session that would run unsandboxed while the store claims
        // otherwise. That is the silent downgrade this method exists to refuse.
        if (!policy.Enabled
            && (policy.Auth is not null
                || policy.Credentials is not null
                || policy.Filesystem is not null
                || policy.Network is not null
                || policy.KeychainAccess is not null))
        {
            throw new DurableSandboxPolicyException(
                "the policy disables the sandbox but still names credential, filesystem, network, "
                + "or keychain restrictions, which a disabled sandbox cannot enforce. Enable the "
                + "sandbox, or clear the restrictions to opt out deliberately.",
                policy);
        }

        if (policy.Credentials?.EnvVars is not { } envVars)
        {
            return;
        }

        foreach (var pair in envVars)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new DurableSandboxPolicyException(
                    "a credential is listed under an empty environment variable name.",
                    policy);
            }

            var hosts = pair.Value;
            if (hosts is null || hosts.Count == 0)
            {
                throw new DurableSandboxPolicyException(
                    $"the credential '{pair.Key}' names no inject hosts, so it would be injected "
                    + "nowhere. Name at least one HTTPS host or host pattern.",
                    policy);
            }

            foreach (var host in hosts)
            {
                if (string.IsNullOrWhiteSpace(host))
                {
                    throw new DurableSandboxPolicyException(
                        $"the credential '{pair.Key}' names an empty inject host.",
                        policy);
                }

                if (host.Trim() == "*")
                {
                    throw new DurableSandboxPolicyException(
                        $"the credential '{pair.Key}' names a bare '*' inject host, which would "
                        + "send the value to every host. Name the hosts it may reach.",
                        policy);
                }
            }
        }
    }

    private static List<string> ExpandPaths(List<string>? paths, string? workingDirectory) =>
        paths is null
            ? []
            : paths.Select(path => DurableSandboxPolicy.ExpandPath(path, workingDirectory)).ToList();

    /// <summary>Builds the Copilot session configuration for a set of choices.</summary>
    internal SessionConfig BuildSessionConfig(AgentChoices? choices, DurableConversation? conversation) =>
        (SessionConfig)BuildSessionConfig(choices, conversation, new SessionConfig());

    /// <summary>
    /// Builds the config used to resume a forked session. It is the same config a created
    /// session gets, so the fork runs with the choices inherited at the fork boundary, plus
    /// the resume-only switches: the fork is a new session, so it must not replay a resume
    /// event, and it must not continue work the parent had pending.
    /// </summary>
    internal ResumeSessionConfig BuildResumeSessionConfig(AgentChoices? choices, DurableConversation? conversation)
    {
        var config = (ResumeSessionConfig)BuildSessionConfig(choices, conversation, new ResumeSessionConfig());
        config.SuppressResumeEvent = true;
        config.ContinuePendingWork = false;
        return config;
    }

    private SessionConfigBase BuildSessionConfig(
        AgentChoices? choices,
        DurableConversation? conversation,
        SessionConfigBase config)
    {
        var resolved = new ResolvedAgent(
            choices?.Model,
            choices?.ReasoningEffort,
            choices?.WorkingDirectory,
            choices?.Instructions,
            choices?.Streaming,
            choices?.Hooks,
            choices?.InfiniteSessions,
            ResolveExtensions(choices?.Extensions),
            ResolveTools(ResolveExtensions(choices?.Extensions), choices?.Tools),
            choices?.Permissions,
            choices?.Sandbox);

        // The default is a cheap, reasoning-capable model rather than "auto": "auto"
        // does not accept a reasoning effort, and it re-routes per request, which
        // makes an example's behaviour less reproducible.
        config.Model = resolved.Model ?? DefaultModel;

        // Fail-closed: a durable conversation may only use what it was explicitly granted.
        // The SDK's own default approves everything, which would make the agent's behaviour
        // depend on state that is not in the store. A host that supplies its own handler
        // takes over that decision for every conversation this runtime opens.
        config.OnPermissionRequest = Options.PermissionHandler ?? PermissionPolicy.Handler(resolved.Permissions);
        config.WorkingDirectory = resolved.WorkingDirectory;

        if (Options.EnableFileHooks is { } enableFileHooks)
        {
            config.EnableFileHooks = enableFileHooks;
        }

        // A null policy means "no sandbox": the session is created with no sandbox
        // configuration at all, exactly as every example before this one did.
        if (resolved.Sandbox is { } sandbox)
        {
            config.SandboxConfig = BuildSandboxConfig(sandbox, resolved.WorkingDirectory);
            // Trust is a create-time decision: a resumed session already has its working
            // directory, so the resume config has no such property to set. Nothing is lost by
            // leaving it alone — the trust in force is the one this same stored policy set when
            // the session was created.
            if (config is SessionConfig created && sandbox.TrustWorkingDirectory is { } trust)
            {
                created.TrustWorkingDirectory = trust
                    ? WorkingDirectoryTrust.User
                    : WorkingDirectoryTrust.Session;
            }
        }

        var sections = resolved.Extensions.SelectMany(extension => extension.Sections).ToList();

        // The "auto" model does not accept a reasoning effort, and neither do some concrete
        // models. The runtime rejects the session outright, so only send one when the
        // chosen model actually advertises support.
        if (resolved.ReasoningEffort is not null && SupportsReasoningEffort(resolved.Model))
        {
            config.ReasoningEffort = resolved.ReasoningEffort;
        }

        if (resolved.Streaming is { } streaming)
        {
            config.Streaming = streaming;
        }

        if (resolved.Hooks is not null)
        {
            config.Hooks = resolved.Hooks;
        }

        // Native compaction: the SDK summarizes the model context itself once it grows past
        // the configured thresholds, so the app never has to.
        if (resolved.InfiniteSessions is not null)
        {
            config.InfiniteSessions = resolved.InfiniteSessions;
        }

        // Prompt sections are rendered before every request, not once at session
        // creation: a section that reads a durable document must see the document's
        // value at the time of the request. The runtime injects the rendered
        // sections as additional context for the model.
        if (sections.Count > 0 && conversation is not null)
        {
            var userPromptSubmitted = config.Hooks?.OnUserPromptSubmitted;
            config.Hooks ??= new SessionHooks();
            config.Hooks.OnUserPromptSubmitted = async (input, invocation) =>
            {
                var output = userPromptSubmitted is null
                    ? null
                    : await userPromptSubmitted(input, invocation);

                var rendered = await RenderSectionsAsync(resolved, sections, conversation);
                if (string.IsNullOrWhiteSpace(rendered))
                {
                    return output;
                }

                output ??= new UserPromptSubmittedHookOutput();
                output.AdditionalContext = string.IsNullOrWhiteSpace(output.AdditionalContext)
                    ? rendered
                    : $"{output.AdditionalContext}\n\n{rendered}";
                return output;
            };
        }

        if (resolved.Tools.Count > 0)
        {
            config.Tools = resolved.Tools.Cast<AIFunctionDeclaration>().ToList();
        }

        var systemMessage = BuildSystemMessage(resolved, sections, conversation);
        if (!string.IsNullOrWhiteSpace(systemMessage))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemMessage,
            };
        }

        return config;
    }

    /// <summary>
    /// Renders the prompt sections for a conversation, the way they are rendered
    /// before each request. A section that reads a durable document sees the
    /// document's value at the time of the call.
    /// </summary>
    internal async Task<string> RenderSectionsAsync(DurableConversation conversation)
    {
        var resolved = conversation.Agent();
        var sections = resolved.Extensions.SelectMany(extension => extension.Sections).ToList();
        return await RenderSectionsAsync(resolved, sections, conversation);
    }

    /// <summary>Renders the prompt sections for a request.</summary>
    private static async Task<string> RenderSectionsAsync(
        ResolvedAgent agent,
        IReadOnlyList<PromptSection> sections,
        DurableConversation conversation)
    {
        var parts = new List<string>();
        foreach (var section in sections)
        {
            var input = new SectionInput(conversation, agent.Tools, agent.Extensions, agent.WorkingDirectory);
            var text = await section.Render(input);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            parts.Add(section.Tag ? $"<{section.Key}>\n{text}\n</{section.Key}>" : text);
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>Renders the system message from the selected extensions' sections.</summary>
    internal string BuildSystemMessage(
        ResolvedAgent agent,
        IReadOnlyList<PromptSection> sections,
        DurableConversation? conversation)
    {
        var parts = new List<string>();
        foreach (var section in sections)
        {
            var input = new SectionInput(conversation, agent.Tools, agent.Extensions, agent.WorkingDirectory);
            var text = section.Render(input).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            parts.Add(section.Tag ? $"<{section.Key}>\n{text}\n</{section.Key}>" : text);
        }

        if (!string.IsNullOrWhiteSpace(agent.Instructions))
        {
            parts.Add($"<instructions>\n{agent.Instructions}\n</instructions>");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Waits for the task runs in flight to finish, so a checkpoint that is being written
    /// completes before the store is closed.
    /// </summary>
    private async Task DrainTasksAsync()
    {
        while (true)
        {
            var pending = _inFlight.Values.ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending);
            }
            catch
            {
                // A run's failure is recorded in the task's outcome, not thrown at disposal.
            }
        }
    }

    /// <summary>
    /// Disposes what the runtime owns: at most once, and never a borrowed store or client.
    /// </summary>
    /// <remarks>
    /// The order matters. Subscriptions are removed first, so a session event that arrives
    /// during disposal cannot write into a store that is going away. Task runs in flight are
    /// then drained, so a checkpoint that is mid-commit finishes. Only then is the store's
    /// task-scheduler slot released and the owned resources disposed.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        CopilotClient? client;
        DurableStore? store;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            client = _client;
            store = _store;
            // Unbind, so IsInitialized reports false and the getters stop handing out
            // resources that are about to be released.
            _client = null;
            _store = null;
        }

        foreach (var conversation in _conversations.Values)
        {
            conversation.Detach();
        }

        _conversations.Clear();

        await DrainTasksAsync();

        if (store is not null)
        {
            store.ReleaseTaskScheduler(this);
        }

        if (OwnsClient && client is not null)
        {
            await client.DisposeAsync();
        }

        if (OwnsStore && store is not null)
        {
            store.Dispose();
        }
    }
}

/// <summary>The harness: the durable host that owns the store, the registry, and the conversations.</summary>
/// <remarks>
/// The harness is a front door onto <see cref="DurableRuntime"/>, not a second implementation of
/// it. It creates and owns its SDK client, borrows the store and the registry the caller
/// supplied, and delegates every durable operation to the runtime — which is the same runtime
/// <see cref="DurableCopilotClient"/> drives, so a conversation opened here and one opened
/// through the facade are the same object over the same store.
/// </remarks>
public sealed class DurableHarness : IAsyncDisposable
{
    /// <summary>
    /// The model used when a conversation does not name one. It is a cheap,
    /// reasoning-capable model: the examples are about durable mechanics, not model
    /// quality, and a fixed model makes an example's behaviour reproducible.
    /// </summary>
    public const string DefaultModel = DurableRuntime.DefaultModel;

    private readonly DurableRuntime _runtime;

    private DurableHarness(
        CopilotClient client,
        DurableStore store,
        ExtensionRegistry registry,
        DurableHarnessOptions? options = null)
    {
        _runtime = new DurableRuntime(registry, options);
        // The harness always created its client, so it always owns it. The store is the
        // caller's: the harness never disposes it.
        _runtime.Attach(client, store, ownsClient: true, ownsStore: false);
    }

    /// <summary>
    /// Binds a harness to a client the caller already started. The harness owns the client
    /// from here on; the store stays the caller's.
    /// </summary>
    internal static DurableHarness Attach(
        CopilotClient client,
        DurableStore store,
        ExtensionRegistry registry,
        DurableHarnessOptions? options = null) =>
        new(client, store, registry, options);

    /// <summary>The Copilot client.</summary>
    public CopilotClient Client => _runtime.Client;

    /// <summary>The durable store.</summary>
    public DurableStore Store => _runtime.Store;

    /// <summary>The extension registry.</summary>
    public ExtensionRegistry Registry => _runtime.Registry;

    /// <summary>The options this harness was opened with.</summary>
    public DurableHarnessOptions Options => _runtime.Options;

    /// <summary>The document holding per-conversation agent choices.</summary>
    public DocumentDefinition<AgentChoices> AgentDocument => _runtime.AgentDocument;

    /// <summary>The conversations this harness knows about.</summary>
    public IReadOnlyCollection<DurableConversation> Conversations => _runtime.Conversations;

    /// <summary>
    /// The model the runtime reported for the most recent session or turn this harness opened.
    /// </summary>
    /// <remarks>
    /// It is fed from the runtime's own reports — <c>session.start</c> and
    /// <c>session.model_change</c> for the session's model, <c>assistant.turn_start</c> and
    /// <c>assistant.usage</c> for each turn — never from the model the host requested, so a
    /// runtime that substitutes a model is visible here.
    /// <para>
    /// <see cref="OpenAsync(DurableStore, ExtensionRegistry, DurableHarnessOptions, CancellationToken)"/>
    /// only starts the client: no session exists yet, so the value is
    /// <see cref="DurableModelReport.NotReported"/> immediately after it returns and becomes
    /// populated once a conversation opens a session and the runtime reports. Read it again
    /// after <see cref="CreateConversationAsync"/> or <see cref="RootAsync"/> to see the
    /// session-level report, and after a turn completes to see the turn-level one.
    /// </para>
    /// <para>
    /// When the runtime reports no model — a deterministic runtime whose recording carries none,
    /// for instance — the value stays <see cref="DurableModelReport.NotReported"/> with
    /// <see cref="DurableModelReport.Model"/> <c>null</c>. There is deliberately no fallback to
    /// <see cref="DefaultModel"/>: "the runtime reported nothing" must not be indistinguishable
    /// from "the runtime reported the default".
    /// </para>
    /// </remarks>
    public DurableModelReport EffectiveModel => _runtime.EffectiveModel;

    /// <summary>
    /// Finds a conversation by id, restoring it from the store if the harness has not
    /// opened it yet. A conversation that was created before a restart is still there.
    /// </summary>
    public DurableConversation? FindConversation(string conversationId) =>
        _runtime.FindConversation(conversationId);

    /// <summary>Opens a harness over a store and a registry.</summary>
    public static Task<DurableHarness> OpenAsync(
        DurableStore store,
        ExtensionRegistry registry,
        CancellationToken cancellationToken = default) =>
        OpenAsync(store, registry, new DurableHarnessOptions(), cancellationToken);

    /// <summary>Opens a harness over a store and a registry with explicit options.</summary>
    public static async Task<DurableHarness> OpenAsync(
        DurableStore store,
        ExtensionRegistry registry,
        DurableHarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runtimePath = ResolveRuntimePath(options);
        var client = runtimePath is null
            ? new CopilotClient()
            : new CopilotClient(new CopilotClientOptions
            {
                Connection = RuntimeConnection.ForStdio(runtimePath, options.RuntimeArguments),
            });

        await client.StartAsync(cancellationToken);
        return new DurableHarness(client, store, registry, options);
    }

    /// <summary>
    /// The runtime executable the options select: the explicit path, else the environment
    /// variable, else <c>null</c> for the bundled runtime. A named path that does not exist is
    /// an error that names it, so the caller is never left guessing why the open failed.
    /// </summary>
    internal static string? ResolveRuntimePath(DurableHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var path = options.RuntimePath;
        var origin = $"DurableHarnessOptions.{nameof(DurableHarnessOptions.RuntimePath)}";
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable(DurableHarnessOptions.RuntimePathEnvironmentVariable);
            origin = $"the {DurableHarnessOptions.RuntimePathEnvironmentVariable} environment variable";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The runtime executable named by {origin} does not exist: '{path}'.",
                path);
        }

        return path;
    }

    /// <summary>Creates a conversation and its Copilot session.</summary>
    public Task<DurableConversation> CreateConversationAsync(
        AgentChoices? choices = null,
        string? ownerTaskId = null,
        string? ownerConversationId = null,
        CancellationToken cancellationToken = default) =>
        _runtime.CreateConversationAsync(choices, ownerTaskId, ownerConversationId, cancellationToken);

    /// <summary>Returns the root conversation, creating it on first use.</summary>
    public Task<DurableConversation> RootAsync(
        AgentChoices? choices = null,
        CancellationToken cancellationToken = default) =>
        _runtime.RootAsync(choices, cancellationToken);

    /// <summary>Rebinds a stored conversation to the native session it recorded.</summary>
    public Task<DurableConversation> ResumeConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default) =>
        _runtime.ResumeConversationAsync(conversationId, cancellationToken);

    /// <summary>Forks a conversation at an entry.</summary>
    /// <param name="parent">The conversation to fork from.</param>
    /// <param name="entryId">
    /// The parent's <b>durable entry id</b> — the value <see cref="Transaction.AppendEntry"/>
    /// returns, e.g. <c>e7</c> — never an SDK event id. The fork holds that entry and everything
    /// before it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The forked conversation, with its session attached.</returns>
    public Task<DurableConversation> ForkAsync(
        DurableConversation parent,
        string entryId,
        CancellationToken cancellationToken = default) =>
        _runtime.ForkAsync(parent, entryId, cancellationToken);

    /// <summary>Creates a durable task and schedules it.</summary>
    public Task<DurableTaskRecord> CreateTaskAsync(
        DurableTaskDefinition definition,
        JsonElement input,
        string? ownerConversationId = null,
        string? ownerTaskId = null,
        bool background = false,
        CancellationToken cancellationToken = default) =>
        _runtime.CreateTaskAsync(definition, input, ownerConversationId, ownerTaskId, background, cancellationToken);

    /// <summary>Runs a task to completion, resuming from its last checkpoint.</summary>
    public Task<TaskOutcome> RunTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        _runtime.RunTaskAsync(taskId, cancellationToken);

    /// <summary>Waits for a task to finish.</summary>
    public Task<TaskOutcome> WaitForTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        _runtime.WaitForTaskAsync(taskId, cancellationToken);

    /// <summary>Aborts a task, running its abort handler.</summary>
    public Task AbortTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        _runtime.AbortTaskAsync(taskId, cancellationToken);

    /// <summary>Resumes every task that was running when the process stopped.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome of each task that was re-entered, in store order.</returns>
    /// <remarks>
    /// The tasks are awaited rather than started and forgotten, so the returned task completes
    /// when the resumed work does. This is the same shape
    /// <see cref="DurableCopilotClient.ResumeTasksAsync"/> has: one operation, one way to await
    /// it, whichever entry point a host uses.
    /// </remarks>
    public Task<IReadOnlyList<TaskOutcome>> ResumeAsync(CancellationToken cancellationToken = default) =>
        _runtime.ResumeAsync(cancellationToken);

    /// <summary>Reads the task graph: tasks and their owner edges.</summary>
    public IReadOnlyList<(DurableTaskRecord Task, string? Owner)> TaskGraph() => _runtime.TaskGraph();

    /// <summary>Resolves extension names against the registry.</summary>
    public IReadOnlyList<Extension> ResolveExtensions(IReadOnlyList<string>? names) =>
        _runtime.ResolveExtensions(names);

    /// <summary>
    /// Resolves tool names against the selected extensions. When two selected extensions
    /// contribute a tool with the same name, the later one replaces the earlier one in
    /// place: the tool keeps its position in the list, but the later implementation wins.
    /// Wrappers are applied last, so a wrapper decorates whichever implementation won.
    /// </summary>
    public IReadOnlyList<AIFunction> ResolveTools(IReadOnlyList<Extension> extensions, IReadOnlyList<string>? names) =>
        _runtime.ResolveTools(extensions, names);

    /// <summary>
    /// Maps a durable sandbox policy onto the SDK's wire type, expanding the
    /// <see cref="DurableSandboxPolicy.WorkspaceToken"/> token against the working directory.
    /// </summary>
    public static SandboxConfig BuildSandboxConfig(DurableSandboxPolicy policy, string? workingDirectory) =>
        DurableRuntime.BuildSandboxConfig(policy, workingDirectory);

    /// <summary>Builds the Copilot session configuration for a set of choices.</summary>
    public SessionConfig BuildSessionConfig(AgentChoices? choices, DurableConversation? conversation) =>
        _runtime.BuildSessionConfig(choices, conversation);

    /// <summary>
    /// Renders the prompt sections for a conversation, the way they are rendered
    /// before each request. A section that reads a durable document sees the
    /// document's value at the time of the call.
    /// </summary>
    public Task<string> RenderSectionsAsync(DurableConversation conversation) =>
        _runtime.RenderSectionsAsync(conversation);

    /// <summary>Renders the system message from the selected extensions' sections.</summary>
    public string BuildSystemMessage(
        ResolvedAgent agent,
        IReadOnlyList<PromptSection> sections,
        DurableConversation? conversation) =>
        _runtime.BuildSystemMessage(agent, sections, conversation);

    /// <summary>Disposes the harness and the client it created.</summary>
    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
/// <summary>
/// Everything a <see cref="DurableCopilotClient"/> is built from: the store, the caller's
/// extension registry, the Copilot SDK client, and the ownership flags that decide what the
/// durable layer disposes.
/// </summary>
/// <remarks>
/// An options object is a description, not a resource. Constructing one touches nothing:
/// no file is opened, no process is started, and no connection is made. The stores and
/// clients it points at are created by the caller (or by
/// <see cref="DurableClientOptions.StoreOptions"/> / <see cref="DurableClientOptions.ClientOptions"/>)
/// before <see cref="DurableClientBuilder.Build"/> is called.
/// <para>
/// <b>Ownership.</b> The durable layer borrows what the caller supplies and owns what it
/// creates. Concretely:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="Store"/> set by the caller is borrowed: the durable layer never disposes it.
///     A store created from <see cref="StoreOptions"/> is owned, and
///     <see cref="DurableCopilotClient.DisposeAsync"/> disposes it exactly once.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="Client"/> set by the caller is borrowed: the durable layer never disposes it,
///     and the caller remains responsible for calling <c>DisposeAsync</c> on it. A client
///     created from <see cref="ClientOptions"/> is owned, and is disposed exactly once.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="OwnsStore"/> and <see cref="OwnsClient"/> override the default for a
///     resource the caller supplied. Set them to <c>true</c> to hand a supplied resource to
///     the durable layer, or <c>false</c> to keep a resource the durable layer would otherwise
///     own. <c>null</c> — the default — means "borrow what was supplied, own what was created".
///     </description>
///   </item>
/// </list>
/// <para>
/// The registry is never disposed: it holds this process's code, not a resource, and nothing
/// in it is persisted.
/// </para>
/// </remarks>
public sealed class DurableClientOptions
{
    /// <summary>
    /// The store the client reads and writes durable state through, or <c>null</c> when
    /// <see cref="StoreOptions"/> describes one instead.
    /// </summary>
    /// <remarks>
    /// A store supplied here is borrowed. Its lifetime — including the exclusive writer lock a
    /// persistent store holds — stays with the caller, so a host that opens one store and hands
    /// it to several clients disposes it once, after the last client is gone.
    /// </remarks>
    public DurableStore? Store { get; set; }

    /// <summary>
    /// How to create the store when <see cref="Store"/> is <c>null</c>, or <c>null</c> to
    /// require a supplied store.
    /// </summary>
    /// <remarks>
    /// A store created from these options is owned by the durable layer. The creation happens
    /// during <see cref="DurableCopilotClient.InitializeAsync"/>, not during
    /// <see cref="DurableClientBuilder.Build"/>: opening a persistent store is I/O, and
    /// <c>Build()</c> performs none.
    /// <para>
    /// The options follow the settled store contract — <see cref="DurableStore.Create"/> is the
    /// factory, an unspecified format means the persistent per-user path, and the ephemeral
    /// tier is asked for explicitly with <see cref="DurableStoreFormat.InMemory"/>.
    /// </para>
    /// </remarks>
    public DurableStoreOptions? StoreOptions { get; set; }

    /// <summary>
    /// The extension registry the client resolves tools, prompt sections, hooks, and task
    /// definitions from, or <c>null</c> to build with an empty registry.
    /// </summary>
    /// <remarks>
    /// The registry is this process's code, so it is never persisted and never disposed.
    /// Installing an extension after <see cref="DurableClientBuilder.Build"/> is allowed and
    /// takes effect the next time a session is configured or a section is rendered, exactly as
    /// it does for <see cref="DurableHarness"/>.
    /// </remarks>
    public ExtensionRegistry? Registry { get; set; }

    /// <summary>
    /// The Copilot SDK client the durable layer drives, or <c>null</c> when
    /// <see cref="ClientOptions"/> describes one instead.
    /// </summary>
    /// <remarks>
    /// A client supplied here is borrowed: the durable layer never disposes it, and it never
    /// starts it. The caller is responsible for the client's lifetime and for its own
    /// <c>StartAsync</c> / <c>DisposeAsync</c> calls.
    /// <para>
    /// Supplying a client is how a host shares one SDK client across the durable layer and its
    /// own code — a second client would mean a second process and a second runtime, so the
    /// durable layer never creates one when the caller has already supplied it.
    /// </para>
    /// </remarks>
    public CopilotClient? Client { get; set; }

    /// <summary>
    /// How to create the Copilot SDK client when <see cref="Client"/> is <c>null</c>, or
    /// <c>null</c> to use the SDK's defaults.
    /// </summary>
    /// <remarks>
    /// A client created from these options is owned by the durable layer, and is constructed
    /// and started during <see cref="DurableCopilotClient.InitializeAsync"/> — never during
    /// <see cref="DurableClientBuilder.Build"/>. Starting it is the first operation that leaves
    /// the process, which is why it is asynchronous and why <c>Build()</c> does not do it.
    /// </remarks>
    public CopilotClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// The harness options applied to every session the client configures.
    /// </summary>
    /// <remarks>
    /// These are host configuration, not durable state: the permission handler, the
    /// repository-hook switch, and the host-hook failure tolerance never travel with a
    /// conversation and never change what a stored choice means. They are the same options
    /// <see cref="DurableHarness.OpenAsync(DurableStore, ExtensionRegistry, DurableHarnessOptions, CancellationToken)"/>
    /// takes, because the client drives the same runtime the harness does.
    /// </remarks>
    public DurableHarnessOptions Harness { get; set; } = new();

    /// <summary>
    /// Whether the durable layer disposes <see cref="Store"/>, or <c>null</c> to apply the
    /// default policy.
    /// </summary>
    /// <remarks>
    /// The default policy is "borrow what the caller supplied, own what the durable layer
    /// created". Set this to <c>true</c> only when the caller obtained the store for this
    /// client alone and wants the client's disposal to release it — including the exclusive
    /// writer lock on a store file. Set it to <c>false</c> to keep the store open past the
    /// client's lifetime.
    /// <para>
    /// Whatever the value, the store is disposed at most once: the client's disposal is
    /// idempotent and a second call is a no-op.
    /// </para>
    /// </remarks>
    public bool? OwnsStore { get; set; }

    /// <summary>
    /// Whether the durable layer disposes <see cref="Client"/>, or <c>null</c> to apply the
    /// default policy.
    /// </summary>
    /// <remarks>
    /// The default policy is "borrow what the caller supplied, own what the durable layer
    /// created". A supplied client is typically shared with the host's own code, so the default
    /// is to leave it alone; set this to <c>true</c> when the client was created for this
    /// durable client alone.
    /// <para>
    /// Whatever the value, the client is disposed at most once: the client's disposal is
    /// idempotent and a second call is a no-op.
    /// </para>
    /// </remarks>
    public bool? OwnsClient { get; set; }
}

/// <summary>
/// Builds a <see cref="DurableCopilotClient"/>: the facade that turns the Copilot SDK client
/// into a durable host.
/// </summary>
/// <remarks>
/// The builder exists so a host can state the pieces it already has — its store, its SDK
/// client, its registry — and let the durable layer supply the rest. Every <c>Use…</c> method
/// replaces what the previous call set; the builder is a description, so ordering does not
/// matter beyond last-write-wins.
/// <para>
/// <b><see cref="Build"/> validates and constructs. It does not connect, and it performs no
/// persistence I/O.</b> It opens no store file, starts no SDK client, resumes no task, and
/// renders no prompt section. Everything that leaves the process happens in
/// <see cref="DurableCopilotClient.InitializeAsync"/>, which is asynchronous and safe when
/// several first operations race: the work happens once and every caller awaits the same
/// result.
/// </para>
/// <para>
/// The split matters because construction is cheap and fallible in only one way — a missing or
/// contradictory option — while initialization is slow and fallible in many. A host can build a
/// client during dependency-injection container construction without doing I/O there, and
/// decide on its own terms when to pay for startup.
/// </para>
/// </remarks>
public sealed class DurableClientBuilder
{
    /// <summary>Creates a builder with no pieces chosen yet.</summary>
    public DurableClientBuilder()
    {
    }

    /// <summary>The options describing the client that will be built.</summary>
    /// <remarks>
    /// Exposed so a host can inspect or adjust what the <c>Use…</c> methods set, and so a
    /// dependency-injection registration can configure the same object a manual build would.
    /// </remarks>
    public DurableClientOptions Options { get; } = new();

    /// <summary>Borrows a store the caller owns.</summary>
    /// <param name="store">The store to read and write durable state through.</param>
    /// <returns>This builder, so calls can be chained.</returns>
    /// <remarks>
    /// The store is borrowed unless <see cref="DurableClientOptions.OwnsStore"/> says otherwise.
    /// Most hosts open one store for the process, hand it to the client, and dispose it after
    /// the client — which is also the only safe order for a persistent store, whose exclusive
    /// writer lock is released on disposal.
    /// </remarks>
    public DurableClientBuilder UseStore(DurableStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Options.Store = store;
        Options.StoreOptions = null;
        return this;
    }

    /// <summary>Asks the durable layer to create and own a store.</summary>
    /// <param name="options">How to create the store.</param>
    /// <returns>This builder, so calls can be chained.</returns>
    /// <remarks>
    /// The store is created during <see cref="DurableCopilotClient.InitializeAsync"/>, not here,
    /// because opening a persistent store takes its writer lock and reads the file.
    /// </remarks>
    public DurableClientBuilder UseStore(DurableStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options.Store = null;
        Options.StoreOptions = options;
        return this;
    }

    /// <summary>Borrows a Copilot SDK client the caller owns.</summary>
    /// <param name="client">The client the durable layer drives.</param>
    /// <returns>This builder, so calls can be chained.</returns>
    /// <remarks>
    /// The client is borrowed unless <see cref="DurableClientOptions.OwnsClient"/> says
    /// otherwise, and the durable layer does not start it: a host that supplies a client has
    /// already started it, or will start it before the first operation that needs it.
    /// </remarks>
    public DurableClientBuilder UseClient(CopilotClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Options.Client = client;
        Options.ClientOptions = null;
        return this;
    }

    /// <summary>Asks the durable layer to create, own, and start an SDK client.</summary>
    /// <param name="options">How to create the client.</param>
    /// <returns>This builder, so calls can be chained.</returns>
    /// <remarks>
    /// The client is constructed and started during
    /// <see cref="DurableCopilotClient.InitializeAsync"/>. Passing options with a null
    /// <paramref name="options"/> is allowed and means "create one with the SDK's defaults".
    /// </remarks>
    public DurableClientBuilder UseClient(CopilotClientOptions? options)
    {
        Options.Client = null;
        Options.ClientOptions = options ?? new CopilotClientOptions();
        return this;
    }

    /// <summary>Sets the extension registry the client resolves code from.</summary>
    /// <param name="registry">The registry holding this process's extensions.</param>
    /// <returns>This builder, so calls can be chained.</returns>
    /// <remarks>
    /// The registry is held by reference and never disposed, so extensions installed after the
    /// build are visible to the next configuration the client performs.
    /// </remarks>
    public DurableClientBuilder UseRegistry(ExtensionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Options.Registry = registry;
        return this;
    }

    /// <summary>Sets the harness options applied to every session the client configures.</summary>
    /// <param name="options">The host configuration for the sessions.</param>
    /// <returns>This builder, so calls can be chained.</returns>
    public DurableClientBuilder UseHarnessOptions(DurableHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options.Harness = options;
        return this;
    }

    /// <summary>Adjusts the options in place.</summary>
    /// <param name="configure">The adjustments to apply.</param>
    /// <returns>This builder, so calls can be chained.</returns>
    /// <remarks>
    /// The escape hatch for the options no <c>Use…</c> method names — the ownership flags in
    /// particular. It runs immediately, not at build time.
    /// </remarks>
    public DurableClientBuilder Configure(Action<DurableClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
        return this;
    }

    /// <summary>Validates the options and constructs the client.</summary>
    /// <returns>A client that has not been initialized yet.</returns>
    /// <exception cref="InvalidOperationException">
    /// The options cannot describe a client — for example when neither
    /// <see cref="DurableClientOptions.Store"/> nor <see cref="DurableClientOptions.StoreOptions"/>
    /// is set, or when both a supplied store and an ownership flag that contradicts it are
    /// present.
    /// </exception>
    /// <remarks>
    /// <b>Validate and construct only.</b> No connection is made, no store file is opened, no
    /// task is resumed, and no persistence I/O of any kind happens here. The returned client is
    /// inert until <see cref="DurableCopilotClient.InitializeAsync"/> runs, which is what makes
    /// it safe to build during dependency-injection container construction or at the top of a
    /// program that later decides not to do any work.
    /// <para>
    /// The return type is the facade, not the SDK's <c>CopilotClient</c>: what the caller gets
    /// back is the durable host, and the SDK client is one of the things it drives. Use
    /// <see cref="DurableCopilotClient.NativeClient"/> to reach the SDK client itself, on the
    /// understanding described there.
    /// </para>
    /// </remarks>
    public DurableCopilotClient Build()
    {
        var options = Options;

        // Validate and construct only. Nothing here opens a store, starts a client, or
        // touches the file system: everything that leaves the process happens in
        // InitializeAsync.
        if (options.Store is null && options.StoreOptions is null)
        {
            throw new InvalidOperationException(
                "A durable client needs a store: call UseStore(DurableStore) to borrow one, or " +
                "UseStore(DurableStoreOptions) to have the durable layer create and own one.");
        }

        if (options.Store is not null && options.StoreOptions is not null)
        {
            throw new InvalidOperationException(
                "A durable client takes one store source: Store and StoreOptions are both set. " +
                "UseStore replaces the previous choice, so this means the options were adjusted " +
                "directly; clear one of them.");
        }

        if (options.Client is not null && options.ClientOptions is not null)
        {
            throw new InvalidOperationException(
                "A durable client takes one SDK client source: Client and ClientOptions are both " +
                "set. UseClient replaces the previous choice, so this means the options were " +
                "adjusted directly; clear one of them.");
        }

        if (options.OwnsStore is true && options.Store is null)
        {
            throw new InvalidOperationException(
                "OwnsStore is set but no store was supplied. A store the durable layer creates is " +
                "always owned; the flag only decides the fate of a store the caller supplied.");
        }

        if (options.OwnsClient is true && options.Client is null)
        {
            throw new InvalidOperationException(
                "OwnsClient is set but no SDK client was supplied. A client the durable layer " +
                "creates is always owned; the flag only decides the fate of a client the caller " +
                "supplied.");
        }

        if (options.Harness is null)
        {
            throw new InvalidOperationException("Harness options cannot be null; use the default.");
        }

        // Finding 1: the registry is resolved once, here, and the same instance is what
        // Options.Registry and Registry both report. A getter that allocated a fresh empty
        // registry per read would hand out a different object every time, so an extension
        // installed through one read would be invisible to the next.
        options.Registry ??= new ExtensionRegistry();

        return new DurableCopilotClient(options);
    }
}

/// <summary>
/// The durable host: a Copilot SDK client bound to a store, a registry, and conversation
/// handles that survive a restart.
/// </summary>
/// <remarks>
/// A <see cref="DurableCopilotClient"/> is what <see cref="DurableClientBuilder.Build"/>
/// returns, and it is the supported entry point for an application that wants durable
/// conversations without assembling the pieces itself. It drives the same internal runtime
/// <see cref="DurableHarness"/> drives — the same conversation binding, the same configuration
/// building, the same task coordination, and the same lifecycle — so a conversation created
/// through the facade is indistinguishable from one created through the harness, and the two
/// can be used together over one store. Sharing configuration helpers alone would not be
/// enough: the runtime, not the helpers, is what holds the bindings.
/// <para>
/// <b>Initialization.</b> A built client is inert. The first operation that needs the store or
/// the SDK client — an explicit <see cref="InitializeAsync"/> or any conversation operation —
/// initializes the client, and concurrent first operations share one initialization: the work
/// happens once and every caller awaits the same result. Initialization is asynchronous because
/// it may open a store, start an SDK client process, and re-enter running tasks, and because
/// waiting synchronously for any of those would risk a deadlock in a host with a
/// synchronization context.
/// </para>
/// <para>
/// <b>Disposal.</b> Exactly once. The state is idempotent: a second
/// <see cref="DisposeAsync"/> is a no-op. Borrowed stores and clients are left alone; created
/// ones are disposed. See <see cref="DurableClientOptions"/> for the policy and its overrides.
/// </para>
/// <para>
/// <b>Native SDK access.</b> <see cref="NativeClient"/> is an explicit escape hatch. Calls made
/// through it — creating a session, resuming one, forking one, or any other SDK operation the
/// facade does not expose — are the caller's own, and they sit outside the facade's durability
/// guarantees. A session the caller creates that way is not a
/// <see cref="DurableConversation"/>: its choices are not stored, its documents are not bound,
/// its grants are not enforced by the durable permission policy, and it does not appear in
/// <see cref="FindConversation(Func{DurableConversation, bool})"/>. Reach for it to configure
/// the SDK, not to route around the facade.
/// </para>
/// </remarks>
public sealed class DurableCopilotClient : IAsyncDisposable
{
    private readonly DurableClientOptions _options;
    private readonly DurableRuntime _runtime;
    private readonly object _initGate = new();

    private Task? _initialization;
    private bool _disposed;

    internal DurableCopilotClient(DurableClientOptions options)
    {
        _options = options;
        // Finding 1: the registry is resolved once, by Build(), and this is that instance.
        // Options.Registry and Registry therefore report the same object, so an extension
        // installed through one is visible through the other.
        _runtime = new DurableRuntime(options.Registry!, options.Harness);
    }

    /// <summary>The options this client was built from.</summary>
    public DurableClientOptions Options => _options;

    /// <summary>The store the client reads and writes durable state through.</summary>
    /// <remarks>
    /// Available as soon as the client is initialized. A store described by
    /// <see cref="DurableClientOptions.StoreOptions"/> is created during initialization, so
    /// reading this before then is an error.
    /// </remarks>
    public DurableStore Store => _runtime.Store;

    /// <summary>
    /// The extension registry the client resolves code from, or an empty registry when none was
    /// supplied.
    /// </summary>
    public ExtensionRegistry Registry => _runtime.Registry;

    /// <summary>
    /// The Copilot SDK client the durable layer drives.
    /// </summary>
    /// <remarks>
    /// This is the escape hatch: everything reachable from here is the SDK's own surface, and
    /// operations performed through it are outside the facade's durability guarantees. Use it to
    /// configure the SDK client — models, telemetry, logging — not to create or fork sessions;
    /// a session created that way has none of the durable layer's state, choices, or grants.
    /// <para>
    /// A supplied client is returned as it was supplied; a created one exists once the client is
    /// initialized. The durable layer never disposes a borrowed client, and never starts one it
    /// was given.
    /// </para>
    /// </remarks>
    public CopilotClient NativeClient => _runtime.Client;

    /// <summary>Whether initialization has completed and the client is ready for work.</summary>
    public bool IsInitialized => _runtime.IsAttached;

    /// <summary>Initializes the client, at most once, safely across concurrent callers.</summary>
    /// <param name="cancellationToken">Cancels the initialization attempt.</param>
    /// <returns>A task that completes when the client is ready.</returns>
    /// <remarks>
    /// The first operation that needs the store or the SDK client performs this, so calling it
    /// is optional — but a host that wants startup cost and failures at a known point calls it
    /// itself. Concurrent calls, and concurrent first operations, share one initialization: the
    /// work happens once and every caller awaits the same result. A failed initialization is
    /// not retried implicitly; the caller decides whether to try again.
    /// <para>
    /// Because the attempt is shared, the first caller's cancellation token governs it: a
    /// caller that arrives while initialization is running joins the attempt already in
    /// progress rather than starting a second one.
    /// </para>
    /// </remarks>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_initGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _initialization ??= InitializeCoreAsync(cancellationToken);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // Everything that leaves the process happens here, never in Build().
        var ownsStore = _options.Store is null;
        var ownsClient = _options.Client is null;

        DurableStore? createdStore = null;
        CopilotClient? createdClient = null;

        try
        {
            var store = _options.Store;
            if (store is null)
            {
                createdStore = DurableStore.Create(_options.StoreOptions!);
                store = createdStore;
            }

            var client = _options.Client;
            if (client is null)
            {
                createdClient = new CopilotClient(_options.ClientOptions ?? new CopilotClientOptions());
                await createdClient.StartAsync(cancellationToken);
                client = createdClient;
            }

            // A supplied resource is borrowed unless the caller handed it over explicitly; a
            // created one is always owned.
            _runtime.Attach(
                client,
                store,
                ownsClient: ownsClient || _options.OwnsClient is true,
                ownsStore: ownsStore || _options.OwnsStore is true);
        }
        catch
        {
            // A half-built client owns nothing: release what this attempt created, in the
            // reverse order, and let the failure surface to every caller that joined it.
            if (createdClient is not null)
            {
                await createdClient.DisposeAsync();
            }

            if (createdStore is not null)
            {
                createdStore.Dispose();
            }

            throw;
        }
    }

    /// <summary>Creates a conversation and its Copilot session.</summary>
    /// <param name="choices">
    /// The conversation's stored choices, or <c>null</c> for the defaults. The choices are
    /// written to the store before the session is created, so a crash between the two leaves a
    /// conversation whose choices are already durable.
    /// </param>
    /// <param name="ownerTaskId">The task that owns the conversation, if any.</param>
    /// <param name="ownerConversationId">The conversation that spawned it, if any.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The durable conversation handle.</returns>
    /// <remarks>
    /// The handle is a <see cref="DurableConversation"/> — the same type the harness returns,
    /// not a second durable-session abstraction. Its documents, entries, answers, and forks are
    /// the store's, and its session configuration is built by the shared runtime.
    /// </remarks>
    public async Task<DurableConversation> CreateConversationAsync(
        AgentChoices? choices = null,
        string? ownerTaskId = null,
        string? ownerConversationId = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.CreateConversationAsync(choices, ownerTaskId, ownerConversationId, cancellationToken);
    }

    /// <summary>Returns a conversation, creating it on first use.</summary>
    /// <param name="choices">The choices to create it with, when it has to be created.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The store's root conversation.</returns>
    /// <remarks>
    /// A store reopened from disk already holds its conversations, and they are restored before
    /// a new one is created, so the root is the conversation that was there before a restart
    /// rather than a second one.
    /// </remarks>
    public async Task<DurableConversation> RootAsync(
        AgentChoices? choices = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.RootAsync(choices, cancellationToken);
    }

    /// <summary>Rebinds a stored conversation to a live session.</summary>
    /// <param name="conversationId">The stored conversation's identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The conversation handle, with its session attached.</returns>
    /// <remarks>
    /// Resuming reads the conversation's stored choices and builds the session from them — the
    /// precedence rule in <see cref="GetSessionConfig(AgentChoices?, DurableConversation?)"/>
    /// applies — so a conversation resumed after a restart runs with the authority and the
    /// sandbox policy it had when it was last configured.
    /// </remarks>
    public async Task<DurableConversation> ResumeConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.ResumeConversationAsync(conversationId, cancellationToken);
    }

    /// <summary>Forks a conversation at one of its entries.</summary>
    /// <param name="parent">The conversation to fork from.</param>
    /// <param name="entryId">
    /// The parent's <b>durable entry id</b> — the value <see cref="Transaction.AppendEntry"/>
    /// returns, e.g. <c>e7</c> — never an SDK event id. The fork holds that entry and everything
    /// before it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The new conversation handle.</returns>
    /// <remarks>
    /// <para>
    /// The fork copies the parent's transcript up to the entry and each document's value under
    /// its own <see cref="ForkPolicy"/>, and inherits the parent's choices as a snapshot — so a
    /// fork can never silently widen the parent's grants. The isolation is in the store: the two
    /// conversations hold independent copies, and a commit in one is not visible in the other.
    /// </para>
    /// <para>
    /// The fork's durable half is committed before the native fork is attempted, so an
    /// interrupted fork is completed or abandoned the next time the store is opened.
    /// </para>
    /// </remarks>
    public async Task<DurableConversation> ForkAsync(
        DurableConversation parent,
        string entryId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.ForkAsync(parent, entryId, cancellationToken);
    }

    /// <summary>Finds a live conversation handle by its identifier.</summary>
    /// <param name="conversationId">The conversation's identifier.</param>
    /// <returns>The handle, or <c>null</c> when it is not bound in this client.</returns>
    /// <remarks>
    /// Only conversations this client created or resumed are bound. A conversation in the store
    /// that no session is attached to is not returned; resume it first.
    /// </remarks>
    public DurableConversation? FindConversation(string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        // Deliberately not the runtime's store-backed lookup: this answers "is this client
        // holding that conversation", and restoring one here would create a session as a side
        // effect of a read.
        return _runtime.Conversations.FirstOrDefault(c => c.Id == conversationId);
    }

    /// <summary>Finds a live conversation handle by predicate.</summary>
    /// <param name="match">The predicate a handle must satisfy.</param>
    /// <returns>The first matching handle, or <c>null</c> when none matches.</returns>
    public DurableConversation? FindConversation(Func<DurableConversation, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return _runtime.Conversations.FirstOrDefault(match);
    }

    /// <summary>
    /// Builds the Copilot session configuration for a set of stored choices, applying the
    /// persisted-agent-choice precedence rule.
    /// </summary>
    /// <param name="choices">
    /// The choices to build from, or <c>null</c> to use the client's defaults.
    /// </param>
    /// <param name="conversation">
    /// The conversation the session belongs to, or <c>null</c> when the configuration is being
    /// inspected outside a conversation. Sections that read durable documents need the
    /// conversation; a configuration built without one has no document-bound sections.
    /// </param>
    /// <returns>The session configuration the runtime would use.</returns>
    /// <remarks>
    /// <b>Precedence.</b> The persisted agent choices are authoritative. They are the record of
    /// what this conversation is: its model, reasoning effort, working directory, streaming
    /// flag, extension and tool selection, permission grants, sandbox policy, and instructions.
    /// Where a stored choice is set, it wins over anything the SDK configuration says about the
    /// same thing. Where a stored choice is unset, the SDK configuration's value — or the
    /// runtime's default when the configuration is silent too — applies.
    /// <para>
    /// The rule exists because the store is what makes a conversation reproducible: a grant that
    /// lives only in host configuration is not part of the durable record, so it would change
    /// across a restart and travel badly across a fork. Reading the configuration back through
    /// this overload is how a host sees the effective result — the stored choices already
    /// resolved against the registry's code and the runtime's defaults — rather than having to
    /// reconstruct the merge itself.
    /// </para>
    /// <para>
    /// Host-level options are not part of this merge and cannot be overridden by a stored
    /// choice: <see cref="DurableHarnessOptions.PermissionHandler"/>,
    /// <see cref="DurableHarnessOptions.EnableFileHooks"/>, and
    /// <see cref="DurableHarnessOptions.TolerateHostHookFailures"/> describe the process, not
    /// the conversation. Supplying a permission handler replaces the fail-closed durable policy
    /// for every conversation, which moves the agent's authority out of the store and out of
    /// these guarantees — see <see cref="DurableHarnessOptions.PermissionHandler"/>.
    /// </para>
    /// </remarks>
    public SessionConfig GetSessionConfig(
        AgentChoices? choices = null,
        DurableConversation? conversation = null)
    {
        // The stored choices are authoritative, so a conversation's own choices win over
        // whatever the caller passed; the argument only supplies defaults for a conversation
        // that has none stored.
        var effective = conversation?.Choices() ?? choices;
        return _runtime.BuildSessionConfig(effective, conversation);
    }

    /// <summary>
    /// Builds the session configuration a stored conversation would be resumed with, without
    /// creating a session.
    /// </summary>
    /// <param name="conversationId">The stored conversation's identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The session configuration the runtime would use.</returns>
    /// <remarks>
    /// The asynchronous counterpart of <see cref="GetSessionConfig(AgentChoices?, DurableConversation?)"/>
    /// for a conversation that is not bound to a live handle. The same precedence rule applies:
    /// the stored choices are authoritative and the SDK configuration only fills what they leave
    /// unset. This performs store reads but creates no session and no sandbox.
    /// </remarks>
    public async Task<SessionConfig> GetSessionConfigAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var stored = _runtime.Store.Document(_runtime.AgentDocument.Kind, conversationId)?.Latest;
        var choices = stored is { } latest
            ? latest.Deserialize<AgentChoices>(DurableJson.Options)
            : null;

        // No session is created and no sandbox is started: the configuration is built the way
        // the runtime would build it, from the store's choices alone.
        return _runtime.BuildSessionConfig(choices, null);
    }

    /// <summary>Re-enters the tasks the store records as running.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome of each task that was re-entered, in store order.</returns>
    /// <remarks>
    /// A task is resumed from its last checkpoint, and its idempotency memo decides which of its
    /// effects are replayed and which are skipped. This is the operation a host calls right after
    /// startup, once the client is initialized, so work interrupted by the previous process
    /// finishes.
    /// </remarks>
    public async Task<IReadOnlyList<TaskOutcome>> ResumeTasksAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _runtime.ResumeAsync(cancellationToken);
    }

    /// <summary>Waits for a durable task to finish.</summary>
    /// <param name="taskId">The task's identifier.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The task's outcome.</returns>
    public async Task<TaskOutcome> WaitForTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        await InitializeAsync(cancellationToken);
        return await _runtime.WaitForTaskAsync(taskId, cancellationToken);
    }

    /// <summary>
    /// Disposes the resources the durable layer owns: at most once, and never a borrowed store
    /// or client.
    /// </summary>
    /// <returns>A task that completes when disposal has finished.</returns>
    /// <remarks>
    /// Idempotent: a second call is a no-op, so a <c>using</c> statement, an explicit call, and a
    /// container's disposal can all run without double-disposing anything. A created store is
    /// disposed here, which releases its exclusive writer lock; a created SDK client is disposed
    /// here too. Borrowed ones are left for their owner, and the order in which a host disposes a
    /// borrowed store against this client is the host's choice — but a persistent store must be
    /// disposed before another process opens the same path.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        Task? initialization;
        lock (_initGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            initialization = _initialization;
        }

        // An initialization that is still running has to finish before the runtime can be
        // disposed, or disposal would race the attach. A failed one is not an error here: the
        // runtime owns nothing, so there is nothing to release.
        if (initialization is not null)
        {
            try
            {
                await initialization;
            }
            catch
            {
                return;
            }
        }

        await _runtime.DisposeAsync();
    }
}

/// <summary>
/// The undo log of a transaction in flight. A transaction writes straight into the
/// store's collections, so a throw has to undo what it wrote. The log records the
/// inverse of every mutation, and replays it in reverse when the transaction fails.
/// </summary>
internal sealed class UndoLog
{
    private readonly List<Action> _actions = [];

    /// <summary>Records that a key was added to a dictionary, so it can be removed again.</summary>
    public void Remove<TKey, TValue>(Dictionary<TKey, TValue> map, TKey key) where TKey : notnull =>
        _actions.Add(() => map.Remove(key));

    /// <summary>Records that an item was appended to a list, so it can be removed again.</summary>
    public void Append<T>(List<T> list) =>
        _actions.Add(() => list.RemoveAt(list.Count - 1));

    /// <summary>Records a restore action, such as resetting the write sequence.</summary>
    public void Sequence(Action restore) =>
        _actions.Add(restore);

    /// <summary>Undoes every recorded mutation, most recent first.</summary>
    public void Undo(IDurableStoreProvider provider)
    {
        for (var i = _actions.Count - 1; i >= 0; i--)
        {
            _actions[i]();
        }

        _actions.Clear();
    }

    /// <summary>
    /// Records the task's own state, so a failed phase leaves the checkpoint where
    /// it was and the task can be resumed.
    /// </summary>
    public void Restore(TaskStateSnapshot snapshot, DurableTaskRecord task) =>
        _actions.Add(() => snapshot.ApplyTo(task));
}

/// <summary>Convenience helpers for the durable layer.</summary>
public static class DurableExtensions
{
    /// <summary>Reads the assistant's text from an event.</summary>
    public static string Text(this AssistantMessageEvent message) => message.Data.Content;

    /// <summary>Reads the user's text from an event.</summary>
    public static string Text(this UserMessageEvent message) => message.Data.Content;

    /// <summary>Reads the tool name from a tool execution event.</summary>
    public static string ToolName(this ToolExecutionStartEvent tool) => tool.Data.ToolName;
}

/// <summary>
/// Assertions for the examples. Every example checks the durable invariant it
/// demonstrates, so a broken guarantee fails the run instead of printing a
/// plausible-looking value.
/// </summary>
public static class Check
{
    /// <summary>Asserts that a condition holds.</summary>
    public static void That(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {because}");
        }
    }

    /// <summary>Asserts that two values are equal.</summary>
    public static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Assertion failed: {because}\n  expected: {Format(expected)}\n  actual:   {Format(actual)}");
        }
    }

    /// <summary>Asserts that two sequences hold the same values in the same order.</summary>
    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string because)
    {
        var expectedList = expected.ToList();
        var actualList = actual.ToList();
        if (!expectedList.SequenceEqual(actualList))
        {
            throw new InvalidOperationException(
                $"Assertion failed: {because}\n  expected: [{string.Join(", ", expectedList.Select(Format))}]\n  actual:   [{string.Join(", ", actualList.Select(Format))}]");
        }
    }

    /// <summary>Asserts that a value is not null, and returns it.</summary>
    public static T NotNull<T>(T? value, string because) where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException($"Assertion failed: {because}\n  expected: not null\n  actual:   null");
        }

        return value;
    }

    /// <summary>Asserts that a string contains a substring.</summary>
    public static void Contains(string haystack, string needle, string because)
    {
        if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Assertion failed: {because}\n  expected to contain: {needle}\n  actual:              {haystack}");
        }
    }

    private static string Format<T>(T value) => value switch
    {
        null => "(null)",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "(null)",
    };
}

#pragma warning restore IL2026, IL3050
