using System.Text.Json;
using Durable.EfCore.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Durable.EfCore;

/// <summary>
/// The EF Core durable store provider: the store's state, written through the
/// provider-neutral <see cref="DurableDbContext"/> model.
/// </summary>
/// <remarks>
/// The provider keeps the store's state in memory exactly as the in-memory and JSONL
/// providers do — that is the shape of <see cref="InMemoryStoreProvider"/>, which owns
/// the undo log every provider relies on — and writes the committed state through EF
/// Core. It stores:
/// <list type="bullet">
/// <item><c>sessions</c>: one row per conversation, including fork lineage and ownership.</item>
/// <item><c>turns</c>: one row per transcript entry, keyed by the entry id the store minted.</item>
/// <item><c>events</c>: the append-only write log the provider replays on open —
/// <c>document</c>, <c>document-version</c>, <c>task</c>, <c>entry-append</c>,
/// <c>entry-list</c> and <c>sequence</c>.</item>
/// </list>
/// <para>
/// The <c>turns</c> table is the durable form of one entry and is what a reader queries
/// directly; the <c>events</c> table is the provider's own log, and it is what reconstructs
/// the ordering of a conversation's entry list — which <c>turns</c> alone cannot express,
/// because a fork inherits entry ids that belong to its parent's session.
/// </para>
/// <para>
/// Nothing in the model is provider-specific, so the same provider works against any
/// database the context is configured for. The default configuration is SQLite over the
/// store path, because SQLite needs no server; the per-database wiring is #2.3/#2.4 and
/// is reachable through the constructor that takes a context options factory.
/// </para>
/// </remarks>
public sealed class EfCoreStoreProvider : InMemoryStoreProvider
{
    /// <summary>The name the provider is registered under by <see cref="Register"/>.</summary>
    public const string RegisteredName = "efcore";

    private const int EnvelopeVersion = 1;
    private const string DocumentType = "document";
    private const string DocumentVersionType = "document-version";
    private const string TaskType = "task";
    private const string EntryAppendType = "entry-append";
    private const string EntryListType = "entry-list";
    private const string SequenceType = "sequence";

    private readonly Func<DurableStoreOptions, DbContextOptions<DurableDbContext>>? _contextOptionsFactory;

    private DurableDbContext? _context;
    private StoreFileLock? _lock;
    private long _envelopeSequence;
    private readonly HashSet<string> _persistedTurnIds = [];

    /// <summary>Creates a provider that persists to the SQLite database at the store path.</summary>
    public EfCoreStoreProvider()
    {
    }

    /// <summary>
    /// Creates a provider over a context configured by the factory. Use this overload to
    /// run the store on a database other than SQLite, or over a connection that is not a
    /// file path at all.
    /// </summary>
    /// <param name="contextOptionsFactory">
    /// Builds the context options for the store. The provider still takes its exclusive
    /// lock on <see cref="DurableStoreOptions.Path"/> when that is set.
    /// </param>
    public EfCoreStoreProvider(Func<DurableStoreOptions, DbContextOptions<DurableDbContext>> contextOptionsFactory)
    {
        ArgumentNullException.ThrowIfNull(contextOptionsFactory);
        _contextOptionsFactory = contextOptionsFactory;
    }

    /// <summary>
    /// Registers the provider with <see cref="DurableStore.RegisterProvider"/> under
    /// <see cref="RegisteredName"/>, so a store can select it with
    /// <c>ProviderName = EfCoreStoreProvider.RegisteredName</c>.
    /// </summary>
    public static void Register() =>
        DurableStore.RegisterProvider(RegisteredName, _ => new EfCoreStoreProvider());

    /// <summary>The provider persists, so the store reports the durable tier.</summary>
    public override bool IsPersistent => true;

