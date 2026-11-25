using Android.App;
using Android.Content;
using Android.OS;
using BandEMobile.Durability;
using Dev.Durable.Bande.Foundry;

namespace BandEMobile.Chat;

/// <summary>
/// The bridge boundary, and the app's only file that names an Android type.
/// </summary>
/// <remarks>
/// <para>
/// Decision record §3.3 says the durable layer must not learn that Android exists. The way this
/// app keeps that true is mechanical rather than aspirational: this file is the single place a
/// <c>using Android.*</c> appears, so <c>grep -rl 'using Android' app/</c> returning one path is
/// the whole proof. Everything above it — <see cref="IChatClient"/>, the durable store, the page —
/// sees only plain .NET types.
/// </para>
/// <para>
/// It holds three things, all of which are Android concerns:
/// <see cref="BridgeShim"/> (the JNI listener and the one long-lived
/// <c>FoundryBridge</c>), <see cref="DeviceProfile"/> (where app-private storage is, and what the
/// device reports about itself), and <see cref="DeviceResources"/> (memory and disk, measured).
/// </para>
/// <para>
/// The bridge is <b>not</b> created per turn. In embedded mode the inference engine is
/// process-global (<c>CoreRuntime</c> is a singleton) and closing a bridge would tear it down,
/// forcing a multi-hundred-megabyte model reload between turns. So the shim owns one bridge for
/// the app's lifetime and reuses the loaded model.
/// </para>
/// </remarks>
internal sealed class BridgeShim : Java.Lang.Object, FoundryBridge.IListener
{
    private readonly FoundryBridge _bridge;
    private readonly Handler _main;

    private TaskCompletionSource<string>? _stage;
    private TaskCompletionSource<string>? _turn;
    private TaskCompletionSource<BridgeException>? _failure;
    private Action<string>? _delta;
    private Action<string>? _progress;
    private volatile bool _turnInFlight;
    private volatile bool _closed;
    private ChatFailureKind _lastKind = ChatFailureKind.Unknown;
    private string _lastError = "(none)";

    public BridgeShim(Context context, string appName)
    {
        _main = new Handler(Looper.MainLooper!);
        _bridge = new FoundryBridge(context, appName);
    }

    /// <summary>The failure class the facade reported last, for the screen and the evidence file.</summary>
    public string LastFailureDescription => $"[{_lastKind}] {_lastError}";

    /// <summary>Whether <see cref="StartAsync"/> has succeeded and the bridge has not been closed.</summary>
    public bool IsStarted { get; private set; }

    /// <summary>
    /// Creates the manager and checks compatibility. Idempotent, because the facade's own
    /// <c>start()</c> answers "already started" once a manager exists.
    /// </summary>
    public async Task<string> StartAsync()
    {
        var signal = ArmStage();
        _bridge.Start(this);
        var outcome = await signal.Task.ConfigureAwait(false);
        IsStarted = true;
        return outcome;
    }

    /// <summary>
    /// Resolves the alias, downloads it when it is not cached, loads it, and creates the chat
    /// client. Progress strings arrive on <paramref name="progress"/> (off the main thread).
    /// </summary>
    public async Task<string> EnsureModelAsync(string alias, Action<string>? progress)
    {
        var signal = ArmStage();
        _progress = progress;
        try
        {
            _bridge.EnsureModel(alias, this);
            return await signal.Task.ConfigureAwait(false);
        }
        finally
        {
            _progress = null;
        }
    }

    /// <summary>The catalog's aliases, comma-separated, or a classified failure description.</summary>
    public async Task<string> ListAliasesAsync()
    {
        var signal = ArmStage();
        _bridge.ListModelAliases(this);
        return await signal.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// One streaming turn. Each delta is posted to the main looper <em>in order</em> before
    /// <paramref name="onDelta"/> runs, so the caller may touch UI directly.
    /// </summary>
    /// <returns>The assembled answer, identical to the concatenation of the deltas.</returns>
    public async Task<string> StreamTurnAsync(
        IReadOnlyList<ChatMessage> history,
        Action<string> onDelta,
        CancellationToken cancellationToken,
        int maxTokens = 0)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        // The turn has two completion channels: the answer, and the classified failure. Whichever
        // fires first settles the call. Without the failure channel a failed turn returns an empty
        // answer and is committed as a completed turn with nothing in it — a durable lie, and the
        // first thing this turned up on the device.
        var turn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<BridgeException>(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = turn;
        _failure = failure;
        _delta = onDelta;
        _turnInFlight = true;

        using var registration = cancellationToken.Register(() => _bridge.Cancel(0));

        // History crosses as "role\u0001content"; the facade splits them back. The SDK does not
        // keep history, so the whole list goes on every request.
        var encoded = history.Select(m => $"{m.Role}\u0001{m.Text}").ToArray();
        if (maxTokens > 0)
        {
            _bridge.CompleteStreamingCapped(encoded, 0, maxTokens, this);
        }
        else
        {
            _bridge.CompleteStreaming(encoded, 0, this);
        }

        try
        {
            var settled = await Task.WhenAny(turn.Task, failure.Task).ConfigureAwait(false);
            var answer = settled == turn.Task ? await turn.Task.ConfigureAwait(false) : string.Empty;
            if (settled == failure.Task)
            {
                throw await failure.Task.ConfigureAwait(false);
            }

            return answer;
        }
        finally
        {
            _turnInFlight = false;
            _turn = null;
            _failure = null;
            _delta = null;
        }
    }

    /// <summary>Cancels the in-flight turn; the facade's own stop mechanism.</summary>
    public void Cancel() => _bridge.Cancel(0);

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _bridge.Close();
    }

