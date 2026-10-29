using Durable.EfCore;
using Durable.EfCore.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Durable.EfCore.Tests;

public class ProviderNeutralityTests
{
    [Fact]
    public void Model_Uses_No_Provider_Specific_Column_Types()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new DurableDbContext(
            new DbContextOptionsBuilder<DurableDbContext>().UseSqlite(connection).Options);

        var storeTypes = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Select(property => property.GetColumnType())
            .Where(columnType => columnType is not null)
            .Select(columnType => columnType!.ToLowerInvariant())
            .Distinct()
            .ToArray();

        // rowversion/timestamp, jsonb, and JSON are the types that would fork the model.
        Assert.DoesNotContain(storeTypes, type => type.Contains("rowversion"));
        Assert.DoesNotContain(storeTypes, type => type.Contains("timestamp"));
        Assert.DoesNotContain(storeTypes, type => type.Contains("jsonb"));
        Assert.DoesNotContain(storeTypes, type => type == "json");
    }

    [Fact]
    public void Model_Carries_No_DateTimeOffset_Columns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new DurableDbContext(
            new DbContextOptionsBuilder<DurableDbContext>().UseSqlite(connection).Options);

        var clrTypes = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Select(property => property.ClrType)
            .ToArray();

        Assert.DoesNotContain(typeof(DateTimeOffset), clrTypes);
        Assert.DoesNotContain(typeof(DateTime), clrTypes);
    }

    [Fact]
    public void Every_Key_Is_Application_Assigned()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new DurableDbContext(
            new DbContextOptionsBuilder<DurableDbContext>().UseSqlite(connection).Options);

        foreach (var entity in context.Model.GetEntityTypes())
        {
            foreach (var keyProperty in entity.FindPrimaryKey()!.Properties)
            {
                Assert.Equal(ValueGenerated.Never, keyProperty.ValueGenerated);
            }
        }
    }
}
