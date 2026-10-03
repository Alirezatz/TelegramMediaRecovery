using System.IO.Compression;

namespace TelegramMediaRecovery.Core.Media;

/// <summary>
/// Identifies a cached value from its first bytes.
/// </summary>
/// <remarks>
/// Everything in the cache is stored as raw bytes with no metadata whatsoever: the
/// binlog only knows a key, a size and an XXH32 checksum. The only way to learn
/// what a file is (and therefore what to call it on disk) is to look at its magic
/// bytes, so this class carries a deliberately wide table &mdash; Telegram caches
/// whatever a user receives, not only photos and videos.
/// <para>
/// Magic numbers are written as byte lists rather than as <c>"..."u8</c> string
/// literals on purpose: in C# a <c>\xFF</c> escape denotes the Unicode code point
/// U+00FF, which UTF-8 encodes as <i>two</i> bytes, so <c>"\xff\xd8\xff"u8</c> is
/// six bytes long and silently never matches. ASCII magics such as
/// <c>"RIFF"u8</c> are unaffected and kept as literals.
/// </para>
/// </remarks>
public static class MediaSniffer
{
    private const int HeadBytes = 512;

    private static readonly MediaFormat Jpeg = new("jpg", MediaKind.Image, "image/jpeg");
    private static readonly MediaFormat Png = new("png", MediaKind.Image, "image/png");
    private static readonly MediaFormat Gif = new("gif", MediaKind.Image, "image/gif");
    private static readonly MediaFormat Bmp = new("bmp", MediaKind.Image, "image/bmp");
    private static readonly MediaFormat Webp = new("webp", MediaKind.Image, "image/webp");
    private static readonly MediaFormat Avif = new("avif", MediaKind.Image, "image/avif");
    private static readonly MediaFormat Heic = new("heic", MediaKind.Image, "image/heic");
    private static readonly MediaFormat Svg = new("svg", MediaKind.Image, "image/svg+xml");
    private static readonly MediaFormat Ico = new("ico", MediaKind.Image, "image/x-icon");
    private static readonly MediaFormat Tiff = new("tiff", MediaKind.Image, "image/tiff");
    private static readonly MediaFormat Mp4 = new("mp4", MediaKind.Video, "video/mp4");
    private static readonly MediaFormat Mov = new("mov", MediaKind.Video, "video/quicktime");
    private static readonly MediaFormat M4v = new("m4v", MediaKind.Video, "video/x-m4v");
    private static readonly MediaFormat ThreeGp = new("3gp", MediaKind.Video, "video/3gpp");
    private static readonly MediaFormat Webm = new("webm", MediaKind.Video, "video/webm");
    private static readonly MediaFormat Mkv = new("mkv", MediaKind.Video, "video/x-matroska");
    private static readonly MediaFormat Ogg = new("ogg", MediaKind.Audio, "audio/ogg");
    private static readonly MediaFormat Opus = new("opus", MediaKind.Audio, "audio/opus");
    private static readonly MediaFormat Mp3 = new("mp3", MediaKind.Audio, "audio/mpeg");
    private static readonly MediaFormat Flac = new("flac", MediaKind.Audio, "audio/flac");
    private static readonly MediaFormat Wav = new("wav", MediaKind.Audio, "audio/wav");
    private static readonly MediaFormat Aac = new("aac", MediaKind.Audio, "audio/aac");
    private static readonly MediaFormat M4a = new("m4a", MediaKind.Audio, "audio/mp4");
    private static readonly MediaFormat Tgs = new("tgs", MediaKind.Animation, "application/x-tgsticker");
    private static readonly MediaFormat Gz = new("gz", MediaKind.Document, "application/gzip");
    private static readonly MediaFormat Zip = new("zip", MediaKind.Document, "application/zip");
    private static readonly MediaFormat Pdf = new("pdf", MediaKind.Document, "application/pdf");
    private static readonly MediaFormat Ttf = new("ttf", MediaKind.Document, "font/ttf");
    private static readonly MediaFormat Otf = new("otf", MediaKind.Document, "font/otf");
    private static readonly MediaFormat Woff = new("woff", MediaKind.Document, "font/woff");
    private static readonly MediaFormat Woff2 = new("woff2", MediaKind.Document, "font/woff2");
    private static readonly MediaFormat SevenZ = new("7z", MediaKind.Document, "application/x-7z-compressed");
    private static readonly MediaFormat Rar = new("rar", MediaKind.Document, "application/vnd.rar");