    private TaskCompletionSource<string> ArmStage()
    {
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stage = signal;
        return signal;
    }

    // Called on the facade's IO dispatcher.
    public void OnChunk(string? text)
    {
        var value = text ?? string.Empty;
        LastProgress = $"chunk: {value}";
        if (value.Length == 0)
        {
            return;
        }

        if (_turnInFlight && _delta is { } delta)
        {
            _main.Post(() => delta(value));
            return;
        }

        _progress?.Invoke(value);
    }

    /// <summary>The last thing the facade reported, verbatim, for the evidence export.</summary>
    public string LastProgress { get; private set; } = "(none)";

    public void OnCompleted(string? text)
    {
        var value = text ?? string.Empty;
        LastProgress = $"completed: {value}";
        if (_turnInFlight && _turn is { Task.IsCompleted: false } turn)
        {
            turn.TrySetResult(value);
            return;
        }

        _stage?.TrySetResult(value);
        _stage = null;
    }

    public void OnFailed(int code, string? message, bool disconnected)
    {
        // `disconnected` is the facade's own hint. It is correct that the durable layer never sees
        // it: in embedded mode there is no service to disconnect from, which is why this variant's
        // offline claim is about the model not needing a network at all.
        _ = disconnected;
        Fail(Classify(code), message ?? "(no message)");
    }

    public void OnDisconnected() =>
        Fail(ChatFailureKind.Disconnected, "service disconnected mid-turn");

    private static ChatFailureKind Classify(int code) => code switch
    {
        FoundryBridge.CodeIncompatible => ChatFailureKind.ModelUnavailable,
        FoundryBridge.CodeDisconnected => ChatFailureKind.Disconnected,
        FoundryBridge.CodeModelMissing or FoundryBridge.CodeModelFailed => ChatFailureKind.ModelUnavailable,
        FoundryBridge.CodeStartFailed or FoundryBridge.CodeNotStarted or FoundryBridge.CodeNotReady
            => ChatFailureKind.TransportUnavailable,
        _ => ChatFailureKind.Unknown,
    };

    private void Fail(ChatFailureKind kind, string message)
    {
        _lastKind = kind;
        _lastError = message;
        LastProgress = $"failed [{kind}]: {message}";

        var exception = new BridgeException(kind, message);
        _failure?.TrySetResult(exception);
        _stage?.TrySetException(exception);
        _stage = null;
    }
}

internal sealed class BridgeException(ChatFailureKind kind, string message) : Exception(message)
{
    public ChatFailureKind Kind { get; } = kind;
}

/// <summary>
/// What the device says about itself, and where this app's private storage is.
/// </summary>
/// <remarks>
/// The paths come from Android (<c>FilesDir</c>, <c>GetExternalFilesDir</c>) because only Android
/// knows them; nothing above this type constructs a path. <see cref="DurableStorePath"/> is inside
/// <c>filesDir</c>, which is the app's private data directory: it survives the process being
/// killed, it survives the app being swiped away, and it is removed only by uninstalling the app or
/// clearing its data.
/// </remarks>
public sealed class DeviceProfile
{
    public required string Abi { get; init; }

    public required int Sdk { get; init; }

    public required string AppVersion { get; init; }

    /// <summary>The app-private store file. Its directory survives process death.</summary>
    public required string DurableStorePath { get; init; }

    /// <summary>Where the model binary is cached, app-private for the same reason as the store.</summary>
    /// <remarks>
    /// This is the SDK's own default, observed rather than chosen: FoundryLocalCore logs
    /// <c>Cache directory:/data/user/0/&lt;pkg&gt;/files/models_cache</c> on initialize, and the
    /// facade passes only an app name, so the location is the embedded SDK's. It is named here so the
    /// resource measurement can walk it and report the model's real size on disk; changing it would
    /// mean a <c>Configuration.modelCacheDir</c> in the facade, which this variant does not need.
    /// Keeping app-private storage also means the cache is not shared with any other app.
    /// </remarks>
    public required string ModelCachePath { get; init; }

