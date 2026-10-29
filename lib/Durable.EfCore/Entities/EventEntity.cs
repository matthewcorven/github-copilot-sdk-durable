namespace Durable.EfCore.Entities;

/// <summary>
/// The append-only event envelope, persisted as a row.
/// </summary>
/// <remarks>
/// The envelope shape is settled and must not be redesigned: <c>{v, seq, type, payload}</c>.
/// <see cref="Sequence"/> is the envelope's <c>seq</c> and is the primary key, because the
/// store's write sequence is already unique and monotonic. It is application-assigned
/// (<c>ValueGeneratedNever</c>), which is what keeps the model portable: no provider has to
/// supply an identity or sequence default.
/// <para>
/// <see cref="Payload"/> is JSON text rather than a provider-specific JSON column type.
/// SQL Server has <c>nvarchar(max)</c>, PostgreSQL has <c>jsonb</c>, MySQL has <c>JSON</c>
/// and SQLite has none of them; a text column is the only shape all four accept unchanged.
/// </para>
/// </remarks>
public class EventEntity
{
    /// <summary>The envelope's <c>seq</c>: the store's monotonic write sequence.</summary>
    public long Sequence { get; set; }

    /// <summary>The envelope's <c>v</c>: the envelope format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The envelope's <c>type</c>: the record type discriminator.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The envelope's <c>payload</c>: the record body as JSON text.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>The session the event belongs to, when the event is session-scoped.</summary>
    public string? SessionId { get; set; }

    /// <summary>The owning session, when the event is session-scoped.</summary>
    public SessionEntity? Session { get; set; }
}
