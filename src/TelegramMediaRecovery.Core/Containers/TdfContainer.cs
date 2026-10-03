using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TelegramMediaRecovery.Core.Containers;

/// <summary>
/// Reader and writer for the <c>TDF$</c> container used by the small
/// <c>tdata</c> bookkeeping files (<c>key_datas</c>, <c>settingss</c>,
/// <c>maps</c>, <c>configs</c>, ...).
/// </summary>
/// <remarks>
/// Layout (lib_storage <c>storage/details/storage_file_utilities.cpp</c> &rarr;
/// <c>ReadFile()</c> / <c>FileWriteDescriptor::finish()</c>):
/// <code>
/// [0:4]   "TDF$"
/// [4:8]   int32 LE   AppVersion
/// [8:-16] payload     (QDataStream, big-endian lengths)
/// [-16:]  MD5( payload || int32le(payloadLen) || int32le(version) || "TDF$" )
/// </code>
/// </remarks>
public sealed class TdfContainer
{
    /// <summary>The four magic bytes every container starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "TDF$"u8;

    /// <summary>Length of the trailing MD5 signature.</summary>
    public const int SignatureSize = 16;

    private const int HeaderSize = 8;

    private TdfContainer(int version, byte[] payload)
    {
        Version = version;
        Payload = payload;
    }

    /// <summary>The application version the file was written by.</summary>
    public int Version { get; }

    /// <summary>The QDataStream-serialised payload.</summary>
    public byte[] Payload { get; }

    /// <summary>Reads and validates a container from disk.</summary>
    /// <exception cref="InvalidDataException">Bad magic or bad MD5 signature.</exception>
    public static TdfContainer Read(string path)
    {
        var raw = File.ReadAllBytes(path);

        if (raw.Length < HeaderSize + SignatureSize || !raw.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException($"Not a TDF$ container: {path}");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4, 4));
        var payload = raw.AsSpan(HeaderSize, raw.Length - HeaderSize - SignatureSize).ToArray();
        var signature = raw.AsSpan(raw.Length - SignatureSize, SignatureSize);
        var expected = ComputeSignature(payload, version);

        if (!CryptographicOperations.FixedTimeEquals(signature, expected))
        {
            throw new InvalidDataException($"Bad MD5 signature: {path}");
        }

        return new TdfContainer(version, payload);
    }

    /// <summary>Serialises a payload into a container (used by the tests).</summary>
    public static byte[] Write(int version, ReadOnlySpan<byte> payload)
    {
        var result = new byte[HeaderSize + payload.Length + SignatureSize];
        Magic.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4, 4), version);
        payload.CopyTo(result.AsSpan(HeaderSize, payload.Length));
        ComputeSignature(payload, version).CopyTo(result, HeaderSize + payload.Length);
        return result;
    }

    private static byte[] ComputeSignature(ReadOnlySpan<byte> payload, int version)
    {
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
        var versionBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(versionBytes, version);

        using var md5 = MD5.Create();
        md5.TransformBlock(payload.ToArray(), 0, payload.Length, null, 0);
        md5.TransformBlock(lengthBytes, 0, 4, null, 0);
        md5.TransformBlock(versionBytes, 0, 4, null, 0);
        md5.TransformFinalBlock(Magic.ToArray(), 0, Magic.Length);
        return md5.Hash!;
    }
}
