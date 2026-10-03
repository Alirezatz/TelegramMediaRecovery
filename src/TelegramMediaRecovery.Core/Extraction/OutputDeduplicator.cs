using System.Collections.Concurrent;

namespace TelegramMediaRecovery.Core.Extraction;

/// <summary>
/// Thread-safe de-duplication of recovered content.
/// </summary>
/// <remarks>
/// Identical bytes appear many times in a cache: a photo cached at several sizes,
/// a sticker re-downloaded, a video stored in both <c>cache</c> and
/// <c>media_cache</c>. Keying on the SHA-256 keeps the output folder small and
/// stops the same image from being written five times.
/// </remarks>
public sealed class OutputDeduplicator
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    /// <summary>Number of duplicates suppressed so far.</summary>
    public int Duplicates { get; private set; }

    /// <summary>Number of distinct contents written.</summary>
    public int Unique { get; private set; }

    /// <summary>Returns <c>true</c> the first time a digest is seen.</summary>
    public bool TryAdd(string? digest)
    {
        if (string.IsNullOrEmpty(digest))
        {
            // Hashing disabled: nothing can be compared, so keep everything.
            return true;
        }

        if (_seen.TryAdd(digest, 0))
        {
            Unique++;
            return true;
        }

        Duplicates++;
        return false;
    }

    /// <summary>Pre-loads digests from a previous run so resuming works.</summary>
    public void Seed(IEnumerable<string> digests)
    {
        foreach (var digest in digests)
        {
            if (!string.IsNullOrEmpty(digest))
            {
                _seen.TryAdd(digest, 0);
            }
        }
    }
}
