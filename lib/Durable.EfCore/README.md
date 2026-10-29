# Durable.EfCore

The durable store's schema as an EF Core model, and the provider that writes the store's
state through it. Per-provider connection wiring (SQL Server, PostgreSQL, MySQL) is #2.3 /
#2.4.

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
