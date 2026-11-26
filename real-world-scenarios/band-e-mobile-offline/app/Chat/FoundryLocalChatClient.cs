using System.Runtime.CompilerServices;

namespace BandEMobile.Chat;

/// <summary>
/// The Foundry Local transport: <b>embedded</b> mode.
/// </summary>
/// <remarks>
/// <para>
/// Decision record §2 chose IPC and named "the companion app is unavailable" as a legitimate
/// trigger to re-open it. On this emulator that trigger fired — Google Play reports
/// <c>com.microsoft.foundry.app</c> is not compatible with the device — and §2's fallback was
/// taken; see <c>DECISION-bridge.md</c> §6. The AAR is the only thing that changed: this type, the
/// facade, and the shim compile and run unchanged against either mode, because both AARs publish
/// the same Kotlin API (verified in <c>evidence/foundry-local-aar.txt</c>).
/// </para>
/// <para>
/// The interesting property for Band E is the negative one: <c>isConnected</c> is not a concept
/// in embedded mode (the SDK's own mode table says "not applicable"), so a turn that completes here
/// is not evidence of a healthy connection — it is evidence that no connection was needed.
/// </para>
/// </remarks>
internal sealed class FoundryLocalChatClient : IChatClient
{
    private readonly BridgeShim _shim;
    private readonly Action<string>? _log;

    public FoundryLocalChatClient(BridgeShim shim, Action<string>? log = null)
    {
        _shim = shim;
        _log = log;
    }

    public string Name => "Foundry Local (embedded)";

    public bool IsReady { get; private set; }

    public async Task<ChatResult> PrepareAsync(
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_shim.IsStarted)
            {
                var started = await _shim.StartAsync().ConfigureAwait(false);
                _log?.Invoke($"start: {started}");
            }

            var ready = await _shim.EnsureModelAsync(BandEMobile.Durability.BandEDurable.ModelAlias, progress)
                .ConfigureAwait(false);
            _log?.Invoke($"model: {ready}");

            IsReady = true;
            return ChatResult.Ok(new ChatCompletion(ready, TimeSpan.Zero, 0));
        }
        catch (BridgeException ex)
        {
            IsReady = false;
            return ChatResult.Fail(ex.Kind, $"{_shim.LastFailureDescription} {ex.Message}".Trim());
        }
        catch (Exception ex)
        {
            IsReady = false;
            return ChatResult.Fail(ChatFailureKind.Unknown, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<string> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> conversation,
        int maxTokens = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        // The facade delivers deltas on a background dispatcher and the shim marshals each one to
        // the main looper; this channel re-serialises them back into an async sequence without
        // buffering, so a delta rendered by the caller is the delta the model produced.
        var chunks = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

        var turn = _shim.StreamTurnAsync(
            conversation,
            delta => chunks.Writer.TryWrite(delta),
            cancellationToken,
            maxTokens);

        _ = Task.Run(async () =>
        {
            try
            {
                await turn.ConfigureAwait(false);
                chunks.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                chunks.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        await foreach (var delta in chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (delta.Length > 0)
            {
                yield return delta;
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListAliasesAsync()
    {
        try
        {
            if (!_shim.IsStarted)
            {
                await _shim.StartAsync().ConfigureAwait(false);
            }

            var joined = await _shim.ListAliasesAsync().ConfigureAwait(false);
            return joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"catalog failed: {_shim.LastFailureDescription} {ex.Message}");
            return [];
        }
    }
}
