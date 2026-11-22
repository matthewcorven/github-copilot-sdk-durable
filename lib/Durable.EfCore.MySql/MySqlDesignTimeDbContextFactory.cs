using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Durable.EfCore.MySql;

/// <summary>
/// The design-time factory the EF Core tooling uses to generate the MySQL migration.
/// </summary>
/// <remarks>
/// Run the tooling with this project as both the project and the startup project, so this
/// factory is the one it finds:
/// <code>
/// dotnet ef migrations add InitialCreate --project lib/Durable.EfCore.MySql
/// </code>
/// The connection string is only used to build the model; no server is contacted.
/// </remarks>
public sealed class MySqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DurableDbContext>
{
    /// <inheritdoc />
    public DurableDbContext CreateDbContext(string[] args) => new(MySqlProvider.ContextOptions());
}
