using System.Security.Cryptography;
using TelegramMediaRecovery.Core.Crypto;

namespace TelegramMediaRecovery.Core.Containers;

/// <summary>
/// Raised when a TDEF file does not belong to the local key we hold, i.e. its
/// header SHA256 check failed.
/// </summary>
public sealed class WrongKeyException : Exception
{
    public WrongKeyException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Streaming reader for the encrypted TDEF container that holds every cache value
/// and every binlog of a <c>tdata</c> folder.
/// </summary>
/// <remarks>
/// Layout, from lib_storage <c>storage/storage_encrypted_file.cpp</c> &rarr;
/// <c>File::readHeader()</c>:
/// <code>
/// [0:4]     "TDEF"          (this is also the base::FileLock::kSkipBytes region)
/// [4:68]    salt, 64 bytes, stored in clear
/// [68:116]  encrypted header: uint32 format | uint32 reserved
///                            | uint64 appVersion | 32-byte SHA256 checksum
///                            (48 bytes = 3 CTR blocks)
/// [116:..]  encrypted payload, CTR counter continuing from block 3
/// </code>
/// The header checksum is <c>SHA256(localKey || salt || format || reserved ||
/// appVersion)</c>. Checking it is what lets the tool tell "this file is mine"
/// from "this file is noise" before writing anything to disk.
/// <para>
/// The payload length on disk is always rounded up to a 16-byte multiple; the
/// surplus bytes are random padding added by <c>File::writeWithPadding()</c>.
/// </para>
/// </remarks>
public sealed class TdefFile : IDisposable
{
    private const int SaltSize = StorageEncryptionKey.SaltSize;
    private const int HeaderRestSize = 48;
    private const long PayloadOffset = 4 + SaltSize + HeaderRestSize;
    private const long HeaderBlocks = 3;

    private readonly FileStream _stream;
    private readonly byte[] _ctrKey;
    private readonly byte[] _baseIv;
    private bool _disposed;

    private TdefFile(FileStream stream, byte[] ctrKey, byte[] baseIv, long payloadSize)
    {
        _stream = stream;
        _ctrKey = ctrKey;
        _baseIv = baseIv;
        PayloadSize = payloadSize;
    }

    /// <summary>Path of the opened file.</summary>
    public string Path => _stream.Name;

    /// <summary>
    /// True when a running client still holds this file open. Telegram Desktop
    /// locks exactly the first four bytes (the <c>TDEF</c> magic) with
    /// <c>LockFile</c> for as long as a database is open, so everything we need
    /// &mdash; the salt at offset 4, the encrypted header and the payload &mdash;
    /// stays readable and a recovery run does not require quitting the app first.
    /// Only the magic check is skipped in that case.
    /// </summary>
    public bool IsLocked { get; private init; }

    /// <summary>Number of payload bytes physically stored (block aligned).</summary>
    public long PayloadSize { get; }

    /// <summary>
    /// Opens a TDEF file and validates its header against <paramref name="key"/>.
    /// </summary>
    /// <exception cref="WrongKeyException">The local key does not open this file.</exception>
    public static TdefFile Open(string path, LocalKey key)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.RandomAccess);

        try
        {
            var locked = false;
            var magic = new byte[4];
            try
            {
                stream.ReadExactly(magic, 0, magic.Length);
                if (!magic.AsSpan().SequenceEqual("TDEF"u8))
                {
                    throw new InvalidDataException($"Missing TDEF magic: {path}");
                }
            }
            catch (IOException)
            {
                // The four magic bytes are locked by a live client; the rest is
                // readable, so carry on without the check.
                locked = true;
            }

            var salt = new byte[SaltSize];
            stream.Seek(4, SeekOrigin.Begin);
            stream.ReadExactly(salt, 0, salt.Length);

            var encryptedHeader = new byte[HeaderRestSize];
            stream.ReadExactly(encryptedHeader, 0, encryptedHeader.Length);

            var crypto = new StorageEncryptionKey(key);
            var ctrKey = crypto.DeriveCtrKey(salt);
            var baseIv = crypto.DeriveCtrIv(salt);

            var header = DecryptWithCtr(ctrKey, baseIv, firstBlock: 0, encryptedHeader, copy: true);

            var storedChecksum = header.AsSpan(16, 32);
            var expectedChecksum = SHA256.HashData(Concat(key.Span, salt, header.AsSpan(0, 16)));
            if (!CryptographicOperations.FixedTimeEquals(storedChecksum, expectedChecksum))
            {
                throw new WrongKeyException(
                    $"TDEF header checksum mismatch: {path} was not written with this local key.");
            }

            var payloadSize = stream.Length - PayloadOffset;
            if (payloadSize < 0 || (payloadSize % 16) != 0)
            {
                throw new InvalidDataException(
                    $"TDEF payload of {path} is {payloadSize} bytes, which is not block aligned.");
            }

            return new TdefFile(stream, ctrKey, baseIv, payloadSize) { IsLocked = locked };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads the whole payload into memory.</summary>
    public byte[] ReadAll()
    {
        var result = new byte[PayloadSize];
        if (result.Length > 0)
        {
            _stream.Position = PayloadOffset;
            ReadExactly(result);
            DecryptWithCtr(_ctrKey, _baseIv, HeaderBlocks, result);
        }

        return result;
    }

    /// <summary>
    /// Reads <paramref name="length"/> logical bytes starting at
    /// <paramref name="offset"/> inside the payload and drops the block padding.
    /// </summary>
    public byte[] Read(long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length == 0 || offset >= PayloadSize)
        {
            return [];
        }

        var available = PayloadSize - offset;
        var wanted = (int)Math.Min((long)((length + 15) & ~15L), available);

        var buffer = new byte[wanted];
        _stream.Seek(PayloadOffset + offset, SeekOrigin.Begin);
        ReadExactly(buffer);
        DecryptWithCtr(_ctrKey, _baseIv, HeaderBlocks + (offset / 16), buffer);

        return wanted == length ? buffer : buffer[..length];
    }

    /// <summary>
    /// Decrypts into a caller-supplied buffer. The buffer length must be a
    /// multiple of 16 and the range must fit inside <see cref="PayloadSize"/>.
    /// </summary>
    public void Read(long offset, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination.Length == 0)
        {
            return;
        }

        if ((destination.Length % 16) != 0)
        {
            throw new ArgumentException("Destination length must be a multiple of 16.", nameof(destination));
        }

        if (offset < 0 || offset + destination.Length > PayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Range {offset}+{destination.Length} exceeds the {PayloadSize}-byte payload.");
        }

        _stream.Seek(PayloadOffset + offset, SeekOrigin.Begin);
        ReadExactly(destination);
        DecryptWithCtr(_ctrKey, _baseIv, HeaderBlocks + (offset / 16), destination);
    }

    private void ReadExactly(byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = _stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of TDEF file after {total} of {buffer.Length} bytes.");
            }

            total += read;
        }
    }

    private static byte[] DecryptWithCtr(
        byte[] ctrKey,
        byte[] baseIv,
        long firstBlock,
        byte[] buffer,
        bool copy = false)
    {
        if (buffer.Length == 0)
        {
            return buffer;
        }

        var target = copy ? (byte[])buffer.Clone() : buffer;
        using var cipher = new AesCtrCipher(ctrKey, baseIv);
        cipher.Transform(target, firstBlock);
        return target;
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
