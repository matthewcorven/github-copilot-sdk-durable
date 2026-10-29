using Durable.EfCore.MySql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.Data.MySqlClient;

namespace Durable.EfCore.Tests;

/// <summary>
/// The durable store against a real MySQL server, plus the connection-string convention
/// that does not need one.
/// </summary>
/// <remarks>
/// The server tests skip with the probe's reason when no server is reachable, so the suite
/// still exits zero on a machine without MySQL. The convention tests always run.
/// </remarks>
public sealed class MySqlIntegrationTests : IClassFixture<MySqlStoreFixture>
{
    private readonly MySqlStoreFixture _fixture;

    public MySqlIntegrationTests(MySqlStoreFixture fixture) => _fixture = fixture;

    [ServerFact(ServerKind.MySql)]
    public Task Fixture_Applies_The_Checked_In_Migration() =>
        ServerStoreScenarios.Fixture_Applies_The_Checked_In_Migration(_fixture);

    [ServerFact(ServerKind.MySql)]
    public Task Write_Then_Read_Back_Through_A_New_Store() =>
        ServerStoreScenarios.Write_Then_Read_Back_Through_A_New_Store(_fixture);

    [ServerFact(ServerKind.MySql)]
    public Task Restart_Persistence_Mints_Past_The_Replayed_Sequence() =>
        ServerStoreScenarios.Restart_Persistence_Mints_Past_The_Replayed_Sequence(_fixture);

    [ServerFact(ServerKind.MySql)]
    public Task Provider_Selected_By_Name_Persists_Across_A_Restart() =>
        ServerStoreScenarios.Provider_Selected_By_Name_Persists_Across_A_Restart(_fixture);

    [Fact]
    public void Connection_String_Convention_Normalizes_A_MySql_String()
    {
        var parsed = new MySqlConnectionStringBuilder(MySqlProvider.ConnectionStringFor(
            "Server=localhost;Port=3306;Database=durable;User Id=root;Password=durable"));

        Assert.Equal("localhost", parsed.Server);
        Assert.Equal(3306u, parsed.Port);
        Assert.Equal("durable", parsed.Database);
        Assert.Equal("root", parsed.UserID);
    }

    [Fact]
    public void Connection_String_Convention_Replaces_The_Database()
    {
        var parsed = new MySqlConnectionStringBuilder(MySqlProvider.ConnectionStringFor(
            MySqlProvider.DefaultConnectionString,
            "durable_other"));

        Assert.Equal("durable_other", parsed.Database);
        Assert.Equal("localhost", parsed.Server);
    }

    [Fact]
    public void Connection_String_Convention_Rejects_An_Empty_String()
    {
        Assert.Throws<ArgumentException>(() => MySqlProvider.ConnectionStringFor(" "));
        Assert.Throws<ArgumentException>(() => MySqlProvider.ConnectionStringFor(" ", "durable"));
    }

    [Fact]
    public void Context_Options_Use_The_MySql_Migration_Assembly()
    {
        var options = MySqlProvider.ContextOptionsFor(MySqlProvider.DefaultConnectionString);

        Assert.Equal(
            typeof(MySqlProvider).Assembly.GetName().Name,
            new DurableDbContext(options).GetService<IMigrationsAssembly>().Assembly.GetName().Name);
    }
}
