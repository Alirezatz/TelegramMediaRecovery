namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// The two 64-bit halves of a cache key, in the exact order the C++ struct lays
/// them out in memory.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/cache/storage_cache_types.h</c> &rarr;
/// <c>struct Key { uint64 high = 0; uint64 low = 0; }</c>.
/// <para>
/// For the streaming (<c>media_cache</c>) database the key is derived from the
/// document and then incremented per slice:
/// <c>Key{ baseKey.high, baseKey.low + sliceNumber }</c>
/// (<c>media_streaming_reader.cpp</c> &rarr; <c>Reader::CacheHelper::key()</c>).
/// Slice 0 is the file header, slice <c>n</c> holds file bytes
/// <c>[(n-1) * kInSlice, n * kInSlice)</c>. That is the whole basis of
/// <see cref="Media.StreamedFilePlanner"/>.
/// </para>
/// <para>
/// For the plain <c>cache</c> database every key holds one self-contained value
/// (a photo, a GIF, a sticker, a small video).
/// </para>
/// </remarks>
public readonly struct CacheKey : IEquatable<CacheKey>, IComparable<CacheKey>
{
    public CacheKey(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    /// <summary>First 64-bit word of the struct (low memory address).</summary>
    public ulong High { get; }

    /// <summary>Second 64-bit word of the struct.</summary>
    public ulong Low { get; }

    /// <summary>Matches <c>Key::valid()</c>: a key of all zeros is never stored.</summary>
    public bool IsValid => High != 0 || Low != 0;

    /// <summary>Returns the same key with <paramref name="delta"/> added to <see cref="Low"/>.</summary>
    public CacheKey WithLowDelta(ulong delta) => new(High, Low + delta);

    /// <inheritdoc />
    public bool Equals(CacheKey other) => High == other.High && Low == other.Low;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(High, Low);

    /// <inheritdoc />
    public int CompareTo(CacheKey other)
    {
        var byHigh = High.CompareTo(other.High);
        return byHigh != 0 ? byHigh : Low.CompareTo(other.Low);
    }

    /// <inheritdoc />
    public override string ToString() => $"{High:x16}:{Low:x16}";

    /// <summary>Value equality.</summary>
    public static bool operator ==(CacheKey left, CacheKey right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(CacheKey left, CacheKey right) => !left.Equals(right);

    /// <summary>Ascending order by <see cref="High"/> then <see cref="Low"/>.</summary>
    public static bool operator <(CacheKey left, CacheKey right) => left.CompareTo(right) < 0;

    /// <summary>Ascending order by <see cref="High"/> then <see cref="Low"/>.</summary>
    public static bool operator >(CacheKey left, CacheKey right) => left.CompareTo(right) > 0;
}
