using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Durable.EfCore.PostgreSql;

/// <summary>
/// The PostgreSQL wiring for the durable store: the connection-string convention, the
/// context options, and the registration that makes the provider selectable by name.
/// </summary>
/// <remarks>
/// The model is <see cref="DurableDbContext"/>, unchanged: this project adds a provider
/// package and a migration assembly, not a second model. The migrations live in this
/// assembly rather than in <c>Durable.EfCore</c> because EF Core resolves a context's
/// model snapshot as the first <c>ModelSnapshot</c> it finds in the migrations assembly —
/// four providers' snapshots in one assembly would be ambiguous, and
/// <c>Database.Migrate()</c> would apply every provider's migration to every database.
/// </remarks>
public static class PostgreSqlProvider
{
    /// <summary>The name the provider is registered under by <see cref="Register"/>.</summary>
    public const string RegisteredName = "efcore-postgresql";

    /// <summary>The environment variable that overrides <see cref="DefaultConnectionString"/>.</summary>
    public const string ConnectionStringVariable = "DURABLE_POSTGRESQL_CONNECTION";

    /// <summary>
    /// The connection string used when <see cref="ConnectionStringVariable"/> is not set:
    /// the local development server the integration tests expect. It is a throwaway
    /// credential for a container on localhost, not a secret, and it is never used unless
    /// no override is configured.
    /// </summary>
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=durable;Username=durable;Password=durable";

    /// <summary>The migrations assembly: this one, which holds the PostgreSQL migration.</summary>
    public static string MigrationsAssembly => typeof(PostgreSqlProvider).Assembly.FullName!;

    /// <summary>
    /// The connection string the provider uses: <see cref="ConnectionStringVariable"/> when
    /// it is set, otherwise <see cref="DefaultConnectionString"/>.
    /// </summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } configured
            ? configured
            : DefaultConnectionString;

    /// <summary>
    /// The connection-string convention for the PostgreSQL provider: the caller supplies a
    /// PostgreSQL connection string, and it is validated and normalized by
    /// <see cref="NpgsqlConnectionStringBuilder"/> so a malformed string fails here rather
    /// than at the first query.
    /// </summary>
    /// <param name="connectionString">A PostgreSQL connection string.</param>
    /// <returns>The same connection, normalized.</returns>
    public static string ConnectionStringFor(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return new NpgsqlConnectionStringBuilder(connectionString).ConnectionString;
    }

    /// <summary>
    /// The connection-string convention for a named database on the same server: the
    /// caller supplies a connection string and the database is replaced, so a caller that
    /// holds a server-level connection string can point the store at one database without
    /// rebuilding the string by hand.
    /// </summary>
    /// <param name="connectionString">A PostgreSQL connection string.</param>
    /// <param name="database">The database the store should use.</param>
    /// <returns>The same connection, pointed at <paramref name="database"/>.</returns>
    public static string ConnectionStringFor(string connectionString, string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = database,
        };

        return builder.ConnectionString;
    }

    /// <summary>Builds the PostgreSQL context options for a connection string.</summary>
    /// <param name="connectionString">A PostgreSQL connection string.</param>
    /// <returns>The options the provider opens the database with.</returns>
    public static DbContextOptions<DurableDbContext> ContextOptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<DurableDbContext>()
            .UseNpgsql(
                ConnectionStringFor(connectionString),
                npgsql => npgsql.MigrationsAssembly(MigrationsAssembly))
            .Options;

    /// <summary>Builds the context options for <see cref="ConnectionString"/>.</summary>
    /// <returns>The options the provider opens the database with.</returns>
    public static DbContextOptions<DurableDbContext> ContextOptions() => ContextOptionsFor(ConnectionString);

    /// <summary>Creates a store provider over a PostgreSQL connection string.</summary>
    /// <param name="connectionString">A PostgreSQL connection string.</param>
    /// <returns>The provider, ready to be opened by a store.</returns>
    public static EfCoreStoreProvider CreateProvider(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return new EfCoreStoreProvider(_ => ContextOptionsFor(connectionString));
    }

    /// <summary>
    /// Registers the provider with <see cref="DurableStore.RegisterProvider"/> under
    /// <see cref="RegisteredName"/>, so a store can select it with
    /// <c>ProviderName = PostgreSqlProvider.RegisteredName</c>. The connection string is
    /// read when the store opens, not when it is registered.
    /// </summary>
    public static void Register() =>
        DurableStore.RegisterProvider(RegisteredName, _ => new EfCoreStoreProvider(_ => ContextOptions()));
}
