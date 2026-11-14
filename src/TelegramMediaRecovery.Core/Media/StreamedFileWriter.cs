using System.Buffers;
using System.Security.Cryptography;
using TelegramMediaRecovery.Core.Cache;

namespace TelegramMediaRecovery.Core.Media;

/// <summary>Writes verified ranges with at most one source value in memory, then hashes the actual output.</summary>
public static class StreamedFileWriter
{
    public static string Write(StreamedFilePlan plan, string destination, CancellationToken cancellationToken = default, bool computeHash = true)
    {
        var created = false;
        try
        {
            using var stream = Create(destination);
            created = true;
            CopyTo(plan, stream, cancellationToken);
            return computeHash ? Hash(stream, cancellationToken) : string.Empty;
        }
        catch
        {
            if (created) { TryDelete(destination); }
            throw;
        }
    }

    internal static StreamedWriteResult Recover(IEnumerable<CacheEntry> entries, Func<CacheEntry, CacheValueLease?> open,
        string destination, long minimumSize, CancellationToken cancellationToken)
    {
        using var stream = Create(destination);
        var plan = StreamedFilePlanner.BuildVerifiedPlan(entries, open, (part, bytes) =>
        {
            stream.Position = part.Offset;
            stream.Write(bytes.Span);
        }, cancellationToken).WithMinimumSize(minimumSize);
        if (plan.Fragments.Count == 0 || plan.CachedBytes == 0)
        {
            throw new InvalidDataException("The cached fragments are no longer readable. Scan the data source again.");
        }
        stream.SetLength(plan.FileSize);
        return new StreamedWriteResult(plan, Hash(stream, cancellationToken));
    }

    internal static void CopyTo(StreamedFilePlan plan, Stream output, CancellationToken token)
    {
        output.SetLength(plan.FileSize);
        foreach (var group in plan.Ranges.GroupBy(p => p.Fragment))
        {
            token.ThrowIfCancellationRequested();
            using var value = group.Key.Open();
            foreach (var part in group)
            {
                token.ThrowIfCancellationRequested();
                output.Position = part.Offset;
                output.Write(value.Memory.Span.Slice(part.ValueOffset, part.Length));
            }
        }
    }

    private static FileStream Create(string destination) => new(destination, FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.RandomAccess);

    private static void TryDelete(string destination)
    {
        try { File.Delete(destination); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Hash(Stream stream, CancellationToken token)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

internal sealed record StreamedWriteResult(StreamedFilePlan Plan, string Sha256);
