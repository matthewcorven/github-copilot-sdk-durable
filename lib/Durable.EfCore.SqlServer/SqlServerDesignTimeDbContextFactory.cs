using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Durable.EfCore.SqlServer;

/// <summary>
/// The design-time factory the EF Core tooling uses to generate the SQL Server migration.
/// </summary>
/// <remarks>
/// Run the tooling with this project as both the project and the startup project, so this
/// factory is the one it finds:
/// <code>
/// dotnet ef migrations add InitialCreate --project lib/Durable.EfCore.SqlServer
/// </code>
/// The connection string is only used to build the model; no server is contacted.
/// </remarks>
public sealed class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DurableDbContext>
{
    /// <inheritdoc />
    public DurableDbContext CreateDbContext(string[] args) => new(SqlServerProvider.ContextOptions());
}
