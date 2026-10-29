// 37 — The EF Core store.
//
// The store's state, written through a relational schema instead of a JSONL file.
//
// Example 32 showed the file the store writes. This example shows the other half of the
// persistence seam: a provider that keeps the same state in a database. The store does not
// know which one it has. `DurableStoreOptions` names a provider — by factory, by registered
// name, or by the built-in format — and everything above that seam is unchanged.
//
// The provider here is `Durable.EfCore`. It maps the store's state onto three tables:
// `sessions` (one row per conversation, including fork lineage), `turns` (one row per
// transcript entry), and `events` (the append-only envelope log the provider replays on
// open). The model is deliberately provider-neutral — application-assigned keys, a plain
// integer concurrency token, JSON payloads stored as text, and no timestamps — so the same
// `DurableDbContext` is accepted by SQLite, SQL Server, PostgreSQL and MySQL without a
// per-provider fork.
//
// SQLite is the reference provider because it needs no server: the store path *is* the
// database file, and the connection string is `Data Source=<absolute path>`. That is why
// this example runs on a clean machine with nothing installed. The server-backed providers
// are the same model over a different connection string; the matrix at the end of this
// example is the whole difference.
//
// Unlike the other examples, this one references the provider as a project rather than
// inlining it with `#:include`. The provider is a separate assembly with its own EF Core
// package references and a checked-in migration, so it is consumed the way a real app
// consumes it: `#:project ../lib/Durable.EfCore/Durable.EfCore.csproj`.

#:package GitHub.Copilot.SDK@1.0.26
#:project ../lib/Durable.EfCore/Durable.EfCore.csproj
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
// File-based apps default to the NativeAOT runtime configuration, and EF Core refuses to
// build a model under it ("Model building is not supported when publishing with NativeAOT").
// This example is a JIT program, so the AOT defaults are turned off.
#:property PublishAot=false

using Durable;
using Durable.EfCore;
using Microsoft.Data.Sqlite;

var directory = Path.Combine(Path.GetTempPath(), $"durable-efcore-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var databasePath = Path.Combine(directory, "state.db");

var notes = new DocumentDefinition<Dictionary<string, string>>("example.notes");

// ─── The SQLite convention: the store path is the database file ─────────────

// The convention is implemented once, in `EfCoreStoreProvider.ConnectionStringFor`, and
// both the provider's default `Open` and the design-time factory use it. Two properties
// matter: the path is made absolute, so a relative store path is anchored at the process
// working directory rather than at whatever directory the process is in when the
// connection opens; and the string is built with `SqliteConnectionStringBuilder`, so a
// path containing a space, `;`, `=` or a quote is escaped instead of splitting the
// connection string.

var connectionString = EfCoreStoreProvider.ConnectionStringFor(databasePath);
Check.Equal(
    $"Data Source={Path.GetFullPath(databasePath)}",
    connectionString,
    "the connection string is Data Source=<absolute path>");

var awkwardPath = Path.Combine(directory, "a b;c=d'q.db");
var awkwardConnectionString = EfCoreStoreProvider.ConnectionStringFor(awkwardPath);
Check.Contains(
    awkwardConnectionString,
    "a b;c=d'q.db",
    "a path with a space, a separator, an equals sign and a quote survives intact");
Check.That(
    awkwardConnectionString.StartsWith("Data Source=\"", StringComparison.Ordinal),
    "the awkward path is quoted rather than split into extra connection-string keys");

// ─── The provider is selected by name, and the store is unchanged ───────────

// `Register()` puts the provider in the process-wide registry under `"efcore"`. A store
// then selects it with `ProviderName`, and every other option — the path, the commit
// gate, the document and task API — is exactly what the JSONL store takes.

EfCoreStoreProvider.Register();
Check.That(
    DurableStore.IsProviderRegistered(EfCoreStoreProvider.RegisteredName),
    "the provider registers itself under its name");

string conversationId;

using (var store = DurableStore.Create(new DurableStoreOptions
{
    Path = databasePath,
    ProviderName = EfCoreStoreProvider.RegisteredName,
}))
{
    Check.That(store.IsPersistent, "the EF Core provider is a durable tier");

    conversationId = await store.CommitAsync(async tx =>
    {
        var conversation = tx.CreateConversation();
        var draft = tx.Doc(notes, conversation.Id);
        draft.Value = new Dictionary<string, string> { ["text"] = "written through EF Core" };
        draft.Save(tx.AppendEntry(conversation.Id, "notes.write"));
        await Task.CompletedTask;
        return conversation.Id;
    });
}

// ─── The state is in a real database, not a JSONL file ─────────────────────

// The store path is a SQLite database file, and the schema is the checked-in migration,
// applied by the provider on first open. Reading the catalog directly is the proof that
// the commit went through the relational model rather than a file of JSON lines.

Check.That(File.Exists(databasePath), "the store path is a database file");
Check.That(
    !File.ReadAllText(databasePath).Contains("\"type\":", StringComparison.Ordinal),
    "the file is a database, not a JSONL record log");

var tables = new List<string>();
using (var connection = new SqliteConnection(connectionString))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        tables.Add(reader.GetString(0));
    }
}

