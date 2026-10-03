namespace TelegramMediaRecovery.Core.Media;

/// <summary>
/// Constants of the streaming (sliced) media cache.
/// </summary>
/// <remarks>
/// Reference: <c>Telegram/SourceFiles/media/streaming/media_streaming_reader.cpp</c>
/// and <c>media_streaming_loader.cpp</c>:
/// <code>
/// constexpr auto kPartSize          = Loader::kPartSize;      // 128 KiB
/// constexpr auto kPartsInSlice     = 64;
/// constexpr auto kInSlice          = kPartsInSlice * kPartSize;   // 8 MiB
/// constexpr auto kMaxPartsInHeader = 64;
/// constexpr auto kMaxOnlyInHeader  = 80 * kPartSize;              // 10 MiB
/// constexpr auto kPartsOutsideFirstSliceGood = 8;
/// </code>
/// A streamed file lives at cache keys <c>base + sliceNumber</c>: slice 0 is the
/// "header" (the container metadata plus, for small files, the whole body) and
/// slice <c>n</c> holds file bytes <c>[(n-1) * kInSlice, n * kInSlice)</c>.
/// </remarks>
public static class StreamingLayout
{
    /// <summary>Size of one download part: 128 KiB.</summary>
    public const int PartSize = 128 * 1024;

    /// <summary>Parts per slice.</summary>
    public const int PartsInSlice = 64;

    /// <summary>Bytes per slice: 8 MiB.</summary>
    public const int InSlice = PartsInSlice * PartSize;

    /// <summary>Largest number of parts a header map may hold.</summary>
    public const int MaxPartsInHeader = 64;

    /// <summary>Files up to this size are stored entirely inside the header slice: 10 MiB.</summary>
    public const int MaxOnlyInHeader = 80 * PartSize;

    /// <summary>
    /// How many parts of a header map may live at or beyond
    /// <see cref="InSlice"/> while the header is still considered "good" (i.e.
    /// small enough that folding the first slice into it is fine).
    /// </summary>
    public const int PartsOutsideFirstSliceGood = 8;

    /// <summary>
    /// Highest number of slices a file may have; the client asserts
    /// <c>SlicesCount(size) &lt; 0x1FF</c>.
    /// </summary>
    public const int MaxSlices = 0x1FF;

    /// <summary>Rounds a byte count up to the next part boundary.</summary>
    public static int RoundUpToPart(long size) => (int)((size + PartSize - 1) / PartSize * PartSize);

    /// <summary>
    /// <c>MaxSliceSize(sliceNumber, size)</c> for a known total file size; used to
    /// bound the sanity checks of a complex map.
    /// </summary>
    public static long MaxSliceSize(int sliceNumber, long totalSize)
    {
        if (sliceNumber == 0)
        {
            return totalSize;
        }

        var slices = (totalSize + InSlice - 1) / InSlice;
        return sliceNumber == slices ? totalSize - ((sliceNumber - 1) * (long)InSlice) : InSlice;
    }

    /// <summary>
    /// <c>IsContiguousSerialization(serializedSize, maxSliceSize)</c>: a value is
    /// stored as plain bytes when its length is part aligned, or exactly fills
    /// its slice.
    /// </summary>
    public static bool IsContiguousSerialization(int serializedSize, long maxSliceSize) =>
        (serializedSize % PartSize) == 0 || serializedSize == maxSliceSize;

    /// <summary><c>ComputeIsGoodHeader(size, headerParts)</c>.</summary>
    public static bool ComputeIsGoodHeader(long totalSize, IReadOnlyCollection<int> headerPartOffsets)
    {
        if (totalSize <= MaxOnlyInHeader)
        {
            return false;
        }

        var outsideFirstSlice = 0;
        foreach (var offset in headerPartOffsets)
        {
            if (offset >= InSlice)
            {
                outsideFirstSlice++;
            }
        }

        return outsideFirstSlice <= PartsOutsideFirstSliceGood;
    }
}
