using Durable.EfCore.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Durable.EfCore.Tests;

/// <summary>
/// The durable store against a real SQL Server, plus the connection-string convention that
/// does not need one.
/// </summary>
/// <remarks>
/// The server tests skip with the probe's reason when no server is reachable, so the suite
/// still exits zero on a machine without SQL Server. The convention tests always run: they
/// are the part of the provider that can be proved without a server.
/// </remarks>
public sealed class SqlServerIntegrationTests : IClassFixture<SqlServerStoreFixture>
{
    private readonly SqlServerStoreFixture _fixture;

    public SqlServerIntegrationTests(SqlServerStoreFixture fixture) => _fixture = fixture;

    [ServerFact(ServerKind.SqlServer)]
    public Task Fixture_Applies_The_Checked_In_Migration() =>
        ServerStoreScenarios.Fixture_Applies_The_Checked_In_Migration(_fixture);

    [ServerFact(ServerKind.SqlServer)]
    public Task Write_Then_Read_Back_Through_A_New_Store() =>
        ServerStoreScenarios.Write_Then_Read_Back_Through_A_New_Store(_fixture);

    [ServerFact(ServerKind.SqlServer)]
    public Task Restart_Persistence_Mints_Past_The_Replayed_Sequence() =>
        ServerStoreScenarios.Restart_Persistence_Mints_Past_The_Replayed_Sequence(_fixture);

    [ServerFact(ServerKind.SqlServer)]
    public Task Provider_Selected_By_Name_Persists_Across_A_Restart() =>
        ServerStoreScenarios.Provider_Selected_By_Name_Persists_Across_A_Restart(_fixture);

    [Fact]
    public void Connection_String_Convention_Normalizes_A_Sql_Server_String()
    {
        var parsed = new SqlConnectionStringBuilder(SqlServerProvider.ConnectionStringFor(
            "Server=localhost,1433;Database=durable;User Id=sa;Password=Durable_Local_1;" +
            "TrustServerCertificate=True;Encrypt=False"));

        Assert.Equal("localhost,1433", parsed.DataSource);
        Assert.Equal("durable", parsed.InitialCatalog);
        Assert.Equal("sa", parsed.UserID);
    }

    [Fact]
    public void Connection_String_Convention_Replaces_The_Database()
    {
        var parsed = new SqlConnectionStringBuilder(SqlServerProvider.ConnectionStringFor(
            SqlServerProvider.DefaultConnectionString,
            "durable_other"));

        Assert.Equal("durable_other", parsed.InitialCatalog);
        Assert.Equal("localhost,1433", parsed.DataSource);
    }

    [Fact]
    public void Connection_String_Convention_Rejects_An_Empty_String()
    {
        Assert.Throws<ArgumentException>(() => SqlServerProvider.ConnectionStringFor(" "));
        Assert.Throws<ArgumentException>(() => SqlServerProvider.ConnectionStringFor(" ", "durable"));
    }

    [Fact]
    public void Context_Options_Use_The_Sql_Server_Migration_Assembly()
    {
        var options = SqlServerProvider.ContextOptionsFor(SqlServerProvider.DefaultConnectionString);

        Assert.Equal(
            typeof(SqlServerProvider).Assembly.GetName().Name,
            new DurableDbContext(options)
                .GetService<IMigrationsAssembly>()
                .Assembly
                .GetName()
                .Name);
    }
}
