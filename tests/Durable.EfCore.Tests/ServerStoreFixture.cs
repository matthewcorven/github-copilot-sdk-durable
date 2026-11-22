using Durable.EfCore.MySql;
using Durable.EfCore.PostgreSql;
using Durable.EfCore.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Npgsql;

namespace Durable.EfCore.Tests;

/// <summary>
/// A server-backed database for one test class: a database created for the run, migrated
/// from the provider's checked-in migration, and dropped when the fixture is disposed.
/// </summary>
/// <remarks>
/// The fixture is the server counterpart of <see cref="SqliteStoreFixture"/>: it applies
/// the checked-in migration itself, before any store opens, so a test can assert that the
/// schema is the migration's. It creates a database of its own rather than reusing a
/// shared one, so a run cannot see another run's rows, and it drops that database on
/// dispose whether the tests passed or failed.
/// <para>
/// The store's exclusive lock is taken on <see cref="LockPath"/>, a file in a temporary
/// directory, because the provider locks the store path and a server store has no file of
/// its own. The directory is deleted with the database.
/// </para>
/// </remarks>
public abstract class ServerStoreFixture : IAsyncLifetime
{
    private readonly List<DurableStore> _stores = [];

    /// <summary>Creates the fixture. Nothing is created until <see cref="InitializeAsync"/>.</summary>
    /// <param name="kind">The server this fixture runs against.</param>
    protected ServerStoreFixture(ServerKind kind)
    {
        Kind = kind;
        DatabaseName = $"durable_test_{Guid.NewGuid():N}";
        LockDirectory = Path.Combine(Path.GetTempPath(), $"durable-efcore-{kind}-{Guid.NewGuid():N}");
    }

    /// <summary>The server this fixture runs against.</summary>
    public ServerKind Kind { get; }

    /// <summary>The database created for this fixture.</summary>
    public string DatabaseName { get; }

    /// <summary>The temporary directory that holds the store's lock file.</summary>
    public string LockDirectory { get; }

    /// <summary>The store path the provider locks; no database lives here.</summary>
    public string LockPath => Path.Combine(LockDirectory, "store");

    /// <summary>The connection string the provider uses for <see cref="DatabaseName"/>.</summary>
    public string ConnectionString => Kind switch
    {
        ServerKind.SqlServer => SqlServerProvider.ConnectionStringFor(
            ServerProbe.ConnectionStringFor(Kind), DatabaseName),
        ServerKind.PostgreSql => PostgreSqlProvider.ConnectionStringFor(
            ServerProbe.ConnectionStringFor(Kind), DatabaseName),
        ServerKind.MySql => MySqlProvider.ConnectionStringFor(
            ServerProbe.ConnectionStringFor(Kind), DatabaseName),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown server."),
    };

    /// <summary>
    /// Whether the fixture created a database. It is <see langword="false"/> when the
    /// server is unreachable, in which case the fixture did nothing and the tests that
    /// need it are skipped.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Creates the database, the lock directory, and the schema.</summary>
    /// <remarks>
    /// xUnit constructs a class fixture even when every test in the class is skipped, so
    /// this returns without touching the server when the probe says it is unreachable.
    /// Otherwise a machine with no server would fail in fixture setup instead of skipping.
    /// </remarks>
    public async Task InitializeAsync()
    {
        if (!ServerProbe.For(Kind).IsReachable)
        {
            return;
        }

        Directory.CreateDirectory(LockDirectory);

        await ExecuteOnServerAsync($"CREATE DATABASE {Quote(DatabaseName)}");

        await using var context = OpenContext();
        await context.Database.MigrateAsync();

        IsInitialized = true;
    }

    /// <summary>Opens a store over the fixture's database, selected through the seam.</summary>
    /// <returns>The store, disposed with the fixture.</returns>
    public DurableStore OpenStore()
    {
        var store = DurableStore.Create(new DurableStoreOptions
        {
            Path = LockPath,
            Provider = _ => CreateProvider(),
        });

        _stores.Add(store);
        return store;
    }