Check.That(tables.Contains("sessions"), "the migration created the sessions table");
Check.That(tables.Contains("turns"), "the migration created the turns table");
Check.That(tables.Contains("events"), "the migration created the events table");
Check.That(
    tables.Contains("__EFMigrationsHistory"),
    "the migration is recorded, so a later open does not re-apply it");

// ─── Reopening the same database reads back the same state ─────────────────

// The provider replays the `events` log on open, exactly as the JSONL provider replays its
// records. The store above is disposed first: a persistent store holds an exclusive lock on
// its path for its lifetime, and that is true of this provider too.

using (var reopened = DurableStore.Create(new DurableStoreOptions
{
    Path = databasePath,
    ProviderName = EfCoreStoreProvider.RegisteredName,
}))
{
    var document = Check.NotNull(
        reopened.Document(notes.Kind, conversationId),
        "the document was read back from the database");
    var value = DurableJson.FromElement<Dictionary<string, string>>(document.Latest!.Value);
    Check.Equal(
        "written through EF Core",
        value!["text"],
        "the reopened store holds the value the first store committed");
}

// ─── The seam's precedence: a factory wins over a name ─────────────────────

// `Provider` is the lowest-level way to select a backend: a factory that receives the
// options and returns the provider. It takes precedence over `ProviderName`, which takes
// precedence over `Format`. This is how a caller reaches a database the registry does not
// know about — a server-backed provider, or a connection that is not a file path at all.

var factoryPath = Path.Combine(directory, "factory.db");
using (var store = DurableStore.Create(new DurableStoreOptions
{
    Path = factoryPath,
    Provider = _ => new EfCoreStoreProvider(),
}))
{
    Check.That(store.IsPersistent, "a provider factory selects the EF Core provider directly");
    await store.CommitAsync(async tx =>
    {
        var conversation = tx.CreateConversation();
        var draft = tx.Doc(notes, conversation.Id);
        draft.Value = new Dictionary<string, string> { ["text"] = "selected by factory" };
        draft.Save(tx.AppendEntry(conversation.Id, "notes.write"));
        await Task.CompletedTask;
    });
}

Check.That(File.Exists(factoryPath), "the factory-selected store wrote its own database");

// A name that nothing registered is refused at open, rather than silently falling back to
// a built-in format. A typo in a provider name is a configuration error, not a downgrade.

var refused = false;
try
{
    using var unregistered = DurableStore.Create(new DurableStoreOptions
    {
        Path = Path.Combine(directory, "never.db"),
        ProviderName = "no-such-provider",
    });
}
catch (InvalidOperationException error)
{
    refused = error.Message.Contains("no-such-provider", StringComparison.Ordinal);
}

Check.That(refused, "an unregistered provider name is refused at open, naming the name");

DurableStore.UnregisterProvider(EfCoreStoreProvider.RegisteredName);
Check.That(
    !DurableStore.IsProviderRegistered(EfCoreStoreProvider.RegisteredName),
    "the registration is process-wide, so it can be removed again");

// ─── The provider matrix ───────────────────────────────────────────────────

// One model, four databases. Only the package and the connection string change; the
// migration is generated from the same `DurableDbContext` for each. SQLite is wired end to
// end today. The three server-backed providers are the same model over a different
// connection string, and their packages and migrations land with #52.

var matrix = new (string Provider, string Package, string ConnectionString, string Migration, string Server)[]
{
    ("SQLite", "Microsoft.EntityFrameworkCore.Sqlite",
        "Data Source=<absolute path>", "checked in: Migrations/20261029034106_InitialCreate", "no"),
    ("SQL Server", "Microsoft.EntityFrameworkCore.SqlServer",
        "Server=<host>;Database=<name>;...", "dotnet ef migrations add InitialCreate_SqlServer", "yes"),
    ("PostgreSQL", "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Host=<host>;Database=<name>;...", "dotnet ef migrations add InitialCreate_PostgreSql", "yes"),
    ("MySQL", "Pomelo.EntityFrameworkCore.MySql",
        "Server=<host>;Database=<name>;...", "dotnet ef migrations add InitialCreate_MySql", "yes"),
};

Console.WriteLine($"database: {databasePath}");
Console.WriteLine($"tables:   {string.Join(", ", tables)}");
Console.WriteLine();
Console.WriteLine("provider    | package                                    | needs a server");
Console.WriteLine("------------|--------------------------------------------|---------------");
foreach (var row in matrix)
{
    Console.WriteLine($"{row.Provider,-11} | {row.Package,-42} | {row.Server}");
}

Console.WriteLine();
Console.WriteLine("SQLite is the reference provider: no server, no credentials, no setup step.");
Console.WriteLine("The server-backed providers are the same model over a different connection string;");
Console.WriteLine("their packages and migrations are planned in #52.");

Directory.Delete(directory, recursive: true);

// Mirrors the Pi Durable store's SQLite backend: the store path is the database file, the
// provider is selected through the same options the JSONL store takes, and the state
// survives a restart because it is replayed from the database on open.