    /// <summary>
    /// The connection-string convention for the SQLite provider: the store path is the
    /// database file, and the connection is <c>Data Source=&lt;absolute path&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The path is resolved with <see cref="Path.GetFullPath(string)"/>, so a relative
    /// store path is anchored at the process working directory rather than at whatever
    /// directory the process happens to be in when the connection opens. The string is
    /// built with <see cref="SqliteConnectionStringBuilder"/>, so a path containing
    /// spaces, <c>;</c>, <c>=</c> or quotes is escaped instead of splitting the
    /// connection string. The default <see cref="SqliteOpenMode.ReadWriteCreate"/> mode
    /// creates the database file on first open.
    /// </remarks>
    /// <param name="path">The store path, absolute or relative to the working directory.</param>
    /// <returns>The SQLite connection string for that store path.</returns>
    public static string ConnectionStringFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
        }.ToString();
    }

    /// <summary>Builds the SQLite context options for a store path.</summary>
    /// <param name="path">The store path, absolute or relative to the working directory.</param>
    /// <returns>The options the provider opens the database with.</returns>
    public static DbContextOptions<DurableDbContext> ContextOptionsFor(string path) =>
        new DbContextOptionsBuilder<DurableDbContext>()
            .UseSqlite(ConnectionStringFor(path))
            .Options;

    /// <summary>
    /// Opens the database at the store path, creates or migrates the schema, and replays
    /// the persisted state into memory.
    /// </summary>
    /// <remarks>
    /// The store path is held exclusively for the provider's lifetime, exactly as the
    /// JSONL provider holds it: two processes writing one store would interleave their
    /// write logs. The default connection is SQLite over that path.
    /// </remarks>
    public override void Open(DurableStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var path = options.Path;
        if (_contextOptionsFactory is null && path is null)
        {
            throw new InvalidOperationException(
                "The EF Core store provider needs a store path: pass DurableStoreOptions.Path, " +
                "or open the provider over a context options factory.");
        }

        if (path is not null)
        {
            _lock = StoreFileLock.Acquire(path);
        }

        var contextOptions = _contextOptionsFactory is not null
            ? _contextOptionsFactory(options)
            : ContextOptionsFor(path!);

        _context = new DurableDbContext(contextOptions);
        _context.Database.Migrate();
        Replay();
    }

    /// <summary>Releases the database context and the exclusive lock on the store path.</summary>
    public override void Dispose()
    {
        _context?.Dispose();
        _context = null;
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>
    /// Writes the state this commit changed, as one database transaction. A write that
    /// fails leaves the provider exactly as it was before the commit: the database
    /// transaction rolls back, the change tracker is cleared so the failed entities are
    /// not re-attempted, and the provider's own bookkeeping is restored.
    /// </summary>
    protected override void Persist()
    {
        if (_context is null)
        {
            return;
        }

        // Capturing the writes has side effects — it claims the turn ids it builds rows
        // for and numbers the event envelopes — so both are restored if the write fails.
        var persistedTurnIds = _persistedTurnIds.ToArray();
        var envelopeSequence = _envelopeSequence;

        try
        {
            WritePendingWrites();
        }
        catch
        {
            // The failed entities are still tracked as Added, so every later SaveChanges()
            // would re-attempt the same failing insert. Clearing the tracker drops them.
            _context.ChangeTracker.Clear();
            _persistedTurnIds.Clear();
            _persistedTurnIds.UnionWith(persistedTurnIds);
            _envelopeSequence = envelopeSequence;
            throw;
        }
    }

    /// <summary>Writes the pending state as one database transaction.</summary>
    private void WritePendingWrites()
    {
        var pending = TakePendingWrites();

        using var transaction = _context!.Database.BeginTransaction();

        // Sessions first: the turn and event rows carry a foreign key to the session
        // they belong to, so the row has to exist before the records that cite it.
        foreach (var conversation in pending.Sessions)
        {
            var entity = _context.Sessions.Find(conversation.Id);
            if (entity is null)
            {
                _context.Sessions.Add(new SessionEntity
                {
                    Id = conversation.Id,
                    ParentSessionId = conversation.ParentConversationId,
                    ForkEntryId = conversation.ForkEntryId,
                    OwnerTaskId = conversation.OwnerTaskId,
                    OwnerSessionId = conversation.OwnerConversationId,
                });
            }
            else
            {
                entity.ParentSessionId = conversation.ParentConversationId;
                entity.ForkEntryId = conversation.ForkEntryId;
                entity.OwnerTaskId = conversation.OwnerTaskId;
                entity.OwnerSessionId = conversation.OwnerConversationId;

                // The optimistic-concurrency token, incremented on every write. It is a
                // plain integer because SQLite and MySQL have no rowversion.
                entity.Version++;
            }
        }

        if (pending.Sessions.Count > 0)
        {
            _context.SaveChanges();
        }

        if (pending.Turns.Count > 0)
        {
            _context.Turns.AddRange(pending.Turns);
        }

        if (pending.Events.Count > 0)
        {
            _context.Events.AddRange(pending.Events);
        }

        _context.SaveChanges();
        transaction.Commit();
        RememberCommitted();
    }

    /// <summary>
    /// Ends the transaction and flushes the state to the backend. The undo log is kept
    /// until the write has been accepted, so a failed write is undone rather than left
    /// half-applied in the in-memory state the store reads.
    /// </summary>
    public override void Commit()
    {
        try
        {
            Persist();
        }
        catch
        {
            Rollback();
            throw;
        }

        EndTransaction();
    }

    /// <summary>Applies the persisted state to the in-memory state the store reads.</summary>
    private void Replay()
    {
        foreach (var session in _context!.Sessions.AsNoTracking())
        {
            AddConversation(new ConversationRecord(session.Id, session.ParentSessionId, session.ForkEntryId)
            {
                OwnerTaskId = session.OwnerTaskId,
                OwnerConversationId = session.OwnerSessionId,
            });
        }

        // The turns are the durable form of each entry, including the sequence minted for it.
        foreach (var turn in _context.Turns.AsNoTracking().OrderBy(turn => turn.Sequence))
        {
            _entryRecords[turn.Id] = new EntryRecord(
                turn.Id,
                turn.SessionId,
                turn.Kind,
                turn.Data is null ? null : JsonSerializer.Deserialize<JsonElement>(turn.Data),
                turn.Sequence);
            _persistedTurnIds.Add(turn.Id);
            _sequence = Math.Max(_sequence, turn.Sequence);
        }

        // The events are the write log, replayed in the order it was written. Their
        // sequence is the log's own counter, not the store's: one commit can write
        // several events at the same store sequence.
        foreach (var envelope in _context.Events.AsNoTracking().OrderBy(@event => @event.Sequence))
        {
            _envelopeSequence = Math.Max(_envelopeSequence, envelope.Sequence);
            Apply(envelope.Type, envelope.Payload, envelope.Sequence);
        }

        RememberCommitted();
    }

    /// <summary>Applies one replayed event to the in-memory state.</summary>
    private void Apply(string type, string payload, long envelopeSequence)
    {
        switch (type)
        {
            case DocumentType:
                var document = JsonSerializer.Deserialize<DocumentPayload>(payload, DurableJson.Options)!;
                EnsureDocument(document.Kind, document.OwnerId, document.Fork, document.Initial?.Clone());
                break;

            case DocumentVersionType:
                // A version carries the sequence the store minted for it, which is not the
                // envelope sequence: one commit can mint several sequences.
                var version = JsonSerializer.Deserialize<DocumentVersionPayload>(payload, DurableJson.Options)!;
                var state = EnsureDocument(version.Kind, version.OwnerId, version.Fork);
                state.Versions.Add(new DocumentVersion(version.EntryId, version.Value.Clone(), version.Sequence));
                _sequence = Math.Max(_sequence, version.Sequence);
                break;

            case TaskType:
                AddTask(JsonSerializer.Deserialize<DurableTaskRecord>(payload, DurableJson.Options)!);
                break;

            case EntryAppendType:
                var appended = JsonSerializer.Deserialize<EntryRefPayload>(payload, DurableJson.Options)!;
                if (!_entries.TryGetValue(appended.ConversationId, out var entries))
                {
                    entries = [];
                    _entries[appended.ConversationId] = entries;
                }

                entries.Add(appended.EntryId);
                break;

            case EntryListType:
                var list = JsonSerializer.Deserialize<EntryListPayload>(payload, DurableJson.Options)!;
                _entries[list.ConversationId] = [.. list.EntryIds];
                break;

            case SequenceType:
                // The sequence a commit reached. A task or a conversation consumes a
                // sequence value that no row of its own carries, so the commit records
                // the counter itself; without it a restart would mint an id twice.
                var reached = JsonSerializer.Deserialize<SequencePayload>(payload, DurableJson.Options)!;
                _sequence = Math.Max(_sequence, reached.Sequence);
                break;

            default:
                throw new InvalidDataException(
                    $"The durable store at this location holds an event of unknown type " +
                    $"'{type}' at sequence {envelopeSequence}. It was written by a newer provider.");
        }
    }

    /// <summary>
    /// Captures the writes this commit made, by diffing the in-memory state against what
    /// the last commit persisted. The base class writes straight into its collections
    /// without notifying the provider, so the writes are read back off the state.
    /// </summary>
    private PendingWrites TakePendingWrites()
    {
        var pending = new PendingWrites();

        foreach (var conversation in Conversations)
        {
            var current = JsonSerializer.Serialize(conversation, DurableJson.Options);
            if (_committedConversations.TryGetValue(conversation.Id, out var persisted) && persisted == current)
            {
                continue;
            }

            pending.Sessions.Add(conversation);
        }

        foreach (var (conversationId, entryIds) in _entries)
        {
            List<string> persisted = _committedEntries.TryGetValue(conversationId, out var known) ? known : [];
            if (entryIds.Count == persisted.Count && entryIds.SequenceEqual(persisted))
            {
                continue;
            }

            if (entryIds.Count > persisted.Count && persisted.SequenceEqual(entryIds.Take(persisted.Count)))
            {
                // A pure append: one event per new entry, plus the turn row that carries
                // the entry's sequence. An entry inherited from a parent conversation
                // already has its turn row — an entry id is one row, not one per session —
                // so only entries written for the first time get a new one.
                foreach (var entryId in entryIds.Skip(persisted.Count))
                {
                    pending.Events.Add(NewEvent(
                        EntryAppendType,
                        new EntryRefPayload(conversationId, entryId)));
                    if (_persistedTurnIds.Add(entryId))
                    {
                        pending.Turns.Add(NewTurn(conversationId, entryId));
                    }
                }

                continue;
            }

            // The list was replaced: a fork inherits its parent's entry ids, and those
            // entry ids already have a turn row under the parent's session. The list
            // itself is what the fork owns, so it is written as one event.
            pending.Events.Add(NewEvent(
                EntryListType,
                new EntryListPayload(conversationId, [.. entryIds])));
        }

        foreach (var document in Documents)
        {
            var key = DocumentKeyOf(document.Kind, document.OwnerId);
            if (!_committedDocuments.Contains(key))
            {
                pending.Events.Add(NewEvent(
                    DocumentType,
                    new DocumentPayload(document.Kind, document.OwnerId, document.Fork, document.Initial?.Clone())));
            }

            var persisted = _committedVersions.TryGetValue(key, out var count) ? count : 0;
            foreach (var version in document.Versions.Skip(persisted))
            {
                pending.Events.Add(NewEvent(
                    DocumentVersionType,
                    new DocumentVersionPayload(
                        document.Kind,
                        document.OwnerId,
                        document.Fork,
                        version.EntryId,
                        version.Sequence,
                        version.Value.Clone())));
            }
        }

        foreach (var task in Tasks)
        {
            var current = JsonSerializer.Serialize(task, DurableJson.Options);
            if (_committedTasks.TryGetValue(task.Id, out var persisted) && persisted == current)
            {
                continue;
            }

            pending.Events.Add(NewEvent(TaskType, task));
        }

        // The sequence the commit reached is written last, so a replay that stops at any
        // earlier event never claims a sequence the commit did not reach. It is written
        // whenever the commit consumed a sequence value, which is not the same as writing
        // an event: a task or a conversation consumes one without an event of its own.
        if (pending.Events.Count > 0 || Sequence > _committedSequence)
        {
            pending.Events.Add(NewEvent(SequenceType, new SequencePayload(Sequence)));
        }

        return pending;
    }

    /// <summary>Records the state the next commit diffs against.</summary>
    private void RememberCommitted()
    {
        _committedConversations = Conversations.ToDictionary(
            conversation => conversation.Id,
            conversation => JsonSerializer.Serialize(conversation, DurableJson.Options));
        _committedEntries = _entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        _committedDocuments = Documents
            .Select(document => DocumentKeyOf(document.Kind, document.OwnerId))
            .ToHashSet();
        _committedVersions = Documents.ToDictionary(
            document => DocumentKeyOf(document.Kind, document.OwnerId),
            document => document.Versions.Count);
        _committedTasks = Tasks.ToDictionary(
            task => task.Id,
            task => JsonSerializer.Serialize(task, DurableJson.Options));
        _committedSequence = Sequence;
    }

    /// <summary>Builds the turn row for an appended entry.</summary>
    private TurnEntity NewTurn(string conversationId, string entryId)
    {
        var record = _entryRecords.GetValueOrDefault(entryId);
        return new TurnEntity
        {
            Id = entryId,
            SessionId = conversationId,
            Kind = record?.Kind ?? "entry",
            Data = record?.Data is { } data ? data.GetRawText() : null,
            Sequence = record?.Sequence ?? Sequence,
        };
    }

    /// <summary>
    /// Builds an event envelope row, numbered from the log's own counter so every
    /// event has a sequence of its own even when one commit writes several.
    /// </summary>
    private EventEntity NewEvent(string type, object payload) => new()
    {
        Sequence = ++_envelopeSequence,
        Version = EnvelopeVersion,
        Type = type,
        Payload = JsonSerializer.Serialize(payload, DurableJson.Options),
    };

    /// <summary>The key a document is stored under, matching the store's own key.</summary>
    private static string DocumentKeyOf(string kind, string ownerId) => $"{kind}@{ownerId}";

    private Dictionary<string, string> _committedConversations = [];
    private Dictionary<string, List<string>> _committedEntries = [];
    private HashSet<string> _committedDocuments = [];
    private Dictionary<string, int> _committedVersions = [];
    private Dictionary<string, string> _committedTasks = [];
    private long _committedSequence;

    /// <summary>The writes one commit has to persist, captured before it touches the database.</summary>
    private sealed class PendingWrites
    {
        public List<ConversationRecord> Sessions { get; } = [];

        public List<TurnEntity> Turns { get; } = [];

        public List<EventEntity> Events { get; } = [];
    }

    /// <summary>The payload of a <c>document</c> event.</summary>
    private sealed record DocumentPayload(string Kind, string OwnerId, ForkPolicy Fork, JsonElement? Initial = null);

    /// <summary>The payload of a <c>document-version</c> event.</summary>
    private sealed record DocumentVersionPayload(
        string Kind,
        string OwnerId,
        ForkPolicy Fork,
        string EntryId,
        long Sequence,
        JsonElement Value);

    /// <summary>The payload of an <c>entry-append</c> event.</summary>
    private sealed record EntryRefPayload(string ConversationId, string EntryId);

    /// <summary>The payload of an <c>entry-list</c> event: a conversation's whole entry list.</summary>
    private sealed record EntryListPayload(string ConversationId, List<string> EntryIds);

    /// <summary>The payload of a <c>sequence</c> event: the sequence the commit reached.</summary>
    private sealed record SequencePayload(long Sequence);
}
