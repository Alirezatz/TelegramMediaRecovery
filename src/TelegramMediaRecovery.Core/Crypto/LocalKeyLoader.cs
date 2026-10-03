using System.Security.Cryptography;
using TelegramMediaRecovery.Core.Containers;

namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// Locates and unwraps the local key of a <c>tdata</c> folder.
/// </summary>
/// <remarks>
/// Two on-disk shapes are supported:
/// <list type="bullet">
///   <item><description>
///     <b>Modern (multi-account)</b> &mdash; <c>tdata/key_datas</c> is a
///     <c>TDF$</c> container holding three length-prefixed blobs:
///     <c>salt(32) || keyEncrypted || infoEncrypted</c>. <c>keyEncrypted</c> is a
///     local blob wrapped with the modern passcode key and decrypts to the
///     256-byte (or 64-byte) local key.
///   </description></item>
///   <item><description>
///     <b>Legacy (pre multi-account)</b> &mdash; the key lives inside the map file
///     of a <c>tdata/&lt;16 hex chars&gt;/</c> directory
///     (<c>maps</c>, <c>map0</c>, <c>map1</c> or <c>map</c>), which holds
///     <c>legacySalt(32) || legacyKeyEncrypted || mapEncrypted</c> and is wrapped
///     with the legacy (PBKDF2-HMAC-SHA1) passcode key.
///   </description></item>
/// </list>
/// Reference: lib_storage <c>storage/storage_domain.cpp</c> &rarr;
/// <c>Storage::Domain::startModern()</c>, and
/// <c>storage/storage_account.cpp</c> &rarr;
/// <c>Storage::Account::readMapWith()</c>.
/// </remarks>
public static class LocalKeyLoader
{
    /// <summary>Names tried for the modern key container.</summary>
    private static readonly string[] ModernKeyFileNames = ["key_datas", "key_data"];

    /// <summary>Names tried for the legacy map file.</summary>
    private static readonly string[] LegacyMapFileNames = ["maps", "map0", "map1", "map"];

    /// <summary>Derives the local key for a <c>tdata</c> folder.</summary>
    /// <param name="tdataPath">The <c>tdata</c> folder.</param>
    /// <param name="passcode">The local passcode, or an empty string when none is set.</param>
    /// <param name="diagnostics">Optional sink for human-readable notes.</param>
    /// <exception cref="LocalBlobCipher.WrongPasscodeException">The passcode is wrong.</exception>
    /// <exception cref="InvalidDataException">No key file could be read.</exception>
    public static LocalKey Load(string tdataPath, string passcode, Action<string>? diagnostics = null)
    {
        var notes = diagnostics ?? (_ => { });

        foreach (var path in EnumerateModernKeyFiles(tdataPath))
        {
            try
            {
                var container = TdfContainer.Read(path);
                var reader = new QtDataStreamReader(container.Payload);

                var salt = reader.ReadByteArray();
                var keyBlob = reader.ReadByteArray();
                _ = reader.ReadByteArray(); // infoEncrypted, not needed

                var passcodeKey = PasscodeKeyDerivation.DeriveModern(passcode, salt);
                var localKey = new LocalKey(LocalBlobCipher.Decrypt(keyBlob, passcodeKey));

                notes($"Local key loaded from {Path.GetFileName(path)}.");
                return localKey;
            }
            catch (LocalBlobCipher.WrongPasscodeException)
            {
                throw new LocalBlobCipher.WrongPasscodeException(
                    $"Wrong passcode: {Path.GetFileName(path)} did not decrypt.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                notes($"Skipped {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        foreach (var path in EnumerateLegacyMapFiles(tdataPath))
        {
            try
            {
                var container = TdfContainer.Read(path);
                var reader = new QtDataStreamReader(container.Payload);

                var salt = reader.ReadByteArray();
                var keyBlob = reader.ReadByteArray();
                _ = reader.ReadByteArray(); // mapEncrypted, not needed

                var passcodeKey = PasscodeKeyDerivation.DeriveLegacy(passcode, salt);
                var localKey = new LocalKey(LocalBlobCipher.Decrypt(keyBlob, passcodeKey));

                notes($"Local key loaded from the legacy map file {Path.GetFileName(path)}.");
                return localKey;
            }
            catch (LocalBlobCipher.WrongPasscodeException)
            {
                throw new LocalBlobCipher.WrongPasscodeException(
                    $"Wrong passcode: the legacy map file {Path.GetFileName(path)} did not decrypt.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                notes($"Skipped {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        throw new InvalidDataException(
            "No readable key file was found. Is this really a Telegram Desktop / 64Gram "
            + "tdata folder (it must contain key_datas or a <hash>/maps file)?");
    }

    private static IEnumerable<string> EnumerateModernKeyFiles(string tdataPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in ModernKeyFileNames)
        {
            var path = Path.Combine(tdataPath, name);
            if (File.Exists(path) && seen.Add(path))
            {
                yield return path;
            }
        }

        // A custom -key=<name> launch argument produces e.g. "key_custom".
        if (!Directory.Exists(tdataPath))
        {
            yield break;
        }

        foreach (var entry in Directory.EnumerateFiles(tdataPath, "key_*").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith("key_temp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(entry))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<string> EnumerateLegacyMapFiles(string tdataPath)
    {
        if (!Directory.Exists(tdataPath))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(tdataPath).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 16 || !IsHex(name))
            {
                continue;
            }

            foreach (var mapName in LegacyMapFileNames)
            {
                var path = Path.Combine(directory, mapName);
                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Cheap sanity probe: does <paramref name="localKey"/> open the given file?
    /// </summary>
    public static bool CanOpen(string path, LocalKey localKey)
    {
        try
        {
            using var file = TdefFile.Open(path, localKey);
            return file.PayloadSize >= 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or WrongKeyException
                                       or CryptographicException)
        {
            return false;
        }
    }
}
