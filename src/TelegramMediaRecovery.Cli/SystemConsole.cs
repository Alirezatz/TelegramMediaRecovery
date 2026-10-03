using TelegramMediaRecovery.Core.Extraction;

namespace TelegramMediaRecovery.Cli;

/// <summary>Thin wrapper over the console so output can be captured in tests.</summary>
public sealed class SystemConsole
{
    /// <summary>Writes a normal line to stdout.</summary>
    public void Info(string message) => Out(message);

    /// <summary>Writes a warning to stderr.</summary>
    public void Warn(string message) => Error($"warning: {message}");

    /// <summary>Writes an error to stderr.</summary>
    public void Error(string message) => Console.Error.WriteLine(message);

    /// <summary>Writes a debug line to stderr when <c>TMR_VERBOSE</c> is set.</summary>
    public void Debug(string message)
    {
        if (Environment.GetEnvironmentVariable("TMR_VERBOSE") is "1" or "true")
        {
            Console.Error.WriteLine($"  {message}");
        }
    }

    /// <summary>Writes raw text to stdout.</summary>
    public void Out(string message) => Console.Out.WriteLine(message);
}

/// <summary>Prints a single updating progress line.</summary>
public sealed class ConsoleProgress : IProgress<ExtractionProgress>
{
    private int _lastPercentage = -1;

    /// <inheritdoc />
    public void Report(ExtractionProgress value)
    {
        var percent = value.DatabasesTotal == 0
            ? 0
            : (int)(100.0 * value.DatabasesDone / value.DatabasesTotal);
        if (percent == _lastPercentage)
        {
            return;
        }

        _lastPercentage = percent;
        Console.Error.Write(
            $"\r  [{percent,3}%] {Truncate(value.CurrentDatabase, 28)}  "
            + $"entries {value.EntriesSeen}  files {value.FilesWritten}  "
            + $"{Format.Bytes(value.BytesWritten)}   ");
        if (percent >= 100)
        {
            Console.Error.WriteLine();
        }
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";
}

/// <summary>Byte-size formatting helpers.</summary>
public static class Format
{
    /// <summary>Formats a byte count with a binary unit.</summary>
    public static string Bytes(long value) => Core.ByteSize.Format(value);
}
