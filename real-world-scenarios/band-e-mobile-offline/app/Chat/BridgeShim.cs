using Android.Content;
using Dev.Durable.Bande.Foundry;

namespace BandEMobile.Chat;

/// <summary>
/// The boundary the decision record §3.3 describes: the only .NET file that names a type from
/// the generated Java binding (<c>Dev.Durable.Bande.Foundry</c>). Everything above it sees only
/// <see cref="IChatClient"/>, so the durable layer never learns that Android exists.
///
/// Translates the facade's callback interface into Tasks, and its integer codes into
/// <see cref="ChatFailureKind"/> so callers classify failures without knowing Kotlin constants.
/// </summary>
internal sealed class BridgeShim : Java.Lang.Object, FoundryBridge.IListener
{
    private readonly Context _context;
    private readonly string _appName;

    private TaskCompletionSource<bool>? _stageSignal;
    private TaskCompletionSource<string>? _turn;
    private Action<string>? _onChunk;
    private volatile string? _lastError;
    private volatile ChatFailureKind _lastKind;

    private BridgeShim(Context context, string appName)
    {
        _context = context;
        _appName = appName;
    }

    /// <summary>
    /// Runs start -> ensureModel -> complete through the facade and returns the answer text.
    /// Throws <see cref="BridgeException"/> with a classified reason on any failure or disconnect.
    /// </summary>
    public static async Task<string> RunAsync(
        Context context,
        string appName,
        string modelAlias,
        IReadOnlyList<ChatMessage> conversation,
        Action<string> onProgress,
        CancellationToken cancellationToken = default)
    {
        var shim = new BridgeShim(context, appName);
        return await shim.RunCoreAsync(modelAlias, conversation, onProgress, cancellationToken);
    }

    /// <summary>
    /// Exercises the facade's <c>start()</c> even when the service app is absent, so the JNI
    /// boundary itself is proven: the Kotlin class loads, the listener implementor registers,
    /// and a failure comes back as a classified code rather than a crash.
    /// </summary>
    public static async Task<string> ProbeStartAsync(Context context, string appName)
    {
        var shim = new BridgeShim(context, appName);
        using var bridge = new FoundryBridge(context, appName);

        // A turn Task is used as the completion channel so the probe reuses the same listener
        // path the real call takes; no stage chaining is involved.
        shim.MarkProbe();
        try
        {
            bridge.Start(shim);
            var outcome = await shim.WaitForProbeAsync().ConfigureAwait(false);
            return $"bridge loaded; start() reported: {outcome}";
        }
        catch (BridgeException ex)
        {
            // A classified failure still proves the boundary works: the exception came from
            // Kotlin, through the generated invoker, into this managed type.
            return $"bridge loaded; start() failed [{ex.Kind}]: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"bridge boundary error: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            bridge.Close();
        }
    }

    private async Task<string> RunCoreAsync(
        string alias,
        IReadOnlyList<ChatMessage> conversation,
        Action<string> onProgress,
        CancellationToken cancellationToken)
    {
        _onChunk = onProgress;
        using var bridge = new FoundryBridge(_context, _appName);
        using var registration = cancellationToken.Register(() => bridge.Cancel(0));

        await AwaitStageAsync(() => bridge.Start(this)).ConfigureAwait(false);
        await AwaitStageAsync(() => bridge.EnsureModel(alias, this)).ConfigureAwait(false);

        // History crosses as "role\u0001content" strings; the facade splits them back.
        var history = conversation.Select(m => $"{m.Role}\u0001{m.Text}").ToArray();
        _turn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stageSignal = null;
        IsTurnInFlight = true;
        bridge.Complete(history, 0, this);

        var answer = await _turn.Task.ConfigureAwait(false);
        bridge.Close();
        return answer;
    }

    private async Task AwaitStageAsync(Action start)
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stageSignal = signal;
        _lastError = null;
        start();
        await signal.Task.ConfigureAwait(false);
    }

    public void OnChunk(string? text) => _onChunk?.Invoke(text ?? string.Empty);

    public void OnCompleted(string? text)
    {
        // During start/ensureModel, completion means the stage succeeded and the next call may
        // be issued. Once a turn is running, it carries the answer.
        if (_turn is { Task.IsCompleted: false } turn && IsTurnInFlight)
        {
            turn.TrySetResult(text ?? string.Empty);
            return;
        }

        IsTurnInFlight = false;
        if (_probe is { Task.IsCompleted: false } probe)
        {
            probe.TrySetResult(text ?? string.Empty);
            return;
        }
        _stageSignal?.TrySetResult(true);
    }

    private volatile bool IsTurnInFlight;

    public void OnFailed(int code, string? message, bool disconnected)
    {
        var kind = code switch
        {
            FoundryBridge.CodeIncompatible => ChatFailureKind.ModelUnavailable,
            FoundryBridge.CodeDisconnected => ChatFailureKind.Disconnected,
            FoundryBridge.CodeModelMissing or FoundryBridge.CodeModelFailed => ChatFailureKind.ModelUnavailable,
            FoundryBridge.CodeStartFailed => ChatFailureKind.TransportUnavailable,
            _ => ChatFailureKind.Unknown,
        };
        Fail(kind, message ?? "(no message)");
    }

    public void OnDisconnected() =>
        Fail(ChatFailureKind.Disconnected, "service disconnected mid-turn");

    private TaskCompletionSource<string>? _probe;

    private void MarkProbe() =>
        _probe = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task<string> WaitForProbeAsync() =>
        _probe?.Task ?? Task.FromResult("(probe not armed)");

    private void Fail(ChatFailureKind kind, string message)
    {
        _lastKind = kind;
        _lastError = message;
        if (_probe is { Task.IsCompleted: false } probe)
        {
            probe.TrySetException(new BridgeException(kind, message));
        }
        if (_turn is { Task.IsCompleted: false } turn)
        {
            turn.TrySetException(new BridgeException(kind, message));
        }
        _stageSignal?.TrySetException(new BridgeException(kind, message));
    }
}

internal sealed class BridgeException(ChatFailureKind kind, string message) : Exception(message)
{
    public ChatFailureKind Kind { get; } = kind;
}
