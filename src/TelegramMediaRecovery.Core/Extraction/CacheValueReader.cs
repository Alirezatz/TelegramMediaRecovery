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
    private readonly LocalKey _key = key;
    private readonly IReadOnlyList<string> _versionDirectories = versionDirectories;
    private readonly Action<string>? _log = log;

    /// <summary>Number of lookups that had to fall back to another version folder.</summary>
    public int FallbackHits { get; private set; }

    /// <summary>Number of place files that could not be found at all.</summary>
    public int MissingFiles { get; private set; }

    /// <summary>Number of place files that existed but failed the checksum.</summary>
    public int ChecksumFailures { get; private set; }

    /// <summary>Number of place files that did not open with our local key.</summary>
    public int WrongKeyFiles { get; private set; }

    /// <summary>
    /// Reads the value of <paramref name="entry"/> and verifies its checksum.
    /// </summary>
    public ReadOnlyMemory<byte>? Read(CacheEntry entry)
    {
        if (entry.Size == 0)
        {
            return null;
        }

        var sawFile = false;

        for (var i = 0; i < _versionDirectories.Count; i++)
        {
            var relative = entry.Place.ToRelativePath().Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(_versionDirectories[i], relative);
            if (!File.Exists(path))
            {
                continue;
            }

            sawFile = true;

            byte[] value;
            try
            {
                using var file = TdefFile.Open(path, _key);
                if (file.PayloadSize < entry.Size)
                {
                    _log?.Invoke($"{Path.GetFileName(path)} is shorter than its binlog entry.");
                    continue;
                }

                value = file.Read(0, entry.Size);
            }
            catch (Containers.WrongKeyException)
            {
                WrongKeyFiles++;
                continue;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException
                                           or CryptographicException)
            {
                _log?.Invoke($"{Path.GetFileName(path)} could not be decrypted: {ex.Message}");
                continue;
            }

            if (XxHash32.Compute(value) != entry.Checksum)
            {
                ChecksumFailures++;
                continue;
            }

            if (i > 0)
            {
                FallbackHits++;
            }

            return value;
        }

        if (sawFile)
        {
            ChecksumFailures++;
        }
        else
        {
            MissingFiles++;
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
            WrongKeyFiles++;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException
                                       or CryptographicException)
        {
            _log?.Invoke($"{Path.GetFileName(path)} could not be decrypted: {ex.Message}");
        }

        return null;
    }
}
