namespace BandEMobile.Chat;

/// <summary>
/// One turn in a conversation. The caller owns the transcript: Foundry Local does not
/// manage history, so a transport receives the whole list on every request.
/// </summary>
public sealed record ChatMessage(string Role, string Text);

/// <summary>The reason a completion failed, classified where the failure is visible.</summary>
public enum ChatFailureKind
{
    /// <summary>No transport is available: the companion app is missing or the device is offline.</summary>
    TransportUnavailable,

    /// <summary>The model could not be made ready: not in the catalog, download refused, load failed.</summary>
    ModelUnavailable,

    /// <summary>The service disconnected mid-turn. Variant 2 depends on telling this apart.</summary>
    Disconnected,

    /// <summary>Anything else the transport could not classify.</summary>
    Unknown,
}

public sealed record ChatCompletion(string Text, string TransportName);

public sealed record ChatFailure(ChatFailureKind Kind, string Message, bool IsRetryable);

public sealed record ChatResult(ChatCompletion? Completion, ChatFailure? Failure)
{
    public bool Succeeded => Completion is not null;

    public static ChatResult Ok(ChatCompletion completion) => new(completion, null);

    public static ChatResult Fail(ChatFailureKind kind, string message, bool retryable = true) =>
        new(null, new ChatFailure(kind, message, retryable));
}

/// <summary>
/// The seam every Band E variant shares. Nothing here names an Android type, so the same
/// code path is used for an on-device model and a remote one (decision record §3.3).
/// </summary>
public interface IChatClient
{
    /// <summary>Diagnostic name of the transport, shown on screen so evidence names what ran.</summary>
    string Name { get; }

    Task<ChatResult> CompleteAsync(
        IReadOnlyList<ChatMessage> conversation,
        CancellationToken cancellationToken = default);
}
