using System.Windows.Media.Imaging;
using TelegramMediaRecovery.Core;
using TelegramMediaRecovery.Core.Browsing;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Desktop.ViewModels;

public sealed class MediaItemViewModel(RecoveryItem item) : ObservableObject
{
    private bool _isChecked;
    private WeakReference<BitmapSource>? _thumbnail;
    public RecoveryItem Item { get; } = item;
    public string Name => Item.Name;
    public long Bytes => Item.Size;
    public string Size => ByteSize.Format(Bytes);
    public DateTimeOffset Date => Item.Date;
    public DateTime LocalDate { get; } = item.Date.LocalDateTime.Date;
    public string DateLabel => Date.Year < 2000 ? "Unknown date" : Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Kind => KindName(Item.Format.Kind);
    public string Extension => Item.Format.Extension.ToUpperInvariant();
    public string Source => $"{Item.Account} · {(Item.IsStream ? "streaming cache" : "media cache")}";
    public string Usage => UsageName(Item.Usage);
    public string UsageDescription => Item.Usage switch
    {
        CacheUsage.ProfilePhoto => "The cache key identifies a user, group or channel avatar. It may be a photo or a logo; visual contents are not classified.",
        CacheUsage.DocumentThumbnail => "The cache key identifies a document or video thumbnail or preview cover.",
        CacheUsage.StickerSetThumbnail => "The cache key identifies a sticker set thumbnail.",
        CacheUsage.AudioAlbumCover => "The cache key identifies an audio album cover.",
        CacheUsage.Sticker => "The sticker tag or TGS format identifies static stickers, animated stickers or custom emoji.",
        CacheUsage.Photo => "The cache key identifies a photo, possibly one of several size variants. Its source message is not linked.",
        CacheUsage.Document => "The cache key identifies a media file rather than a document thumbnail. Logos sent as files can still appear here.",
        CacheUsage.StreamedMedia => "Streaming photo or document data. The original file's completeness remains unconfirmed.",
        _ => "No reliable usage marker was found. Small or square images are retained for manual inspection.",
    };
    public string State => Item.HasGaps ? "Gaps detected" : Item.IsStream ? "Integrity unknown" : "Cache verified";
    internal CacheState StateKind { get; } = item.HasGaps ? CacheState.Gaps : item.IsStream ? CacheState.Unconfirmed : CacheState.Verified;
    internal string SearchText { get; } = $"{item.Name} {KindName(item.Format.Kind)} {UsageName(item.Usage)} {item.Format.Extension} {item.Id}";
    public string Icon => Item.Format.Kind switch
    {
        MediaKind.Image => "IMG",
        MediaKind.Video => "VID",
        MediaKind.Audio => "AUD",
        MediaKind.Animation => "TGS",
        _ => "FILE",
    };
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public BitmapSource? Thumbnail => _thumbnail is not null && _thumbnail.TryGetTarget(out var bitmap) ? bitmap : null;
    public void SetThumbnail(BitmapSource bitmap)
    {
        _thumbnail = new WeakReference<BitmapSource>(bitmap);
        Raise(nameof(Thumbnail));
    }
    public static string KindName(MediaKind kind) => kind switch
    {
        MediaKind.Image => "Images",
        MediaKind.Video => "Videos",
        MediaKind.Audio => "Audio",
        MediaKind.Animation => "Animated stickers",
        _ => "Documents",
    };
    public static string UsageName(CacheUsage usage) => usage switch
    {
        CacheUsage.Photo or CacheUsage.Document or CacheUsage.StreamedMedia => "Media files",
        CacheUsage.ProfilePhoto => "Avatars / icons",
        CacheUsage.DocumentThumbnail or CacheUsage.StickerSetThumbnail or CacheUsage.AudioAlbumCover => "Thumbnails / covers",
        CacheUsage.Sticker => "Stickers / emoji",
        _ => "Unknown usage",
    };
}
