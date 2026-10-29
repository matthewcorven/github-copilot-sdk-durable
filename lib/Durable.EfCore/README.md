# Durable.EfCore

The durable store's schema as an EF Core model, and the provider that writes the store's
state through it. SQLite is the reference provider and is wired end to end here; the
server-backed providers (SQL Server, PostgreSQL, MySQL) are wired in their own assemblies,
described under [Server providers](#server-providers).

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
| SQL Server | `ReadCommitted` | `SqlConnection.BeginTransaction()` maps `IsolationLevel.Unspecified` to `ReadCommitted` before it issues `SET TRANSACTION ISOLATION LEVEL`, so the level is explicit rather than inherited from the server's `SET TRANSACTION ISOLATION LEVEL` default. |
| PostgreSQL | `ReadCommitted` | `NpgsqlTransaction.Init` maps `IsolationLevel.Unspecified` to `ReadCommitted` and prepends `BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED`. This is also PostgreSQL's own default. |
| MySQL | `RepeatableRead` | `MySqlConnection.BeginTransaction()` maps `IsolationLevel.Unspecified` to `RepeatableRead` and issues `SET TRANSACTION ISOLATION LEVEL REPEATABLE READ` before `BEGIN`. This is also InnoDB's default. |

The three server levels are read from the provider packages this repository references
(`Microsoft.Data.SqlClient` 6.1.6, `Npgsql` 10.0.3, `MySql.Data` 26.10.0), not assumed from
the server's own default — the two happen to agree, but the client is what sets the level.
They are not asserted by a test: the parity suite compares the observable outcome of the same
scenarios across stores, so it would catch a lost write or a partial commit, not a level that
differs from the one named here.

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

### One migration assembly per provider

The server providers do **not** add their migrations here. Each is a project of its own:

| Project | Provider package | Migration |
|---|---|---|
| `lib/Durable.EfCore.SqlServer` | `Microsoft.EntityFrameworkCore.SqlServer` | `Migrations/20261029135250_InitialCreate` |
| `lib/Durable.EfCore.PostgreSql` | `Npgsql.EntityFrameworkCore.PostgreSQL` | `Migrations/20261029135238_InitialCreate` |
| `lib/Durable.EfCore.MySql` | `MySql.EntityFrameworkCore` | `Migrations/20261029135245_InitialCreate` |

The reason is EF Core's own resolution rule, not taste. A context's model snapshot is the
**first** `ModelSnapshot` subclass the migrations assembly contains, and `Database.Migrate()`
applies **every** migration in that assembly. Four providers' snapshots and migrations in
one assembly would therefore be ambiguous: the snapshot picked would be whichever the
loader saw first, and a SQL Server database would be handed PostgreSQL's `CREATE TABLE`
statements. One assembly per provider makes the snapshot and the migration set unambiguous
by construction, and `MigrationsAssembly(...)` in each provider's `ContextOptionsFor` is what
selects it.

The alternative — one assembly with per-provider output directories — was rejected for
exactly that reason. Output directories separate the files on disk; they do not separate
them in the assembly.

### Generating a migration

Each provider project is both the project and the startup project, so the tooling finds that
provider's `IDesignTimeDbContextFactory` and no other:

```sh
dotnet ef migrations add InitialCreate \
  --project lib/Durable.EfCore.PostgreSql \
  --startup-project lib/Durable.EfCore.PostgreSql \
  --output-dir Migrations
```

No server is contacted: the factory builds the model from the connection string without
opening a connection. The connection string it uses is the provider's convention (below), so
`DURABLE_POSTGRESQL_CONNECTION` and friends select the target when a migration is applied
with `dotnet ef database update`.

## Server providers

Each server provider is a static class with the same shape as the SQLite convention:
`RegisteredName`, `ConnectionStringVariable`, `DefaultConnectionString`, `ConnectionString`,
`ConnectionStringFor`, `ContextOptionsFor`, `ContextOptions`, `CreateProvider`, `Register`.

| Provider | `RegisteredName` | Environment variable | Default |
|---|---|---|---|
| SQL Server | `efcore-sqlserver` | `DURABLE_SQLSERVER_CONNECTION` | `Server=localhost,1433;Database=durable;User Id=sa;…` |
| PostgreSQL | `efcore-postgresql` | `DURABLE_POSTGRESQL_CONNECTION` | `Host=localhost;Port=5432;Database=durable;Username=durable;…` |
| MySQL | `efcore-mysql` | `DURABLE_MYSQL_CONNECTION` | `Server=localhost;Port=3306;Database=durable;User Id=root;…` |

The defaults are throwaway credentials for a container on localhost. They are not secrets and
are never used when the environment variable is set.

### The connection-string convention

**The caller supplies a connection string; the provider validates and normalizes it with the
provider's own connection-string builder, and never rewrites it.** That is the whole
convention, and it is implemented once per provider in `ConnectionStringFor`, which
`ContextOptionsFor`, `ContextOptions`, `CreateProvider` and `Register` all route through —
the same single-implementation rule the SQLite path follows.

| Rule | Why |
|---|---|
| The string is parsed with `SqlConnectionStringBuilder` / `NpgsqlConnectionStringBuilder` / `MySqlConnectionStringBuilder` | A malformed string fails at the convention, not at the first query. |
| An empty or whitespace string is rejected | A store with no connection string is a configuration error, not a store that silently writes nowhere. |
| `ConnectionStringFor(connectionString, database)` replaces only the database | A caller holding a server-level string can point the store at one database without rebuilding the string by hand. The integration tests use it to create a database per run. |
| The environment variable is read when the store opens, not when it registers | A process can set the variable after registering the provider. |
| The provider calls `Database.Migrate()` on open | The schema is the provider's checked-in migration, applied on first use and idempotent on every later open. |

```csharp
PostgreSqlProvider.Register();

using var store = DurableStore.Create(new DurableStoreOptions
{
    Path = "store.lock",                       // the store's exclusive lock, not a database
    ProviderName = PostgreSqlProvider.RegisteredName,
});
```

A server store has no file of its own, but the provider still takes its exclusive lock on
`Path` when one is set, so a server store should set `Path` to a lock file. Leaving it null
skips the lock and allows two processes to write the same database.

### MySQL provider package

MySQL uses Oracle's `MySql.EntityFrameworkCore`, not Pomelo. Pomelo's newest release is 9.0.0
and has no EF Core 10 build, so it cannot be used with this repository's EF Core 10. Oracle's
package targets `net10.0` and is licensed GPL-2.0-only WITH Universal-FOSS-exception-1.0,
which is a different licence from this repository's MIT. That is a licensing decision for the
maintainers, recorded here so it is not rediscovered: swapping to Pomelo is a package
reference and a `UseMySQL` call once a Pomelo EF Core 10 build exists.

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

`SqliteIntegrationTests` needs no database server, so that half of the suite passes on a
clean machine.

### The server integration tests

`SqlServerIntegrationTests`, `PostgreSqlIntegrationTests` and `MySqlIntegrationTests` run the
same scenarios — `ServerStoreScenarios` — against a real server, plus the connection-string
convention, which needs no server and always runs.

`ServerStoreFixture` is the server counterpart of `SqliteStoreFixture`: it creates a database
named for the run, applies the provider's checked-in migration, and drops the database and
deletes the lock directory on dispose, whether the tests passed or failed.

**When no server is reachable the server tests skip, and the suite still exits zero.** The
skip is decided at discovery time by `ServerFactAttribute`, from a real connection attempt
with a two-second timeout, and the reason names the endpoint and the failure:

```
SQL Server at localhost,1433, database 'durable' is not reachable, so this test was skipped
rather than failed: SqlException: A network-related or instance-specific error occurred …
Start the server, or point the provider's connection-string environment variable at one, to run it.
```

The probe is a connection attempt rather than a port check, because a port that accepts a
connection but speaks another protocol is not a usable server. `ServerProbeTests` asserts the
skip mechanism itself — that an unreachable endpoint produces that message, that a reachable
one produces no reason, and that `ServerFactAttribute` skips exactly when the probe says so —
so a skip is provable from the test output rather than inferred from a green suite.
