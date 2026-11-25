using BandEMobile.Chat;
using Durable;
using BandEMobile.Durability;

namespace BandEMobile;

/// <summary>
/// The app's object graph, built once per process, with no Android type in it.
/// </summary>
/// <remarks>
/// <para>
/// The platform is reached through <see cref="Chat.Platform"/>, whose members take no Android types
/// and return plain .NET values. So this file — the one that wires the store, the transport, and the
/// page together — does not contain a single <c>Android</c> identifier, and neither does anything it
/// builds. The whole of the app's Android surface is still <c>Chat/BridgeShim.cs</c>, which
/// <c>AppHost.Evidence.cs</c> re-checks on every build.
/// </para>
/// <para>
/// One instance per process on purpose. The store holds an exclusive lock on its file and the
/// embedded engine is a process-global singleton, so a second host in the same process would fail the
/// second turn instead of helping it.
/// </para>
/// </remarks>
public sealed class AppHost : IAsyncDisposable
{
    private AppHost(BandEDurable durable, FoundryLocalChatClient chat, BridgeShim shim, DeviceProfile profile)
    {
        Durable = durable;
        ChatClient = chat;
        _shim = shim;
        Profile = profile;
    }

    private readonly BridgeShim _shim;

    /// <summary>The one host for this process, created by <see cref="Create"/>.</summary>
    public static AppHost Current { get; private set; } = null!;

    public BandEDurable Durable { get; private set; }

    public IChatClient ChatClient { get; private set; }

    /// <summary>Where everything lives on this device, for the status area and the evidence export.</summary>
    public DeviceProfile Profile { get; }

    /// <summary>Lines the app wants on screen and in the export: paths, timings, failures.</summary>
    public List<string> Status { get; } = [];

    /// <summary>The most recent resource reading, if one has been taken.</summary>
    public ResourceSnapshot? LastResources { get; private set; }

    /// <summary>Creates the host. Safe to call once per process.</summary>
    public static AppHost Create()
    {
        var profile = AppPlatform.Profile();

        Directory.CreateDirectory(Path.GetDirectoryName(profile.DurableStorePath)!);
        Directory.CreateDirectory(profile.ModelCachePath);

        var durable = BandEDurable.Open(profile.DurableStorePath);
        var shim = new BridgeShim(AppPlatform.Application, "BandEMobile");
        var host = new AppHost(durable, new FoundryLocalChatClient(shim), shim, profile);

        host.Status.Add($"abi {profile.Abi} · API {profile.Sdk} · app {profile.AppVersion}");
        host.Status.Add($"store {profile.DurableStorePath} · persistent {durable.IsPersistent}");
        host.Status.Add($"model cache {profile.ModelCachePath}");
        host.Status.Add($"export {(profile.ExportDirectory ?? "(absent)")}");

        Current = host;
        return host;
    }

    /// <summary>
    /// The turn whose checkpoint is still waiting, if any.
    /// </summary>
    /// <remarks>
    /// A turn's task is created before the model is asked anything and finished only by the commit
    /// that stores the reply. So a task still in <c>running</c>/<c>waiting</c> whose turn index has no
    /// transcript entry is a turn that was interrupted — including the one currently streaming, which
    /// is why the page compares against the transcript rather than trusting the task alone.
    /// </remarks>
    public DurableTaskRecord? WaitingTurnTask(int turnIndex) =>
        Durable.WaitingTurnTask(turnIndex);

    /// <summary>Replaces the chat transport. Used to make a turn fail on demand in the UI.</summary>
    public void SetChatClient(IChatClient client) => ChatClient = client;

    /// <summary>Takes a resource reading now. Every number is measured by the platform; none is estimated.</summary>
    public ResourceSnapshot CaptureResources(params string[] extra)
    {
        LastResources = DeviceResources.Capture(AppPlatform.Application, Profile.ModelCachePath, extra);
        return LastResources;
    }

    /// <summary>
    /// Appends a line to <c>e2-trace.log</c> in the export directory, flushed immediately.
    /// </summary>
    /// <remarks>
    /// This is the app's own log of what it did. It exists because the on-device story has to be
    /// reproducible from a shell: a release build on this emulator exposes no readable stdout, and a
    /// demonstration whose steps are only visible on screen cannot be checked against the store. The
    /// file is flushed per line so a process killed mid-turn still leaves the lines up to the kill.
    /// </remarks>
    public void Trace(string line)
    {
        Status.Add(line);
        if (Status.Count > 200)
        {
            Status.RemoveAt(0);
        }

        if (Profile.ExportDirectory is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(Profile.ExportDirectory, "e2-export");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "e2-trace.log");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {line}");
            writer.Flush();
        }
        catch (Exception)
        {
            // A trace that cannot be written must not take the app down with it.
        }
    }

    /// <summary>What the bridge reported last: the chunk, completion, or failure text.</summary>
    public string LastBridgeProgress => _shim.LastProgress;

    /// <summary>Bytes and file count in the model cache right now, without touching the last reading.</summary>
    public (long Bytes, int Files) MeasureModelCache()
    {
        var listing = DeviceResources.ListFiles(Profile.ModelCachePath);
        return (listing.Sum(entry => entry.Bytes), listing.Count);
    }

    /// <summary>
    /// A short fingerprint of the store file: its byte length and the first 12 hex digits of its
    /// SHA-256.
    /// </summary>
    /// <remarks>
    /// Shown on screen and written into every export. It is what ties a screenshot to a store: the
    /// evidence file quotes the full hash next to the store's bytes, and the screen quotes the prefix,
    /// so "the screen and the file are the same store" is checkable rather than asserted.
    /// </remarks>
    public string StoreFingerprint()
    {
        try
        {
            if (!File.Exists(Profile.DurableStorePath))
            {
                return "store (absent)";
            }

            var bytes = File.ReadAllBytes(Profile.DurableStorePath);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..12];
            return $"store {bytes.Length} B sha256:{hash}";
        }
        catch (Exception ex)
        {
            return $"store unreadable ({ex.GetType().Name})";
        }
    }

    /// <summary>The per-file model cache listing, for the size evidence.</summary>
    public IReadOnlyList<(string Path, long Bytes)> ModelCacheListing() =>
        DeviceResources.ListFiles(Profile.ModelCachePath);

    /// <summary>Writes the store and the current state to the export directory.</summary>
    public string? ExportEvidence(string tag = "snapshot")
    {
        var snapshot = Durable.ReadSession();
        return EvidenceExporter.Export(
            Profile.ExportDirectory,
            Profile.DurableStorePath,
            snapshot,
            Durable.ArtifactVersions,
            LastResources ?? CaptureResources(),
            tag,
            LastBridgeProgress,
            Status);
    }

    public async ValueTask DisposeAsync()
    {
        _shim.Close();
        await Durable.DisposeAsync().ConfigureAwait(false);
    }
}
