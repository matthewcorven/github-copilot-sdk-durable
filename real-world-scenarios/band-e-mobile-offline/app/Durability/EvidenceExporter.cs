using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Durable;

namespace BandEMobile.Durability;

/// <summary>A resource reading taken at one moment on the device, ready to be written down.</summary>
/// <param name="CapturedAt">When it was read, ISO-8601 UTC.</param>
/// <param name="ProcessPssMb">This process's PSS, from <c>Debug.MemoryInfo.TotalPss</c>.</param>
/// <param name="SystemTotalMb">System memory, from <c>ActivityManager.MemoryInfo.TotalMem</c>.</param>
/// <param name="SystemAvailableMb">Available memory, same source.</param>
/// <param name="FreeBytes">Free bytes on the volume holding app-private storage.</param>
/// <param name="ModelCacheBytes">Bytes under the model cache directory, from a directory walk.</param>
/// <param name="ModelFileCount">Files under the model cache directory.</param>
/// <param name="Extra">Free-form lines the caller wants recorded verbatim.</param>
public sealed record ResourceSnapshot(
    string CapturedAt,
    double ProcessPssMb,
    double SystemTotalMb,
    double SystemAvailableMb,
    long FreeBytes,
    long ModelCacheBytes,
    int ModelFileCount,
    IReadOnlyList<string> Extra);

/// <summary>
/// Writes the durable state and the resource readings to a directory a shell can read.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the store lives in app-private storage, and a release-signed app on a
/// non-debuggable emulator cannot be read with <c>adb</c> (verified: <c>run-as</c> reports "package
/// not debuggable", and <c>/data/data/...</c> is permission denied). Rather than call the store's
/// survival an assertion, the app exports the same bytes to a path the capture scripts can walk.
/// </para>
/// <para>
/// It is <b>not</b> a second source of truth. The store is the durable record; this is a copy of it,
/// with the SHA-256 of the copy and of the store printed next to each other so a reader can check
/// they are the same bytes. Nothing here is producible without the store having been written.
/// </para>
/// <para>
/// The export directory arrives as a string that <c>Chat/DeviceProfile</c> obtained from the
/// platform, so this file takes a path and writes files — nothing here knows what produced it.
/// </para>
/// </remarks>
public static class EvidenceExporter
{
    /// <summary>
    /// Exports the store, a readable transcript, the document versions, and the resources.
    /// </summary>
    /// <returns>The directory written, or null when the app has no export directory.</returns>
    public static string? Export(
        string? exportRoot,
        string storePath,
        SessionSnapshot snapshot,
        IReadOnlyList<DocumentVersion> versions,
        ResourceSnapshot? resources,
        string tag,
        string? bridgeProgress = null,
        IReadOnlyList<string>? status = null)
    {
        if (exportRoot is null)
        {
            return null;
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var directory = Path.Combine(exportRoot, "e2-export", $"{stamp}-{tag}");
        Directory.CreateDirectory(directory);

        var storeSha = "(store file absent)";
        var storeBytes = 0L;
        if (File.Exists(storePath))
        {
            var bytes = File.ReadAllBytes(storePath);
            storeBytes = bytes.Length;
            storeSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            File.WriteAllBytes(Path.Combine(directory, "durable-store.jsonl"), bytes);
        }

        var summary = new StringBuilder();
        summary.AppendLine("# Band E / E2 — durable state exported by the running app");
        summary.AppendLine($"exported_at: {DateTimeOffset.UtcNow:O}");
        summary.AppendLine($"tag: {tag}");
        summary.AppendLine($"store_path: {storePath}");
        summary.AppendLine($"store_bytes: {storeBytes}");
        summary.AppendLine($"store_sha256: {storeSha}");
        summary.AppendLine($"persistent: true");
        if (bridgeProgress is not null)
        {
            summary.AppendLine($"bridge_last_progress: {bridgeProgress.ReplaceLineEndings(" ")}");
        }
        if (status is { Count: > 0 })
        {
            summary.AppendLine("status:");
            foreach (var line in status)
            {
                summary.AppendLine("  " + line.ReplaceLineEndings(" "));
            }
        }
        summary.AppendLine();

        summary.AppendLine($"## transcript ({snapshot.Turns.Count} turns)");
        foreach (var turn in snapshot.Turns)
        {
            summary.AppendLine($"turn {turn.Index}: {turn.Request.ReplaceLineEndings(" ")}");
            summary.AppendLine($"  reply: {turn.Reply?.ReplaceLineEndings(" ") ?? "(none)"}");
            if (turn.Note is not null)
            {
                summary.AppendLine($"  note: {turn.Note}");
            }
            if (turn.ElapsedMs is { } elapsed)
            {
                summary.AppendLine($"  elapsed_ms: {elapsed}   chunks: {turn.ChunkCount}");
            }
        }
        summary.AppendLine();

        summary.AppendLine($"## document — revision {snapshot.Document.Revision}, " +
                           $"updated by turn {snapshot.Document.UpdatedByTurn?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        summary.AppendLine($"title: {snapshot.Document.Title}");
        summary.AppendLine("body:");
        summary.AppendLine(snapshot.Document.Body);
        summary.AppendLine();
        summary.AppendLine($"## committed document versions ({versions.Count})");
        foreach (var version in versions)
        {
            var state = DurableJson.FromElement<ArtifactState>(version.Value);
            summary.AppendLine($"seq {version.Sequence}  entry {version.EntryId}  rev {state?.Revision}  " +
                               $"{state?.Body.Length ?? 0} chars");
        }
        summary.AppendLine();

        summary.AppendLine($"## interrupted turn tasks ({snapshot.Interrupted.Count})");
        foreach (var checkpoint in snapshot.Interrupted)
        {
            summary.AppendLine($"turn {checkpoint.TurnIndex}  {checkpoint.Phase}  started {checkpoint.StartedAt}  " +
                               $"request: {checkpoint.Request.ReplaceLineEndings(" ")}");
        }

        if (resources is not null)
        {
            summary.AppendLine();
            summary.AppendLine("## resources");
            summary.AppendLine($"captured_at: {resources.CapturedAt}");
            summary.AppendLine($"process_pss_mb: {resources.ProcessPssMb:F1}   (Debug.MemoryInfo.TotalPss / 1024)");
            summary.AppendLine($"system_mem_total_mb: {resources.SystemTotalMb:F0}   (ActivityManager.MemoryInfo.TotalMem)");
            summary.AppendLine($"system_mem_available_mb: {resources.SystemAvailableMb:F0}   (ActivityManager.MemoryInfo.AvailMem)");
            summary.AppendLine($"files_dir_free_bytes: {resources.FreeBytes}   (File.UsableSpace)");
            summary.AppendLine($"model_cache_bytes: {resources.ModelCacheBytes}   ({resources.ModelFileCount} files, directory walk)");
            foreach (var line in resources.Extra)
            {
                summary.AppendLine(line);
            }
        }

        File.WriteAllText(Path.Combine(directory, "session.txt"), summary.ToString());
        return directory;
    }
}
