namespace TelegramMediaRecovery.Core.Media;

/// <summary>
/// Broad category a recovered file belongs to; drives the output folder name.
/// </summary>
public enum MediaKind
{
    /// <summary>Not recognised.</summary>
    Unknown,

    /// <summary>Still picture: JPEG, PNG, WebP, GIF, BMP, AVIF, HEIC, ...</summary>
    Image,

    /// <summary>Animation: Lottie <c>.tgs</c>, animated WebP/GIF.</summary>
    Animation,

    /// <summary>Video container.</summary>
    Video,

    /// <summary>Audio only.</summary>
    Audio,

    /// <summary>Documents, fonts, archives, anything else that is not media.</summary>
    Document,
}