    /// <summary>Opens a context over the fixture's database, for asserting on the rows.</summary>
    /// <returns>The context; the caller disposes it.</returns>
    public DurableDbContext OpenContext() => new(ContextOptions());

    /// <summary>The context options the fixture's database is reached with.</summary>
    /// <returns>The options for this provider and database.</returns>
    public DbContextOptions<DurableDbContext> ContextOptions() => Kind switch
    {
        ServerKind.SqlServer => SqlServerProvider.ContextOptionsFor(ConnectionString),
        ServerKind.PostgreSql => PostgreSqlProvider.ContextOptionsFor(ConnectionString),
        ServerKind.MySql => MySqlProvider.ContextOptionsFor(ConnectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown server."),
    };

    /// <summary>Creates a store provider over the fixture's database.</summary>
    /// <returns>The provider, ready to be opened by a store.</returns>
    public EfCoreStoreProvider CreateProvider() => Kind switch
    {
        ServerKind.SqlServer => SqlServerProvider.CreateProvider(ConnectionString),
        ServerKind.PostgreSql => PostgreSqlProvider.CreateProvider(ConnectionString),
        ServerKind.MySql => MySqlProvider.CreateProvider(ConnectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown server."),
    };

    /// <summary>Disposes every store, drops the database, and deletes the lock directory.</summary>
    public async Task DisposeAsync()
    {
        if (!IsInitialized)
        {
            return;
        }

        foreach (var store in _stores)
        {
            store.Dispose();
        }

        _stores.Clear();

        // The provider pools connections; a pooled handle would keep the database in use
        // and make the drop fail on SQL Server and MySQL.
        switch (Kind)
        {
            case ServerKind.SqlServer:
                SqlConnection.ClearAllPools();
                break;
            case ServerKind.PostgreSql:
                NpgsqlConnection.ClearAllPools();
                break;
            case ServerKind.MySql:
                MySqlConnection.ClearAllPools();
                break;
        }

        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS {Quote(DatabaseName)}");

        if (Directory.Exists(LockDirectory))
        {
            Directory.Delete(LockDirectory, recursive: true);
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = CreateServerConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private System.Data.Common.DbConnection CreateServerConnection() => Kind switch
    {
        // SQL Server and PostgreSQL both need a database to connect to, so the maintenance
        // database is used; MySQL connects without one.
        ServerKind.SqlServer => new SqlConnection(
            SqlServerProvider.ConnectionStringFor(ServerProbe.ConnectionStringFor(Kind), "master")),
        ServerKind.PostgreSql => new NpgsqlConnection(
            PostgreSqlProvider.ConnectionStringFor(ServerProbe.ConnectionStringFor(Kind), "postgres")),
        // MySQL connects without a database, so the configured string is used unchanged.
        ServerKind.MySql => new MySqlConnection(
            MySqlProvider.ConnectionStringFor(ServerProbe.ConnectionStringFor(Kind))),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown server."),
    };

    // The database name is generated from a GUID, so it needs no escaping; the quoting is
    // per provider because the three disagree on the delimiter.
    private string Quote(string identifier) => Kind switch
    {
        ServerKind.SqlServer => $"[{identifier}]",
        ServerKind.PostgreSql => $"\"{identifier}\"",
        ServerKind.MySql => $"`{identifier}`",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown server."),
    };
}

/// <summary>The SQL Server fixture.</summary>
public sealed class SqlServerStoreFixture() : ServerStoreFixture(ServerKind.SqlServer);

/// <summary>The PostgreSQL fixture.</summary>
public sealed class PostgreSqlStoreFixture() : ServerStoreFixture(ServerKind.PostgreSql);

/// <summary>The MySQL fixture.</summary>
public sealed class MySqlStoreFixture() : ServerStoreFixture(ServerKind.MySql);
