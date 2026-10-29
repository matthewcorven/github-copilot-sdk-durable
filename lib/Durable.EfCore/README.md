# Durable.EfCore

The durable store's schema as an EF Core model, and the provider that writes the store's
state through it. SQLite is the reference provider and is wired end to end here; the
server-backed providers (SQL Server, PostgreSQL, MySQL) are #2.4.

## What is mapped

| Table | Entity | Key | Notes |
|---|---|---|---|
| `sessions` | `SessionEntity` | `Id` (string, application-assigned) | Conversations, including fork lineage and ownership. |
| `turns` | `TurnEntity` | `Id` (string, application-assigned) | Transcript entries. `Sequence` orders them within a session. |
| `events` | `EventEntity` | `Sequence` (long, application-assigned) | The append-only `{v, seq, type, payload}` envelope. |

The envelope shape is settled and is not redesigned here: `Version` is `v`, `Sequence` is
`seq`, `Type` is `type`, `Payload` is `payload`.

## Why the model is provider-neutral

Every entity definition in `DurableDbContext.OnModelCreating` is the same regardless of the
configured provider. There is no `Database.IsSqlite()` / `IsNpgsql()` branch anywhere in the
model. That is possible because of four choices:

1. **Application-assigned keys.** `ValueGeneratedNever()` on every key. No `IDENTITY`
   (SQL Server), `SERIAL`/`GENERATED` (PostgreSQL), or `AUTO_INCREMENT` (MySQL) divergence.
2. **A `long` concurrency token, not `rowversion`.** SQLite and MySQL have no `rowversion`
   column type. `SessionEntity.Version` is a plain integer marked `IsConcurrencyToken()`;
   the store increments it on write.
3. **JSON as text.** `Payload` and `Data` are `text` columns, not `jsonb` (PostgreSQL),
   `JSON` (MySQL), or `nvarchar(max)` (SQL Server). Text is the only shape all four accept
   unchanged.
4. **No `DateTimeOffset`.** MySQL maps it differently from the other three, so the model
   carries no timestamps at all. Ordering is by the store's monotonic `Sequence`.

## The provider

`EfCoreStoreProvider` implements every member of `IDurableStoreProvider`. It derives from
`InMemoryStoreProvider`, so the read and mutation members are the settled in-memory
semantics, and overrides only what makes it persistent: `IsPersistent`, `Open`,
`Persist()`, and `Dispose`.

The mapping is a hybrid, because the store's state is not all transcript-shaped:

| Store state | Written as |
|---|---|
| Conversations | `sessions` rows, `Version` incremented on every update |
| Entries | `turns` rows, keyed by the entry id, `Sequence` = the sequence minted for the entry |
| Documents, document versions, tasks, entry lists | `events` envelopes (`document`, `document-version`, `task`, `entry-append`, `entry-list`) |

Consecutive appends write an `entry-append` event *and* a `turns` row. A fork that inherits
its parent's entry ids writes only an `entry-list` event, because those ids already have
`turns` rows under the parent — an entry id is one row, not one per session.

`event.Sequence` is the envelope log's own counter, not the store's sequence: a single
commit can write several envelopes, and a single commit can also mint several store
sequences. Document versions therefore carry their minted `sequence` in the payload, and a
replay takes the maximum of both counters so a reopened store always mints past every
sequence it replayed.

## Atomicity and isolation

The in-memory and JSONL stores get atomicity from a compensating-action log: every mutation
registers its inverse, and a failure runs the inverses most-recent-first. This provider gets
atomicity from a database transaction instead. `Persist()` takes the pending writes, opens a
transaction, writes sessions first (foreign-key ordering), then turns and events, and commits
once. A failure rolls the whole thing back.

These are different mechanisms, so the guarantee is demonstrated rather than assumed:
`tests/Durable.EfCore.Tests/StoreAtomicityParityTests.cs` runs the same scenarios against both
stores and compares the observable outcome — the same ids, the same sequence, the same record
counts.

`Persist()` calls `BeginTransaction()` with no argument, so the isolation level is the
provider's default:

| Provider | Isolation level | Notes |
|---|---|---|
| SQLite | `Serializable` | The default for `BeginTransaction()`. SQLite serialises writers: a write transaction takes the database write lock, so two writers cannot interleave. Readers see the last committed state. |
| SQL Server | To be documented with #52 | Not wired up in this tree. |
| PostgreSQL | To be documented with #52 | Not wired up in this tree. |
| MySQL | To be documented with #52 | Not wired up in this tree. |

The store also serialises writers within one process: `DurableStore.CommitAsync` holds a gate
for the whole transaction, so the isolation level governs cross-process concurrency, not
in-process concurrency.

## Registration

The provider registers itself, so existing call sites keep the provider they had:

