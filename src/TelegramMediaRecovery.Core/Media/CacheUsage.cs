using TelegramMediaRecovery.Core.Cache;

namespace TelegramMediaRecovery.Core.Media;

/// <summary>The cache role encoded by Telegram, independently of the file's container or visual contents.</summary>
public enum CacheUsage
{
    Unknown,
    Photo,
    Document,
    StreamedMedia,
    ProfilePhoto,
    DocumentThumbnail,
    StickerSetThumbnail,
    AudioAlbumCover,
    Sticker,
}

internal static class CacheUsageClassifier
{
    // Telegram/SourceFiles/ui/image/image_location.{h,cpp}: StorageFileLocation::cacheKey().
    // Telegram/SourceFiles/data/data_types.{h,cpp}: Document(Thumb)CacheKey and cache tags.
    // Classify only recognized namespaces. In particular, the low type byte alone is insufficient:
    // URL/web cache keys contain hash bits there and can accidentally resemble a profile-photo key.
    internal static CacheUsage Classify(CacheKey key, byte tag, bool isStream, MediaFormat format)
    {
        if (isStream)
        {
            var type = (key.High >> 16) & 0xFF;
            if (type == 8 && (key.High & 0xFF00) != 0)
            {
                return CacheUsage.StickerSetThumbnail;
            }
            if (tag == 2 || format.Extension == "tgs") { return CacheUsage.Sticker; }
            return (key.High >> 16) is 1 or 2 ? CacheUsage.StreamedMedia : CacheUsage.Unknown;
        }

        if (key.Low != 0 && (key.High & 0xFF) != 0 && key.High <= uint.MaxValue)
        {
            var type = (key.High >> 8) & 0xFF;
            var sizeLetter = (key.High >> 16) & 0xFF;
            if ((key.High >> 8) == 9) { return CacheUsage.ProfilePhoto; }
            if ((key.High >> 8) == 2) { return CacheUsage.DocumentThumbnail; }
            if (type == 5 && (key.High >> 24) == 0 && sizeLetter is >= 'a' and <= 'z')
            {
                return CacheUsage.DocumentThumbnail;
            }
            if (type == 10) { return CacheUsage.StickerSetThumbnail; }
            if (tag == 2 || format.Extension == "tgs") { return CacheUsage.Sticker; }
            if (type == 8 && (key.High >> 24) == 0 && sizeLetter is >= 'a' and <= 'z')
            {
                return CacheUsage.Photo;
            }
            if ((key.High >> 8) == 1) { return CacheUsage.Document; }
        }
        if (key.High == 0x300 && key.Low != 0) { return CacheUsage.AudioAlbumCover; }
        if (tag == 2 || format.Extension == "tgs") { return CacheUsage.Sticker; }
        return CacheUsage.Unknown;
    }
}
