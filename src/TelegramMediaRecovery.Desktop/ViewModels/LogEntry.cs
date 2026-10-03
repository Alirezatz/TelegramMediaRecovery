using TelegramMediaRecovery.Core.Extraction;

namespace TelegramMediaRecovery.Desktop.ViewModels;

/// <summary>Severity of one log line.</summary>
public enum LogLevel
{
    /// <summary>Ordinary progress information.</summary>
    Info,

    /// <summary>Something worth noticing.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,

    /// <summary>Per-file detail, hidden unless verbose mode is on.</summary>
    Detail,

    /// <summary>A finished stage, rendered in bold.</summary>
    Success,
}

/// <summary>One line in the activity log.</summary>
/// <param name="Timestamp">When the line was produced.</param>
/// <param name="Level">Severity.</param>
/// <param name="Message">Text.</param>
public readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    /// <summary>Time formatted for display.</summary>
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>Text formatted for display.</summary>
    public string Text => $"{Time}  {Message}";
}

/// <summary>One row of the "what was found in each database" table.</summary>
/// <param name="Database">Account and database folder.</param>
/// <param name="Version">Cache version folder number.</param>
/// <param name="Entries">Live keys in the binlog.</param>
/// <param name="Files">Files recovered.</param>
/// <param name="Size">Bytes written.</param>
/// <param name="Warnings">Notes, joined for display.</param>
public readonly record struct DatabaseSummary(
    string Database,
    int Version,
    long Entries,
    long Files,
    long Size,
    string Warnings);