    /// <summary>
    /// A debug copy of the durable state and the measurements, in app-external storage.
    /// </summary>
    /// <remarks>
    /// The store itself does <b>not</b> live here. This is a read-only export for capture: it is
    /// the only way to get the store's bytes off a release-signed app without a debuggable build or
    /// a rooted device, and an evidence file that quotes the store is worth more than one that
    /// asserts it. App-external storage is still removed when the app is uninstalled.
    /// </remarks>
    public string? ExportDirectory { get; init; }

    public static DeviceProfile Create(Context context)
    {
        var files = context.FilesDir!.AbsolutePath;
        var version = context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.VersionName ?? "unknown";

        return new DeviceProfile
        {
            Abi = Build.SupportedAbis is { Count: > 0 } abis ? abis[0] ?? "unknown" : "unknown",
            Sdk = (int)Build.VERSION.SdkInt,
            AppVersion = version,
            DurableStorePath = Path.Combine(files, "durable", "durable-store.jsonl"),
            ModelCachePath = Path.Combine(files, "models_cache"),
            ExportDirectory = context.GetExternalFilesDir(null)?.AbsolutePath,
        };
    }
}

/// <summary>
/// The platform services the host needs, behind an Android-free facade.
/// </summary>
/// </summary>
/// <remarks>
/// Every member here reaches for the platform's own objects (the application context, the package
/// manager, <c>Debug.MemoryInfo</c>) and hands back plain .NET values. That is what lets
/// <c>AppHost</c> build the object graph without containing an Android type name: the only file with
/// <c>using Android.*</c> is still this one.
/// </remarks>
public static class AppPlatform
{
    /// <summary>The process-wide application context.</summary>
    public static Context Application => global::Android.App.Application.Context;

    /// <summary>Describes this device and where its app-private storage is.</summary>
    public static DeviceProfile Profile() => DeviceProfile.Create(Application);
}

/// <summary>
/// Resource facts measured on the running device, never estimated.
/// </summary>
/// <remarks>
/// Every number here comes from a platform call at the moment it is read: <c>Debug.MemoryInfo</c>
/// for the process PSS, <c>ActivityManager.MemoryInfo</c> for system memory, <c>File.UsableSpace</c>
/// for free bytes, and a real directory walk for the model cache. Requirement 5 of the issue asks
/// for the honest cost of on-device, and a number this app guessed would not be that.
/// </remarks>
public static class DeviceResources
{
    /// <summary>This process's proportional set size in MB, per <c>Debug.MemoryInfo</c>.</summary>
    public static double ProcessPssMb(Context context)
    {
        var info = new Debug.MemoryInfo();
        Debug.GetMemoryInfo(info);
        return info.TotalPss / 1024.0;
    }

    /// <summary>(total, available) system memory in MB, per <c>ActivityManager</c>.</summary>
    public static (double TotalMb, double AvailableMb) SystemMemoryMb(Context context)
    {
        using var activity = (ActivityManager?)context.GetSystemService(Context.ActivityService);
        var info = new ActivityManager.MemoryInfo();
        activity?.GetMemoryInfo(info);
        return (info.TotalMem / 1024.0 / 1024.0, info.AvailMem / 1024.0 / 1024.0);
    }

    /// <summary>Free bytes on the volume that holds app-private storage.</summary>
    public static long FreeBytes(Context context) => context.FilesDir!.UsableSpace;

    /// <summary>A reading of everything the resource table needs, taken now.</summary>
    public static ResourceSnapshot Capture(Context context, string modelCachePath, params string[] extra)
    {
        var (total, available) = SystemMemoryMb(context);
        var listing = ListFiles(modelCachePath);
        return new ResourceSnapshot(
            CapturedAt: DateTimeOffset.UtcNow.ToString("O"),
            ProcessPssMb: ProcessPssMb(context),
            SystemTotalMb: total,
            SystemAvailableMb: available,
            FreeBytes: FreeBytes(context),
            ModelCacheBytes: listing.Sum(entry => entry.Bytes),
            ModelFileCount: listing.Count,
            Extra: extra);
    }

    /// <summary>A recursive listing of a directory: path and byte length, in stable order.</summary>
    public static IReadOnlyList<(string Path, long Bytes)> ListFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(path => (path, new FileInfo(path).Length))
                .ToList()
            : [];

    /// <summary>Total bytes of the files <see cref="ListFiles"/> would list.</summary>
    public static long DirectoryBytes(string directory) => ListFiles(directory).Sum(entry => entry.Bytes);
}
