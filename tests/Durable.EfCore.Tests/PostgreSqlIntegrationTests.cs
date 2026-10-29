using Durable.EfCore.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Durable.EfCore.Tests;

/// <summary>
/// The durable store against a real PostgreSQL server, plus the connection-string
/// convention that does not need one.
/// </summary>
/// <remarks>
/// The server tests skip with the probe's reason when no server is reachable, so the suite
/// still exits zero on a machine without PostgreSQL. The convention tests always run.
/// </remarks>
public sealed class PostgreSqlIntegrationTests : IClassFixture<PostgreSqlStoreFixture>
{
    private readonly PostgreSqlStoreFixture _fixture;

    public PostgreSqlIntegrationTests(PostgreSqlStoreFixture fixture) => _fixture = fixture;

    [ServerFact(ServerKind.PostgreSql)]
    public Task Fixture_Applies_The_Checked_In_Migration() =>
        ServerStoreScenarios.Fixture_Applies_The_Checked_In_Migration(_fixture);

    [ServerFact(ServerKind.PostgreSql)]
    public Task Write_Then_Read_Back_Through_A_New_Store() =>
        ServerStoreScenarios.Write_Then_Read_Back_Through_A_New_Store(_fixture);

    [ServerFact(ServerKind.PostgreSql)]
    public Task Restart_Persistence_Mints_Past_The_Replayed_Sequence() =>
        ServerStoreScenarios.Restart_Persistence_Mints_Past_The_Replayed_Sequence(_fixture);

    [ServerFact(ServerKind.PostgreSql)]
    public Task Provider_Selected_By_Name_Persists_Across_A_Restart() =>
        ServerStoreScenarios.Provider_Selected_By_Name_Persists_Across_A_Restart(_fixture);

    [Fact]
    public void Connection_String_Convention_Normalizes_A_PostgreSql_String()
    {
        var parsed = new NpgsqlConnectionStringBuilder(PostgreSqlProvider.ConnectionStringFor(
            "Host=localhost;Port=5432;Database=durable;Username=durable;Password=durable"));

        Assert.Equal("localhost", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("durable", parsed.Database);
        Assert.Equal("durable", parsed.Username);
    }

    [Fact]
    public void Connection_String_Convention_Replaces_The_Database()
    {
        var parsed = new NpgsqlConnectionStringBuilder(PostgreSqlProvider.ConnectionStringFor(
            PostgreSqlProvider.DefaultConnectionString,
            "durable_other"));

        Assert.Equal("durable_other", parsed.Database);
        Assert.Equal("localhost", parsed.Host);
    }

    [Fact]
    public void Connection_String_Convention_Rejects_An_Empty_String()
    {
        Assert.Throws<ArgumentException>(() => PostgreSqlProvider.ConnectionStringFor(" "));
        Assert.Throws<ArgumentException>(() => PostgreSqlProvider.ConnectionStringFor(" ", "durable"));
    }

    [Fact]
    public void Context_Options_Use_The_PostgreSql_Migration_Assembly()
    {
        var options = PostgreSqlProvider.ContextOptionsFor(PostgreSqlProvider.DefaultConnectionString);

        Assert.Equal(
            typeof(PostgreSqlProvider).Assembly.GetName().Name,
            new DurableDbContext(options).GetService<IMigrationsAssembly>().Assembly.GetName().Name);
    }
}