    private static ReadOnlySpan<byte> JpegMagic => [(byte)0xFF, (byte)0xD8, (byte)0xFF];
    private static ReadOnlySpan<byte> PngMagic => [(byte)0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> TiffLeMagic => [0x49, 0x49, 0x2A, 0x00];
    private static ReadOnlySpan<byte> TiffBeMagic => [0x4D, 0x4D, 0x00, 0x2A];
    private static ReadOnlySpan<byte> IcoMagic => [0x00, 0x00, 0x01, 0x00];
    private static ReadOnlySpan<byte> SevenZMagic => [0x37, 0x7A, (byte)0xBC, (byte)0xAF, 0x27, 0x1C];
    private static ReadOnlySpan<byte> TrueTypeMagic => [0x00, 0x01, 0x00, 0x00];
    private static ReadOnlySpan<byte> EbmlMagic => [(byte)0x1A, 0x45, (byte)0xDF, (byte)0xA3];
    private static ReadOnlySpan<byte> GzipMagic => [0x1F, (byte)0x8B];
    private static ReadOnlySpan<byte> MpegPsMagic => [0x00, 0x00, 0x01, (byte)0xBA];
    private static ReadOnlySpan<byte> MpegVideoMagic => [0x00, 0x00, 0x01, (byte)0xB3];

    /// <summary>
    /// Returns the detected format, or <see cref="MediaFormat.Binary"/> when the
    /// head matches nothing known.
    /// </summary>
    /// <param name="head">
    /// The first bytes of the value. 512 is enough for every container listed
    /// here; callers should pass a slice rather than a whole file.
    /// </param>
    public static MediaFormat Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4)
        {
            return MediaFormat.Binary;
        }

        // --- RIFF containers: WebP, WAV, AVI -------------------------------
        if (head.Length >= 12 && StartsWith(head, "RIFF"u8))
        {
            if (Matches(head, 8, "WEBP"u8))
            {
                return Webp;
            }

            if (Matches(head, 8, "WAVE"u8))
            {
                return Wav;
            }

            if (Matches(head, 8, "AVI "u8))
            {
                return new MediaFormat("avi", MediaKind.Video, "video/x-msvideo");
            }

            return new MediaFormat("riff", MediaKind.Document, "application/octet-stream");
        }

        // --- ISO base media: MP4 / MOV / 3GP / AVIF / HEIC / M4A -------------
        if (head.Length >= 12 && Matches(head, 4, "ftyp"u8))
        {
            return DetectIsoBaseMedia(head.Slice(8, 4));
        }

        var simple = DetectSimple(head);
        if (simple is not null)
        {
            return simple.Value;
        }

        if (StartsWith(head, EbmlMagic))
        {
            return ContainsBytes(head, "webm"u8) ? Webm : Mkv;
        }

        if (StartsWith(head, GzipMagic))
        {
            return DetectGzip(head);
        }

        if (StartsWith(head, MpegPsMagic) || StartsWith(head, MpegVideoMagic))
        {
            return new MediaFormat("mpg", MediaKind.Video, "video/mpeg");
        }

