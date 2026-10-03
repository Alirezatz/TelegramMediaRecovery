using System.Globalization;

namespace TelegramMediaRecovery.Core;

/// <summary>
/// Parses and formats human-readable byte sizes such as <c>4KB</c> or <c>1.5MB</c>.
/// </summary>
public static class ByteSize
{
    private static readonly (string[] Suffixes, long Multiplier)[] Units =
    [
        (["gb", "g"], 1L << 30),
        (["mb", "m"], 1L << 20),
        (["kb", "k"], 1L << 10),
        (["b"], 1),
    ];

    /// <summary>Parses a size; returns <c>false</c> when the text is not a size.</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().ToLowerInvariant();

        foreach (var (suffixes, multiplier) in Units)
        {
            foreach (var suffix in suffixes)
            {
                if (value.EndsWith(suffix, StringComparison.Ordinal))
                {
                    value = value[..^suffix.Length].Trim();
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    {
                        bytes = number * multiplier;
                        return true;
                    }
                }
            }
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes);
    }

    /// <summary>Formats a byte count with a binary unit.</summary>
    public static string Format(long bytes)
    {
        string[] names = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < names.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {names[unit]}");
    }
}
