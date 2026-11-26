using Durable;

namespace BandEMobile.Durability;

/// <summary>
/// One turn as it is stored: the user's request and, once the turn finished, the reply.
/// </summary>
public sealed class StoredTurn
{
    public int Index { get; set; }

    public string Request { get; set; } = string.Empty;

    /// <summary>The reply text, or null while the turn is unfinished or was interrupted.</summary>
    public string? Reply { get; set; }

    /// <summary>Set when the turn did not finish, naming what the store knows instead.</summary>
    public string? Note { get; set; }

    /// <summary>How long the reply took to stream, in milliseconds, when it completed.</summary>
    public long? ElapsedMs { get; set; }

    /// <summary>How many deltas the transport produced for this reply.</summary>
    public int? ChunkCount { get; set; }

    public string? CompletedAt { get; set; }
}

/// <summary>The conversation transcript: the thing the SDK does not keep.</summary>
public sealed class TranscriptState
{
    public List<StoredTurn> Turns { get; set; } = [];
}

/// <summary>The document the agent edits across turns.</summary>
public sealed class ArtifactState
{
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>Incremented on every committed revision; 0 means "never written by the agent".</summary>
    public int Revision { get; set; }

    /// <summary>The turn that produced the current body, or null for the seed.</summary>
    public int? UpdatedByTurn { get; set; }

    public string? UpdatedAt { get; set; }
}

/// <summary>One turn's checkpoint: the durable record that a turn was started and where it got to.</summary>
public sealed class TurnCheckpoint
{
    /// <summary><c>requested</c> or <c>completed</c> or <c>interrupted</c>.</summary>
    public string Phase { get; set; } = TurnPhases.Requested;

    public int TurnIndex { get; set; }

    public string Request { get; set; } = string.Empty;

    public string? Reply { get; set; }

    public string StartedAt { get; set; } = string.Empty;

    public string? FinishedAt { get; set; }

    /// <summary>The store's id for this turn's task. Written into the checkpoint, not inferred.</summary>
    public string? TaskId { get; set; }
}

public static class TurnPhases
{
    public const string Requested = "requested";
    public const string Completed = "completed";
}

/// <summary>Everything the page needs after a process start, read from the store in one pass.</summary>
/// <param name="Turns">The transcript, oldest first.</param>
/// <param name="Document">The document's current body and revision.</param>
/// <param name="Interrupted">Turns the store says were started but never finished.</param>
/// <param name="ConversationTurns">How many committed versions the transcript has.</param>
public sealed record SessionSnapshot(
    IReadOnlyList<StoredTurn> Turns,
    ArtifactState Document,
    IReadOnlyList<TurnCheckpoint> Interrupted,
    int ConversationTurns);
