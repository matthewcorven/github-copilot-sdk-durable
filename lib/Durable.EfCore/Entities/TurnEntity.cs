namespace Durable.EfCore.Entities;

/// <summary>
/// The persisted form of one immutable transcript entry (a "turn" in the durable store's
/// vocabulary): a kind, an optional payload, and the session it belongs to.
/// </summary>
/// <remarks>
/// The key is the entry id the store already assigns, so no provider has to generate it.
/// <see cref="Sequence"/> is the store's monotonic write sequence and is the ordering key
/// within a session; it is not unique across sessions.
/// </remarks>
public class TurnEntity
{
    /// <summary>The entry identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The session the turn belongs to.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The entry kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The entry payload as JSON text, or null when the entry carries no payload.</summary>
    public string? Data { get; set; }

    /// <summary>The store's monotonic write sequence for this turn.</summary>
    public long Sequence { get; set; }

    /// <summary>The owning session.</summary>
    public SessionEntity? Session { get; set; }
}
