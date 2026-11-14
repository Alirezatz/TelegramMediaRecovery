using TelegramMediaRecovery.Core;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Desktop.ViewModels;

internal enum UsageChoice { All, ExcludeAuxiliary, Main, Avatar, Thumbnail, Sticker, Unknown }
internal enum CacheState { Verified, Unconfirmed, Gaps }

/// <summary>Parses UI choices once, then evaluates a typed, allocation-free predicate for every row.</summary>
internal sealed class MediaFilter
{
    private MediaKind? _kind;
    private UsageChoice _usage;
    private CacheState? _state;
    private string _query = string.Empty;
    private long _minimum;
    private long _maximum;
    private DateTime? _from;
    private DateTime? _to;
    public string Error { get; private set; } = string.Empty;

    public void Update(string kind, string usage, string state, string query, string minimum, string maximum, DateTime? from, DateTime? to)
    {
        _kind = kind switch
        {
            "Images" => MediaKind.Image,
            "Videos" => MediaKind.Video,
            "Animated stickers" => MediaKind.Animation,
            "Audio" => MediaKind.Audio,
            "Documents" => MediaKind.Document,
            _ => null
        };
        _usage = usage switch
        {
            "Hide avatars and thumbnails" => UsageChoice.ExcludeAuxiliary,
            "Media files" => UsageChoice.Main,
            "Avatars / icons" => UsageChoice.Avatar,
            "Thumbnails / covers" => UsageChoice.Thumbnail,
            "Stickers / emoji" => UsageChoice.Sticker,
            "Unknown usage" => UsageChoice.Unknown,
            _ => UsageChoice.All
        };
        _state = state switch { "Cache verified" => CacheState.Verified, "Integrity unknown" => CacheState.Unconfirmed, "Gaps detected" => CacheState.Gaps, _ => null };
        _query = query.Trim();
        _minimum = _maximum = 0;
        _from = from?.Date; _to = to?.Date;
        var validMin = string.IsNullOrWhiteSpace(minimum) || ByteSize.TryParse(minimum, out _minimum) && _minimum >= 0;
        var validMax = string.IsNullOrWhiteSpace(maximum) || ByteSize.TryParse(maximum, out _maximum) && _maximum >= 0;
        Error = !validMin || !validMax ? "Invalid size. Use a value such as 4KB or 20MB."
            : _maximum > 0 && _minimum > _maximum ? "Minimum size cannot exceed maximum size."
            : _from > _to ? "The start date cannot be after the end date." : string.Empty;
    }

    public bool Matches(MediaItemViewModel item)
    {
        if (Error.Length > 0 || _kind.HasValue && item.Item.Format.Kind != _kind || _state.HasValue && item.StateKind != _state) { return false; }
        var avatar = item.Item.Usage == CacheUsage.ProfilePhoto;
        var thumbnail = item.Item.Usage is CacheUsage.DocumentThumbnail or CacheUsage.StickerSetThumbnail or CacheUsage.AudioAlbumCover;
        if (!(_usage switch
        {
            UsageChoice.ExcludeAuxiliary => !avatar && !thumbnail,
            UsageChoice.Main => item.Item.Usage is CacheUsage.Photo or CacheUsage.Document or CacheUsage.StreamedMedia,
            UsageChoice.Avatar => avatar,
            UsageChoice.Thumbnail => thumbnail,
            UsageChoice.Sticker => item.Item.Usage == CacheUsage.Sticker,
            UsageChoice.Unknown => item.Item.Usage == CacheUsage.Unknown,
            _ => true,
        })) { return false; }
        if (item.Bytes < _minimum || _maximum > 0 && item.Bytes > _maximum) { return false; }
        if (_from.HasValue && item.LocalDate < _from || _to.HasValue && item.LocalDate > _to) { return false; }
        return _query.Length == 0 || item.SearchText.Contains(_query, StringComparison.OrdinalIgnoreCase);
    }
}
