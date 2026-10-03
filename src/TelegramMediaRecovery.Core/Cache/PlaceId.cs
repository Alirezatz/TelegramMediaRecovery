namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// The 7-byte identifier that decides where a value is stored on disk.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/cache/storage_cache_types.h</c>
/// (<c>using PlaceId = std::array&lt;uint8, 7&gt;;</c>) and
/// <c>storage/cache/storage_cache_database_object.cpp</c> &rarr;
/// <c>PlaceFromId()</c>. Each byte is written <b>low nibble first</b> and a
/// directory separator is inserted after the first byte, producing the familiar
/// two-character bucket directories (<c>00</c>..<c>FF</c>) inside a cache
/// version folder.
/// </remarks>
public readonly struct PlaceId : IEquatable<PlaceId>
{
    /// <summary>Number of bytes in a place id.</summary>
    public const int Size = 7;

    private readonly byte[]? _bytes;

    public PlaceId(byte[] bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A place id is exactly {Size} bytes.", nameof(bytes));
        }

        _bytes = bytes;
    }

    private static readonly byte[] Zero = new byte[Size];

    /// <summary>Raw bytes of the place id.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes ?? Zero;

    /// <summary>All-zero (never stored) place id.</summary>
    public static PlaceId Empty => new(Zero);

    /// <summary>
    /// Renders the relative on-disk path, e.g. <c>21/EDBDA9F65F91</c>.
    /// </summary>
    public string ToRelativePath()
    {
        Span<char> buffer = stackalloc char[Size * 2 + 1];
        var span = Bytes;
        var length = 0;

        for (var i = 0; i < Size; i++)
        {
            buffer[length++] = HexDigit(span[i] & 0x0F);
            buffer[length++] = HexDigit(span[i] >> 4);
            if (i == 0)
            {
                buffer[length++] = '/';
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>Formats the place id as a single hex string (no separator).</summary>
    public string ToHex()
    {
        Span<char> buffer = stackalloc char[Size * 2];
        var span = Bytes;
        for (var i = 0; i < Size; i++)
        {
            buffer[i * 2] = HexDigit(span[i] >> 4);
            buffer[(i * 2) + 1] = HexDigit(span[i] & 0x0F);
        }

        return new string(buffer);
    }

    /// <summary>Parses the form produced by <see cref="ToHex"/>.</summary>
    public static PlaceId FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != Size * 2)
        {
            throw new ArgumentException($"Expected {Size * 2} hex characters.", nameof(hex));
        }

        var bytes = new byte[Size];
        for (var i = 0; i < Size; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return new PlaceId(bytes);
    }

    private static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));

    /// <inheritdoc />
    public bool Equals(PlaceId other) => Bytes.SequenceEqual(other.Bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PlaceId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Bytes[0] << 8 | Bytes[1];

    /// <inheritdoc />
    public override string ToString() => ToHex();

    /// <summary>Value equality.</summary>
    public static bool operator ==(PlaceId left, PlaceId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(PlaceId left, PlaceId right) => !left.Equals(right);
}
