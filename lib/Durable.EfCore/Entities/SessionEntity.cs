namespace Durable.EfCore.Entities;

/// <summary>
/// The persisted form of a conversation (a "session" in the durable store's vocabulary).
/// </summary>
/// <remarks>
/// The key is application-assigned: the store already mints conversation ids, so the
/// model never asks the database to generate one. That keeps the same model valid on
/// SQLite, SQL Server, PostgreSQL and MySQL, which disagree about identity columns.
/// </remarks>
public class SessionEntity
{
    /// <summary>The conversation identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The conversation this one was forked from, if any.</summary>
    public string? ParentSessionId { get; set; }

    /// <summary>The entry the fork was taken at, if any.</summary>
    public string? ForkEntryId { get; set; }

    /// <summary>The owning task, when the conversation is task-owned.</summary>
    public string? OwnerTaskId { get; set; }

    /// <summary>The owning conversation, when the conversation is conversation-owned.</summary>
    public string? OwnerSessionId { get; set; }

    /// <summary>
    /// The optimistic-concurrency token. A plain integer rather than a provider-specific
    /// <c>rowversion</c>, because SQLite and MySQL have no such column type.
    /// </summary>
    public long Version { get; set; }

    /// <summary>The turns recorded against this session.</summary>
    public List<TurnEntity> Turns { get; set; } = [];

    /// <summary>The events recorded against this session.</summary>
    public List<EventEntity> Events { get; set; } = [];
}