```csharp
EfCoreStoreProvider.Register();

using var store = DurableStore.Create(new DurableStoreOptions
{
    Path = "session.db",
    ProviderName = EfCoreStoreProvider.RegisteredName, // "efcore"
});
```

Alternatively, pass a context-options factory and skip the registry:

```csharp
using var store = DurableStore.Create(new DurableStoreOptions
{
    Provider = _ => new DbContextOptionsBuilder<DurableDbContext>()
        .UseSqlite($"Data Source={path}")
        .Options,
});
```

`Provider` wins over `ProviderName`, which wins over `Format`. The seam is additive: no
`DurableStoreFormat` arm, no constructor, and no settled member changed.

## The SQLite connection-string convention

**The store path is the database file, and the connection string is
`Data Source=<absolute path>`.** Nothing else is required: no server, no port, no
credentials, no environment variable. That is why SQLite is the reference provider — the
suite runs on a clean machine and in CI with no database installed.

The convention is implemented once, in `EfCoreStoreProvider.ConnectionStringFor(path)`, and
the provider's default `Open` uses it. The rules it encodes:

| Rule | Why |
|---|---|
| The path is resolved with `Path.GetFullPath` | A relative store path is anchored at the process working directory, not at whatever directory the process is in when the connection opens. |
| The string is built with `SqliteConnectionStringBuilder` | A path containing a space, `;`, `=` or a quote is escaped instead of splitting the connection string. |
| `Mode` stays `ReadWriteCreate` (the default) | The database file is created on first open, so a new store path needs no setup step. |
| The provider calls `Database.Migrate()` on open | The schema is the checked-in migration, applied on first use and idempotent on every later open. |
| The store path is held with an exclusive lock for the store's lifetime | One writer per database file, exactly as the JSONL provider behaves. |

So these two are the same database:

```csharp
// The convention, spelled out:
var connectionString = EfCoreStoreProvider.ConnectionStringFor("session.db");
// "Data Source=/absolute/working/directory/session.db"

// The same thing, through the provider:
using var store = DurableStore.Create(new DurableStoreOptions
{
    Path = "session.db",
    ProviderName = EfCoreStoreProvider.RegisteredName,
});
```

`EfCoreStoreProvider.ContextOptionsFor(path)` returns the matching
`DbContextOptions<DurableDbContext>`, for a caller that wants the context without the store
(the design-time factory uses it too).

A connection that is not a file path — a shared-cache in-memory database, or a
server-backed provider — goes through the context-options factory constructor instead, and
then the convention above does not apply.

## Migrations

`Migrations/` contains the SQLite baseline (`InitialCreate`), because SQLite needs no server
to generate or apply a migration.

The other providers generate their own migrations from the same `DurableDbContext`; nothing
in the model changes. From this directory:

```sh
# SQL Server
dotnet ef migrations add InitialCreate_SqlServer \
  --project lib/Durable.EfCore --output-dir Migrations/SqlServer

# PostgreSQL
dotnet ef migrations add InitialCreate_PostgreSql \
  --project lib/Durable.EfCore --output-dir Migrations/PostgreSql

# MySQL
dotnet ef migrations add InitialCreate_MySql \
  --project lib/Durable.EfCore --output-dir Migrations/MySql
```

Each command needs the matching provider package (`Microsoft.EntityFrameworkCore.SqlServer`,
`Npgsql.EntityFrameworkCore.PostgreSQL`, `Pomelo.EntityFrameworkCore.MySql`) and a
`DurableDbContextFactory` that calls the matching `Use*` method. Adding those packages and
factories is #2.4, deliberately not done here.

## Tests

`tests/Durable.EfCore.Tests` covers model construction, the envelope shape, key and
concurrency-token portability, applying the checked-in migration to a fresh in-memory
database, and that the model and the migration snapshot agree (nothing left to generate).

It also runs the behavioural contract the JSONL provider is held to — round-trip of every
`IDurableStoreProvider` member, rollback in memory and across reopen, monotonic sequences
after reopen, fork inheritance, task checkpoint and memo persistence, abort outcomes,
single-writer locking, and provider registration.

`SqliteIntegrationTests` is the file-backed half. `SqliteStoreFixture` creates a directory
under the system temp path, applies the migration to a real database file in it, and deletes
the directory on dispose — including the `-wal`/`-shm` sidecars and the store's lock files,
and whether the test passed or failed. The tests then cover:

- the fixture's database is a real SQLite file with the migration applied, and the fixture
  deletes it on dispose;
- write, dispose the store, open a **new** store over the same file, and read the
  conversation, task, document and entry list back — the restart-persistence proof;
- every commit survives a restart, and the reopened store mints past every replayed
  sequence;
- the provider selected by name through the registration seam persists across a restart;
- the provider applies the migration on first use of a path that does not exist yet;
- the connection-string convention: absolute `Data Source`, relative paths resolved,
  separators escaped, empty path rejected.

No test needs a database server, so the suite passes on a clean machine.
