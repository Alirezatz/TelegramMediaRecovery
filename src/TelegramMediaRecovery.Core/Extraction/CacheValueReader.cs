using System.Buffers;
using System.Security.Cryptography;
using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Containers;
using TelegramMediaRecovery.Core.Crypto;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Core.Extraction;

/// <summary>
/// Resolves a cache entry to its decrypted bytes, trying every known cache
/// version folder and verifying the XXH32 checksum before handing data back.
/// </summary>
/// <remarks>
/// The client only ever writes into the version folder named by the
/// <c>version</c> marker, but older folders survive after a compaction, and a
/// place id from a stale binlog can still point at a file that was never
/// overwritten. Trying each folder and checking the checksum turns "probably
/// right" into "provably right", which matters because the alternative is writing
/// 80 000 files of noise.
/// </remarks>
public sealed class CacheValueReader(
    LocalKey key,
    IReadOnlyList<string> versionDirectories,
    Action<string>? log = null)
{
    private static readonly ArrayPool<byte> Buffers = ArrayPool<byte>.Create(16 * 1024 * 1024, 2);
    private readonly LocalKey _key = key;
    private readonly IReadOnlyList<string> _versionDirectories = versionDirectories;
    private readonly Action<string>? _log = log;

    /// <summary>Number of lookups that had to fall back to another version folder.</summary>
    private int _fallbackHits;
    public int FallbackHits => _fallbackHits;

    /// <summary>Number of place files that could not be found at all.</summary>
    private int _missingFiles;
    public int MissingFiles => _missingFiles;

    /// <summary>Number of place files that existed but failed the checksum.</summary>
    private int _checksumFailures;
    public int ChecksumFailures => _checksumFailures;

    /// <summary>Number of place files that did not open with our local key.</summary>
    private int _wrongKeyFiles;
    public int WrongKeyFiles => _wrongKeyFiles;

    /// <summary>
    /// Reads the value of <paramref name="entry"/> and verifies its checksum.
    /// </summary>
    public ReadOnlyMemory<byte>? Read(CacheEntry entry)
    {
        using var value = Open(entry);
        if (value is null) { return null; }
        return value.Memory.ToArray();
    }

    /// <summary>Verifies a value in pooled memory. Dispose before reading another large value.</summary>
    public CacheValueLease? Open(CacheEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.Size == 0)
        {
            return null;
        }

        var sawFile = false;

        var relative = entry.Place.ToRelativePath().Replace('/', Path.DirectorySeparatorChar);
        for (var i = 0; i < _versionDirectories.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_versionDirectories[i], relative);
            if (!File.Exists(path))
            {
                continue;
            }

            sawFile = true;

            var buffer = Buffers.Rent(entry.Size);
            var transferred = false;
            try
            {
                using var file = TdefFile.Open(path, _key);
                if (file.PayloadSize < entry.Size)
                {
                    _log?.Invoke($"{Path.GetFileName(path)} is shorter than its binlog entry.");
                    continue;
                }

                file.Read(0, buffer.AsSpan(0, entry.Size), cancellationToken);
                if (XxHash32.Compute(buffer.AsSpan(0, entry.Size)) != entry.Checksum)
                {
                    Interlocked.Increment(ref _checksumFailures);
                    continue;
                }
                if (i > 0) { Interlocked.Increment(ref _fallbackHits); }
                transferred = true;
                return new CacheValueLease(buffer.AsMemory(0, entry.Size), () => Buffers.Return(buffer, clearArray: true));
            }
            catch (Containers.WrongKeyException)
            {
                Interlocked.Increment(ref _wrongKeyFiles);
                continue;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException
                                           or CryptographicException)
            {
                _log?.Invoke($"{Path.GetFileName(path)} could not be decrypted: {ex.Message}");
                continue;
            }

            finally
            {
                if (!transferred) { Buffers.Return(buffer, clearArray: true); }
            }
        }

        if (sawFile)
        {
            Interlocked.Increment(ref _checksumFailures);
        }
        else
        {
            Interlocked.Increment(ref _missingFiles);
        }

        return null;
    }

    /// <summary>Reads an arbitrary place file without checksum verification.</summary>
    /// <remarks>
    /// Used by the orphan scan: a file that no binlog mentions has no checksum to
    /// check, so only the TDEF header hash (inside <c>TdefFile.Open</c>) proves the
    /// bytes are ours.
    /// </remarks>
    public byte[]? TryReadRaw(string path, int maximumSize)
    {
        try
        {
            using var file = TdefFile.Open(path, _key);
            var length = (int)Math.Min(file.PayloadSize, maximumSize);
            return length <= 0 ? null : file.Read(0, length);
        }
        catch (Containers.WrongKeyException)
        {
            Interlocked.Increment(ref _wrongKeyFiles);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException
                                       or CryptographicException)
        {
            _log?.Invoke($"{Path.GetFileName(path)} could not be decrypted: {ex.Message}");
        }

        return null;
    }
}
