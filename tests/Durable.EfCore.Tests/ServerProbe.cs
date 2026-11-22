using System.Collections.Concurrent;
using System.Data.Common;
using Durable.EfCore.MySql;
using Durable.EfCore.PostgreSql;
using Durable.EfCore.SqlServer;
using Microsoft.Data.SqlClient;
using MySql.Data.MySqlClient;
using Npgsql;

namespace Durable.EfCore.Tests;

/// <summary>The database servers the integration tests can run against.</summary>
public enum ServerKind
{
    /// <summary>Microsoft SQL Server.</summary>
    SqlServer,

    /// <summary>PostgreSQL.</summary>
    PostgreSql,

    /// <summary>MySQL.</summary>
    MySql,
}

/// <summary>The outcome of a reachability probe: reachable, or not and why.</summary>
/// <param name="IsReachable">Whether a connection was opened and closed successfully.</param>
/// <param name="Endpoint">The host, port and database the probe tried.</param>
/// <param name="Reason">Why the server is not reachable; <see langword="null"/> when it is.</param>
public sealed record ProbeResult(bool IsReachable, string Endpoint, string? Reason)
{
    /// <summary>
    /// The message a skipped test reports. It names the endpoint and the underlying
    /// failure, so a skip is never mistaken for a test that passed by doing nothing.
    /// </summary>
    public string SkipMessage =>
        $"{Endpoint} is not reachable, so this test was skipped rather than failed: {Reason} " +
        "Start the server, or point the provider's connection-string environment variable at one, to run it.";
}

/// <summary>
/// Probes whether a database server is reachable, so the integration tests can skip
/// instead of fail on a machine that has no server.
/// </summary>
/// <remarks>
/// The probe opens a real connection with a two-second timeout. It is deliberately a
/// connection attempt and not a port check: a port that accepts a connection but speaks
/// another protocol is not a usable server, and a test that skipped on a bare TCP connect
/// would hide that. The result is cached per server for the life of the process, so a
/// suite of skipped tests costs one probe, not one per test.
/// </remarks>
public static class ServerProbe
{
    private static readonly ConcurrentDictionary<ServerKind, ProbeResult> Results = new();

    /// <summary>Probes <paramref name="kind"/>, or returns the cached result.</summary>
    /// <param name="kind">The server to probe.</param>
    /// <returns>Whether the server is reachable, and why not when it is not.</returns>
    public static ProbeResult For(ServerKind kind) => Results.GetOrAdd(kind, Probe);

    /// <summary>The connection string the tests use for <paramref name="kind"/>.</summary>
    /// <param name="kind">The server.</param>
    /// <returns>The configured connection string, or the provider's local default.</returns>
    public static string ConnectionStringFor(ServerKind kind) => kind switch
    {
        ServerKind.SqlServer => SqlServerProvider.ConnectionString,
        ServerKind.PostgreSql => PostgreSqlProvider.ConnectionString,
        ServerKind.MySql => MySqlProvider.ConnectionString,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown server."),
    };

    /// <summary>
    /// Probes an arbitrary endpoint. Exposed so the skip mechanism itself can be tested
    /// against an endpoint that is known to be closed.
    /// </summary>
    /// <param name="endpoint">A description of what is being probed, used in the message.</param>
    /// <param name="connect">Creates the connection to probe with.</param>
    /// <returns>Whether the connection opened, and why not when it did not.</returns>
    public static ProbeResult Probe(string endpoint, Func<DbConnection> connect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(connect);

        try
        {
            using var connection = connect();
            connection.Open();
            return new ProbeResult(true, endpoint, null);
        }
        catch (Exception exception)
        {
            return new ProbeResult(false, endpoint, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static ProbeResult Probe(ServerKind kind)
    {
        var (endpoint, connect) = kind switch
        {
            ServerKind.SqlServer => DescribeSqlServer(),
            ServerKind.PostgreSql => DescribePostgreSql(),
            ServerKind.MySql => DescribeMySql(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown server."),
        };

        return Probe(endpoint, connect);
    }

    private static (string Endpoint, Func<DbConnection> Connect) DescribeSqlServer()
    {
        var builder = new SqlConnectionStringBuilder(SqlServerProvider.ConnectionString)
        {
            ConnectTimeout = 2,
        };

        var endpoint = $"SQL Server at {builder.DataSource}, database '{builder.InitialCatalog}'";
        return (endpoint, () => new SqlConnection(builder.ConnectionString));
    }

    private static (string Endpoint, Func<DbConnection> Connect) DescribePostgreSql()
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgreSqlProvider.ConnectionString)
        {
            Timeout = 2,
        };

        var endpoint = $"PostgreSQL at {builder.Host}:{builder.Port}, database '{builder.Database}'";
        return (endpoint, () => new NpgsqlConnection(builder.ConnectionString));
    }

    private static (string Endpoint, Func<DbConnection> Connect) DescribeMySql()
    {
        var builder = new MySqlConnectionStringBuilder(MySqlProvider.ConnectionString)
        {
            ConnectionTimeout = 2,
        };

        var endpoint = $"MySQL at {builder.Server}:{builder.Port}, database '{builder.Database}'";
        return (endpoint, () => new MySqlConnection(builder.ConnectionString));
    }
}

/// <summary>
/// A fact that runs only when its database server is reachable, and otherwise skips with
/// the probe's reason.
/// </summary>
/// <remarks>
/// The skip is decided when the test is discovered, from a real connection attempt, and
/// the reason names the endpoint and the failure. A skipped test therefore reports why it
/// did not run; it never passes by silently doing nothing.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ServerFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute for one server.</summary>
    /// <param name="kind">The server the test needs.</param>
    public ServerFactAttribute(ServerKind kind)
    {
        var probe = ServerProbe.For(kind);
        if (!probe.IsReachable)
        {
            Skip = probe.SkipMessage;
        }
    }
}
