using System.Security.Cryptography;
using TelegramMediaRecovery.Core.Containers;
using TelegramMediaRecovery.Core.Crypto;

namespace TelegramMediaRecovery.Core.Cache;

/// <summary>One shared index replay policy for browsing and CLI recovery. The current generation wins.</summary>
public static class CacheIndexReader
{
    public static CacheIndexSnapshot Read(CacheDatabase database, LocalKey key, bool includeOldVersions = true,
        int maxEntrySize = CacheRecordLayout.MaxEntrySize, CancellationToken cancellationToken = default)
    {
        var versions = database.Versions().Take(includeOldVersions ? int.MaxValue : 1).ToArray();
        var merged = new Dictionary<CacheKey, CacheEntry>();
        var warnings = new List<string>();
        var records = 0;
        var liveCount = 0;
        var trackTime = false;
        for (var index = 0; index < versions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = versions[index];
            try
            {
                var parsed = CacheBinlogReader.ReadFile(version.BinlogPath, key, version.Number, maxEntrySize);
                records += parsed.RecordsRead;
                if (index == 0) { liveCount = parsed.Entries.Count; trackTime = parsed.Header.TrackEstimatedTime; }
                if (parsed.Truncated || parsed.RejectedRecords > 0)
                {
                    warnings.Add($"version {version.Number}: {parsed.RejectedRecords} rejected records; index stopped at {parsed.StoppedAtOffset}/{parsed.PayloadLength} bytes.");
                }
                foreach (var pair in parsed.Entries) { merged.TryAdd(pair.Key, pair.Value); }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or WrongKeyException or CryptographicException)
            {
                warnings.Add($"version {version.Number}: {ex.Message}");
            }
        }
        if (versions.Length == 0) { warnings.Add("no binlog in any version folder"); }
        return new CacheIndexSnapshot([.. merged.Values], versions.Select(v => v.VersionPath).ToArray(),
            versions.Length == 0 ? -1 : versions[0].Number, trackTime, records, liveCount, warnings);
    }
}

public sealed record CacheIndexSnapshot(IReadOnlyList<CacheEntry> Entries, IReadOnlyList<string> VersionDirectories,
    int LiveVersion, bool TrackEstimatedTime, int RecordsRead, int LiveEntryCount, IReadOnlyList<string> Warnings);
