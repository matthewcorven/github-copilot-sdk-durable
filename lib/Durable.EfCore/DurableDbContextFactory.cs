using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Durable.EfCore;

/// <summary>
/// The design-time factory the EF Core tooling uses to build the model without a host.
/// </summary>
/// <remarks>
/// The migration baseline is SQLite because it needs no server. The model itself is
/// provider-neutral, so the other providers generate their own migrations from the same
/// <see cref="DurableDbContext"/>; see <c>lib/Durable.EfCore/README.md</c>.
/// </remarks>
public sealed class DurableDbContextFactory : IDesignTimeDbContextFactory<DurableDbContext>
{
    /// <inheritdoc />
    public DurableDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DurableDbContext>()
            .UseSqlite("Data Source=durable-design-time.sqlite")
            .Options;

        return new DurableDbContext(options);
    }
}
