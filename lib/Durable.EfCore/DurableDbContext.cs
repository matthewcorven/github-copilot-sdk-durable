using Durable.EfCore.Entities;
using Microsoft.EntityFrameworkCore;

namespace Durable.EfCore;

/// <summary>
/// The durable store's schema as an EF Core model.
/// </summary>
/// <remarks>
/// The model is deliberately provider-neutral: every entity definition below is the same
/// regardless of which provider is configured, so SQLite, SQL Server, PostgreSQL and MySQL
/// all accept it without a per-provider fork. The choices that make that true are:
/// <list type="bullet">
/// <item>Application-assigned keys. No identity/sequence columns, so no
/// <c>IDENTITY</c> vs <c>SERIAL</c> vs <c>AUTO_INCREMENT</c> divergence.</item>
/// <item>A <see cref="long"/> concurrency token instead of <c>rowversion</c>/<c>byte[]</c>,
/// which SQLite and MySQL do not have.</item>
/// <item>JSON payloads stored as text, not as a provider-specific JSON column type.</item>
/// <item>No <see cref="DateTimeOffset"/> columns, which MySQL maps differently from the rest.</item>
/// </list>
/// </remarks>
public class DurableDbContext : DbContext
{
    /// <summary>Creates a context over the given options.</summary>
    public DurableDbContext(DbContextOptions<DurableDbContext> options)
        : base(options)
    {
    }

    /// <summary>The sessions (conversations) known to the store.</summary>
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    /// <summary>The turns (transcript entries) known to the store.</summary>
    public DbSet<TurnEntity> Turns => Set<TurnEntity>();

    /// <summary>The append-only event envelope.</summary>
    public DbSet<EventEntity> Events => Set<EventEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SessionEntity>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Id).HasMaxLength(256).ValueGeneratedNever();
            entity.Property(session => session.ParentSessionId).HasMaxLength(256);
            entity.Property(session => session.ForkEntryId).HasMaxLength(256);
            entity.Property(session => session.OwnerTaskId).HasMaxLength(256);
            entity.Property(session => session.OwnerSessionId).HasMaxLength(256);

            // A plain integer token, incremented by the store on write. Configured as a
            // concurrency token rather than a provider-specific row version.
            entity.Property(session => session.Version).IsConcurrencyToken();

            entity.HasIndex(session => session.ParentSessionId);
            entity.HasIndex(session => session.OwnerTaskId);
        });

        modelBuilder.Entity<TurnEntity>(entity =>
        {
            entity.ToTable("turns");
            entity.HasKey(turn => turn.Id);
            entity.Property(turn => turn.Id).HasMaxLength(256).ValueGeneratedNever();
            entity.Property(turn => turn.SessionId).HasMaxLength(256).IsRequired();
            entity.Property(turn => turn.Kind).HasMaxLength(128).IsRequired();
            entity.Property(turn => turn.Data).HasColumnType("text");

            entity.HasOne(turn => turn.Session)
                .WithMany(session => session.Turns)
                .HasForeignKey(turn => turn.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            // The read path is "the turns of a session, oldest first".
            entity.HasIndex(turn => new { turn.SessionId, turn.Sequence });
            entity.HasIndex(turn => turn.Sequence);
        });

        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.ToTable("events");
            entity.HasKey(@event => @event.Sequence);
            entity.Property(@event => @event.Sequence).ValueGeneratedNever();
            entity.Property(@event => @event.Version).IsRequired();
            entity.Property(@event => @event.Type).HasMaxLength(128).IsRequired();
            entity.Property(@event => @event.Payload).HasColumnType("text").IsRequired();
            entity.Property(@event => @event.SessionId).HasMaxLength(256);

            entity.HasOne(@event => @event.Session)
                .WithMany(session => session.Events)
                .HasForeignKey(@event => @event.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            // The append-only read path is "the events after a sequence, in order".
            entity.HasIndex(@event => new { @event.SessionId, @event.Sequence });
            entity.HasIndex(@event => @event.Type);
        });
    }
}
