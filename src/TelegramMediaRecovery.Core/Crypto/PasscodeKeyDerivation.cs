using System.Security.Cryptography;
using System.Text;

namespace TelegramMediaRecovery.Core.Crypto;

/// <summary>
/// Turns a user passcode into the 256-byte "passcode key" that wraps the local key.
/// </summary>
/// <remarks>
/// Two derivations exist and both are implemented:
/// <list type="bullet">
///   <item><description>
///     <b>Modern</b> &mdash; <c>CreateLocalKey()</c>, used by every account-based
///     layout (<c>tdata/key_datas</c>):
///     <c>PBKDF2-HMAC-SHA512( password = SHA512(salt | passcode | salt),
///     salt = salt, iterations = 100000 or 1 when there is no passcode,
///     dkLen = 256 )</c>.
///   </description></item>
///   <item><description>
///     <b>Legacy</b> &mdash; <c>CreateLegacyLocalKey()</c>, used by the
///     pre-multi-account <c>tdata/&lt;md5-dir&gt;/maps</c> layout:
///     plain <c>PBKDF2-HMAC-SHA1(passcode, salt, 4000 or 4 iterations, dkLen = 256)</c>.
///   </description></item>
/// </list>
/// Reference: lib_storage <c>storage/details/storage_file_utilities.cpp</c>,
/// constants <c>LocalEncryptSaltSize = 32</c>, <c>kStrongIterationsCount = 100000</c>,
/// <c>LocalEncryptIterCount = 4000</c>, <c>LocalEncryptNoPwdIterCount = 4</c>.
/// </remarks>
public static class PasscodeKeyDerivation
{
    /// <summary>Length of the per-install salt.</summary>
    public const int SaltSize = 32;

    /// <summary>Iteration count used by the modern derivation.</summary>
    public const int StrongIterations = 100_000;

    /// <summary>Iteration count used by the legacy derivation when a passcode is set.</summary>
    public const int LegacyIterations = 4_000;

    /// <summary>Iteration count used by the legacy derivation when no passcode is set.</summary>
    public const int LegacyNoPasswordIterations = 4;

    /// <summary>Derives the modern passcode key.</summary>
    public static byte[] DeriveModern(string passcode, ReadOnlySpan<byte> salt)
    {
        ValidateSalt(salt);

        var password = Encoding.UTF8.GetBytes(passcode ?? string.Empty);
        var inner = SHA512.HashData(Concat(salt, password, salt));
        var iterations = password.Length == 0 ? 1 : StrongIterations;

        return Rfc2898DeriveBytes.Pbkdf2(inner, salt, iterations, HashAlgorithmName.SHA512, LocalKey.LegacyKeySize);
    }

    /// <summary>Derives the legacy (pre-multi-account) passcode key.</summary>
    public static byte[] DeriveLegacy(string passcode, ReadOnlySpan<byte> salt)
    {
        ValidateSalt(salt);

        var password = Encoding.UTF8.GetBytes(passcode ?? string.Empty);
        var iterations = password.Length == 0 ? LegacyNoPasswordIterations : LegacyIterations;

        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA1, LocalKey.LegacyKeySize);
    }

    private static void ValidateSalt(ReadOnlySpan<byte> salt)
    {
        if (salt.Length != SaltSize)
        {
            throw new ArgumentException($"Passcode salt must be {SaltSize} bytes, got {salt.Length}.", nameof(salt));
        }
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }
}
