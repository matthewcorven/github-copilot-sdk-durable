using Durable.EfCore;
using Durable.EfCore.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Durable.EfCore.Tests;

public class DurableDbContextTests
{
    private static DurableDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<DurableDbContext>().UseSqlite(connection).Options);

    [Fact]
    public void Model_Builds_And_Maps_Sessions_Turns_And_Events()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);

        var model = context.Model;

        Assert.Equal("sessions", model.FindEntityType(typeof(SessionEntity))!.GetTableName());
        Assert.Equal("turns", model.FindEntityType(typeof(TurnEntity))!.GetTableName());
        Assert.Equal("events", model.FindEntityType(typeof(EventEntity))!.GetTableName());
    }

    [Fact]
    public void Event_Envelope_Keeps_The_Settled_Shape()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);

        var entity = context.Model.FindEntityType(typeof(EventEntity))!;
        var properties = entity.GetProperties().Select(property => property.Name).ToArray();

        Assert.Contains("Version", properties);
        Assert.Contains("Sequence", properties);
        Assert.Contains("Type", properties);
        Assert.Contains("Payload", properties);

        // seq is the key and is application-assigned, so no provider has to generate it.
        Assert.Equal("Sequence", entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal(ValueGenerated.Never, entity.FindProperty("Sequence")!.ValueGenerated);
    }

    [Fact]
    public void Keys_Are_Application_Assigned_And_Concurrency_Token_Is_Portable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);

        var session = context.Model.FindEntityType(typeof(SessionEntity))!;
        var turn = context.Model.FindEntityType(typeof(TurnEntity))!;

        Assert.Equal(ValueGenerated.Never, session.FindProperty("Id")!.ValueGenerated);
        Assert.Equal(ValueGenerated.Never, turn.FindProperty("Id")!.ValueGenerated);

        // A long token, not rowversion/byte[], which SQLite and MySQL do not have.
        var version = session.FindProperty("Version")!;
        Assert.True(version.IsConcurrencyToken);
        Assert.Equal(typeof(long), version.ClrType);
    }

    [Fact]
    public void Migration_Applies_To_A_Fresh_Database()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var context = CreateContext(connection);
        context.Database.Migrate();

        var applied = context.Database.GetAppliedMigrations().ToArray();
        Assert.Contains(applied, name => name.EndsWith("_InitialCreate", StringComparison.Ordinal));

        var tables = context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToList();

        Assert.Contains("sessions", tables);
        Assert.Contains("turns", tables);
        Assert.Contains("events", tables);
    }

    [Fact]
    public void Migration_Can_Be_Generated_From_The_Model()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);

        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var initializer = context.GetService<IModelRuntimeInitializer>();

        var snapshot = initializer.Initialize(migrationsAssembly.ModelSnapshot!.Model, designTime: true);
        var differences = differ.GetDifferences(
            snapshot.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        // The checked-in migration is the model: nothing is left to generate.
        Assert.Empty(differences);
    }
}