        return LooksLikeSvg(head) ? Svg : MediaFormat.Binary;
    }

    private static MediaFormat? DetectSimple(ReadOnlySpan<byte> head)
    {
        if (StartsWith(head, JpegMagic))
        {
            return Jpeg;
        }

        if (StartsWith(head, PngMagic))
        {
            return Png;
        }

        if (StartsWith(head, "GIF87a"u8) || StartsWith(head, "GIF89a"u8))
        {
            return Gif;
        }

        if (head.Length >= 6 && Matches(head, 0, "BM"u8) && head[4] == 0 && head[5] == 0)
        {
            return Bmp;
        }

        if (StartsWith(head, TiffLeMagic) || StartsWith(head, TiffBeMagic))
        {
            return Tiff;
        }

        if (StartsWith(head, IcoMagic))
        {
            return Ico;
        }

        if (StartsWith(head, "OggS"u8))
        {
            return ContainsBytes(head, "OpusHead"u8) ? Opus : Ogg;
        }

        if (StartsWith(head, "ID3"u8))
        {
            return Mp3;
        }

        if (StartsWith(head, "fLaC"u8))
        {
            return Flac;
        }

        if (StartsWith(head, SevenZMagic))
        {
            return SevenZ;
        }

        if (StartsWith(head, "Rar!"u8))
        {
            return Rar;
        }

        if (StartsWith(head, "%PDF"u8))
        {
            return Pdf;
        }

        if (StartsWith(head, "PK\x03\x04"u8) || StartsWith(head, "PK\x05\x06"u8))
        {
            return Zip;
        }

        if (StartsWith(head, "OTTO"u8))
        {
            return Otf;
        }

        if (StartsWith(head, "wOFF"u8))
        {
            return Woff;
        }

        if (StartsWith(head, "wOF2"u8))
        {
            return Woff2;
        }

        if (head.Length >= 5 && StartsWith(head, TrueTypeMagic) && head[4] == 0)
        {
            return Ttf;
        }

        if (StartsWith(head, "moov"u8))
        {
            return Mp4;
        }

        // MPEG audio frame sync: 11 set bits, layer bits not zero.
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x06) != 0x00)
        {
            return Mp3;
        }

        // ADTS AAC frame sync.
        if (head[0] == 0xFF && (head[1] & 0xF6) == 0xF0)
        {
            return Aac;
        }

        return null;
    }

    private static MediaFormat DetectIsoBaseMedia(ReadOnlySpan<byte> brand)
    {
        if (brand.SequenceEqual("M4A "u8) || brand.SequenceEqual("M4B "u8) || brand.SequenceEqual("F4A "u8))
        {
            return M4a;
        }

        if (brand.SequenceEqual("qt  "u8))
        {
            return Mov;
        }

        if (brand.StartsWith("3gp"u8) || brand.StartsWith("3g2"u8))
        {
            return ThreeGp;
        }

        if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
        {
            return Avif;
        }

        if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
            || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8)
            || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("mif1"u8)
            || brand.SequenceEqual("msf1"u8))
        {
            return Heic;
        }

        if (brand.StartsWith("M4V"u8))
        {
            return M4v;
        }

        return Mp4;
    }

    /// <summary>
    /// Tells a Telegram <c>.tgs</c> sticker (gzipped Lottie JSON) apart from a
    /// real gzip archive by inflating the first few hundred bytes.
    /// </summary>
    private static MediaFormat DetectGzip(ReadOnlySpan<byte> head)
    {
        var inflated = TryInflate(head);
        if (inflated.Length > 0)
        {
            if (ContainsBytes(inflated, "\"tgs\""u8) || ContainsBytes(inflated, "\"layers\""u8)
                || ContainsBytes(inflated, "\"ip\":"u8) || ContainsBytes(inflated, "\"fr\":"u8)
                || ContainsBytes(inflated, "tgst"u8))
            {
                return Tgs;
            }

            if (ContainsBytes(inflated, "\"v\":"u8) && ContainsBytes(inflated, "\"fr\":"u8))
            {
                return Tgs;
            }
        }

        return Gz;
    }

    /// <summary>
    /// Inflates at most one KiB of a gzip member so detection stays cheap.
    /// </summary>
    /// <remarks>
    /// The deflate stream is decoded directly with <see cref="DeflateStream"/>
    /// instead of <see cref="GZipStream"/>: we only ever hold the first few
    /// hundred bytes of a cached value, so the gzip trailer (and its CRC) is
    /// truncated, and <see cref="GZipStream"/> would refuse to return anything.
    /// </remarks>
    private static ReadOnlySpan<byte> TryInflate(ReadOnlySpan<byte> head)
    {
        var offset = SkipGzipHeader(head);
        if (offset < 0)
        {
            return default;
        }

        try
        {
            using var input = new MemoryStream(head[offset..].ToArray());
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var buffer = new byte[1024];
            var read = deflate.Read(buffer, 0, buffer.Length);
            return read <= 0 ? default : buffer.AsSpan(0, read);
        }
        catch (InvalidDataException)
        {
            return default;
        }
    }

    /// <summary>Returns the offset of the deflate stream, or -1 when this is not gzip.</summary>
    private static int SkipGzipHeader(ReadOnlySpan<byte> head)
    {
        if (head.Length <= 10 || head[0] != 0x1F || head[1] != 0x8B || head[2] != 0x08)
        {
            return -1;
        }

        var flags = head[3];
        var offset = 10;

        if ((flags & 0x04) != 0)
        {
            if (offset + 2 > head.Length)
            {
                return -1;
            }

            offset += 2 + BitConverter.ToUInt16(head.Slice(offset, 2));
        }

        foreach (var mask in new byte[] { 0x08, 0x10 })
        {
            if ((flags & mask) == 0)
            {
                continue;
            }

            while (offset < head.Length && head[offset] != 0)
            {
                offset++;
            }

            offset++;
        }

        return offset >= head.Length || offset < 0 ? -1 : offset;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> head)
    {
        var window = head[..Math.Min(head.Length, HeadBytes)];
        return ContainsBytes(window, "<svg"u8)
               || (ContainsBytes(window, "<?xml"u8) && ContainsBytes(window, "svg"u8));
    }

    /// <summary>Substring test that works on spans of bytes.</summary>
    private static bool ContainsBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;

    private static bool StartsWith(ReadOnlySpan<byte> head, ReadOnlySpan<byte> magic) => Matches(head, 0, magic);

    private static bool Matches(ReadOnlySpan<byte> head, int offset, ReadOnlySpan<byte> magic) =>
        head.Length >= offset + magic.Length && head.Slice(offset, magic.Length).SequenceEqual(magic);

    /// <summary>Hex dump of the first bytes, used in the manifest for unknowns.</summary>
    public static string Describe(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0)
        {
            return "empty";
        }

        return Convert.ToHexString(head[..Math.Min(head.Length, 16)]).ToLowerInvariant();
    }

    /// <summary>True when the head begins with something that is clearly media.</summary>
    public static bool LooksLikeMedia(ReadOnlySpan<byte> head) => Detect(head).Kind != MediaKind.Document;
}
