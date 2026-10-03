namespace TelegramMediaRecovery.Core.Media;

/// <summary>
/// A recognised container format: extension, category and MIME type.
/// </summary>
/// <param name="Extension">Lower-case extension without the leading dot.</param>
/// <param name="Kind">Category used for the output folder.</param>
/// <param name="MimeType">MIME type reported in the manifest.</param>
public readonly record struct MediaFormat(string Extension, MediaKind Kind, string MimeType)
{
    /// <summary>The fallback used for unrecognised content.</summary>
    public static MediaFormat Binary { get; } = new("bin", MediaKind.Document, "application/octet-stream");

    /// <inheritdoc />
    public override string ToString() => $"{Extension} ({Kind})";
}
