using Android.Content;

namespace BandEMobile.Chat;

/// <summary>
/// The Foundry Local transport: IPC mode, as decision record §2 selects.
///
/// The call path is .NET -> <see cref="BridgeShim"/> -> the Kotlin facade -> Foundry Local's
/// AAR -> the on-device service app. This type decides only whether that path can be entered;
/// the shim owns the interop.
/// </summary>
public sealed class FoundryLocalChatClient : IChatClient
{
    private const string ModelAlias = "phi-3.5-mini";

    private readonly Context? _context;
    private readonly Func<FoundryLocalProbe.Availability> _availability;

    public FoundryLocalChatClient(Context? context)
        : this(context, () => FoundryLocalProbe.Check(context))
    {
    }

    /// <summary>Test seam: lets the classification be exercised without a device.</summary>
    public FoundryLocalChatClient(Context? context, Func<FoundryLocalProbe.Availability> availability)
    {
        _context = context;
        _availability = availability;
    }

    public string Name => "Foundry Local (IPC)";

    public async Task<ChatResult> CompleteAsync(
        IReadOnlyList<ChatMessage> conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var availability = _availability();
        if (!availability.CompanionInstalled)
        {
            // IPC mode cannot start without the service app; report that rather than guess.
            return ChatResult.Fail(
                ChatFailureKind.TransportUnavailable,
                $"companion app {FoundryLocalProbe.CompanionPackage} {availability}",
                retryable: true);
        }

        if (_context is null)
        {
            return ChatResult.Fail(
                ChatFailureKind.TransportUnavailable,
                "no Android Context available",
                retryable: false);
        }

        try
        {
            var text = await BridgeShim.RunAsync(
                _context,
                appName: "BandEMobile",
                modelAlias: ModelAlias,
                conversation,
                onProgress: _ => { },
                cancellationToken).ConfigureAwait(false);

            return ChatResult.Ok(new ChatCompletion(text, Name));
        }
        catch (BridgeException ex)
        {
            return ChatResult.Fail(ex.Kind, ex.Message, retryable: ex.Kind != ChatFailureKind.ModelUnavailable);
        }
        catch (Exception ex)
        {
            return ChatResult.Fail(ChatFailureKind.Unknown, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
