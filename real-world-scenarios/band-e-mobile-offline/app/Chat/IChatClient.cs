namespace BandEMobile.Chat;

/// <summary>One message in a conversation. Role is <c>system</c>, <c>user</c>, or <c>assistant</c>.</summary>
public sealed record ChatMessage(string Role, string Text)
{
    public const string SystemRole = "system";
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    public static ChatMessage System(string text) => new(SystemRole, text);

    public static ChatMessage User(string text) => new(UserRole, text);

    public static ChatMessage Assistant(string text) => new(AssistantRole, text);
}

/// <summary>The reason a turn failed, classified where the failure was visible.</summary>
public enum ChatFailureKind
{
    /// <summary>No transport: the model is not ready, or the bridge could not start.</summary>
    TransportUnavailable,

    /// <summary>The model could not be made ready: not in the catalog, download refused, load failed.</summary>
    ModelUnavailable,

    /// <summary>A connection to a service was lost mid-turn. Embedded mode has no such service.</summary>
    Disconnected,

    /// <summary>Anything else the transport could not classify.</summary>
    Unknown,
}

public sealed record ChatFailure(ChatFailureKind Kind, string Message);

public sealed record ChatCompletion(string Text, TimeSpan Elapsed, int ChunkCount);

public sealed record ChatResult(ChatCompletion? Completion, ChatFailure? Failure)
{
    public bool Succeeded => Completion is not null;

    public static ChatResult Ok(ChatCompletion completion) => new(completion, null);

    public static ChatResult Fail(ChatFailureKind kind, string message) =>
        new(null, new ChatFailure(kind, message));
}

/// <summary>
/// The transport seam, and the app's durability boundary in disguise.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here names an Android type, and <see cref="CompleteStreamingAsync"/> takes the
/// <b>whole</b> conversation on every call. That is not an oversight: the issue's note is the
/// point of the whole variant — "the on-device SDK does not manage history; Durable is what makes
/// the history survive". A transport that kept history would make the durable store optional,
/// which is exactly what Band E is not demonstrating.
/// </para>
/// <para>
/// The signature is <c>IAsyncEnumerable&lt;string&gt;</c> rather than a callback so the deltas are
/// pushed by whoever iterates them: the page can render each one as it is produced, and a caller
/// that wanted the assembled answer can concatenate them. No Android type appears in the shape.
/// </para>
/// </remarks>
public interface IChatClient
{
    /// <summary>Diagnostic name of the transport, shown on screen so evidence names what ran.</summary>
    string Name { get; }

    /// <summary>Resolves, downloads if needed, and loads the model. Safe to call twice.</summary>
    Task<ChatResult> PrepareAsync(Action<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams one turn. Each yielded string is a non-empty delta, in order.
    /// </summary>
    /// <param name="conversation">The whole history, oldest first, plus this turn.</param>
    /// <param name="maxTokens">A generation cap, or 0 for the transport's default.</param>
    /// <param name="cancellationToken">Cancels the turn through the transport's own stop mechanism.</param>
    IAsyncEnumerable<string> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> conversation,
        int maxTokens = 0,
        CancellationToken cancellationToken = default);

    /// <summary>The catalog's aliases, or a description of why they could not be read.</summary>
    Task<IReadOnlyList<string>> ListAliasesAsync();

    /// <summary>Whether <see cref="PrepareAsync"/> has succeeded.</summary>
    bool IsReady { get; }
}
