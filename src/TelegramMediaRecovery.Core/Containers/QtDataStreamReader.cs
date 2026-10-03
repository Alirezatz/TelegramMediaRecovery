using System.Buffers.Binary;

namespace TelegramMediaRecovery.Core.Containers;

/// <summary>
/// Minimal reader for the big-endian primitives of Qt's <c>QDataStream</c>, which
/// is what the <c>TDF$</c> containers are serialised with.
/// </summary>
/// <remarks>
/// Telegram Desktop never switches <c>QDataStream</c> out of its default
/// big-endian byte order, so every length prefix is a big-endian
/// <c>quint32</c>. A length of <c>0xFFFFFFFF</c> encodes a null
/// <c>QByteArray</c> and reads back as an empty one.
/// <para>
/// Reference: lib_storage <c>storage/details/storage_file_utilities.cpp</c>
/// &rarr; <c>ReadFile()</c> / <c>EncryptedDescriptor</c>.
/// </para>
/// </remarks>
public ref struct QtDataStreamReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public QtDataStreamReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>Current read cursor.</summary>
    public int Position => _position;

    /// <summary>Number of bytes still available.</summary>
    public int Remaining => _data.Length - _position;

    /// <summary>Reads one length-prefixed byte array (a <c>QByteArray</c>).</summary>
    public byte[] ReadByteArray()
    {
        if (Remaining < 4)
        {
            throw new InvalidDataException("Truncated QByteArray length prefix.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_position, 4));
        _position += 4;

        if (length == 0xFFFFFFFF)
        {
            return [];
        }

        if (length > Remaining)
        {
            throw new InvalidDataException(
                $"Truncated QByteArray: header says {length} bytes, {Remaining} available.");
        }

        var result = _data.Slice(_position, (int)length).ToArray();
        _position += (int)length;
        return result;
    }

    /// <summary>Reads a big-endian <c>quint32</c>.</summary>
    public uint ReadUInt32()
    {
        if (Remaining < 4)
        {
            throw new InvalidDataException("Truncated quint32.");
        }

        var value = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>Reads a big-endian <c>qint32</c>.</summary>
    public int ReadInt32()
    {
        if (Remaining < 4)
        {
            throw new InvalidDataException("Truncated qint32.");
        }

        var value = BinaryPrimitives.ReadInt32BigEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }
}
