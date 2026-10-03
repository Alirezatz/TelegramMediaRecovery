using System.Globalization;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Core.Extraction;

/// <summary>
/// Decides the output folder and file name of a recovered value.
/// </summary>
/// <remarks>
/// Layout: <c>&lt;output&gt;/&lt;account&gt;/&lt;database&gt;/&lt;kind&gt;/&lt;name&gt;</c>.
/// <c>&lt;kind&gt;</c> is the lower-cased <see cref="MediaKind"/>, so a run can be
/// browsed per media type without reading the manifest.
/// </remarks>
public sealed class OutputNamer(string outputRoot)
{
    private readonly string _outputRoot = Path.GetFullPath(outputRoot);
    private readonly Dictionary<string, int> _counters = [];
    private readonly Lock _gate = new();

    /// <summary>Absolute, normalised output root.</summary>
    public string Root => _outputRoot;

    /// <summary>Turns a media kind into its folder name.</summary>
    public static string FolderFor(MediaKind kind) => kind switch
    {
        MediaKind.Image => "images",
        MediaKind.Animation => "animations",
        MediaKind.Video => "videos",
        MediaKind.Audio => "audio",
        MediaKind.Document => "documents",
        _ => "unknown",
    };

    /// <summary>Creates (once) the folder a database and kind write into.</summary>
    public string EnsureDirectory(string account, string database, MediaKind kind)
    {
        var path = Path.Combine(_outputRoot, account, database, FolderFor(kind));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Reserves the next free file name for one account/database/kind/extension
    /// combination. Thread safe.
    /// </summary>
    /// <param name="partial">
    /// When true the extension gets a <c>.partial</c> infix so incomplete
    /// streamed files are obvious on disk.
    /// </param>
    public string NextPath(
        string account,
        string database,
        MediaKind kind,
        string extension,
        DateTimeOffset date,
        bool datedFileNames,
        bool partial)
    {
        var directory = EnsureDirectory(account, database, kind);
        var counterKey = $"{account}/{database}/{FolderFor(kind)}.{extension}";

        int sequence;
        lock (_gate)
        {
            _counters.TryGetValue(counterKey, out sequence);
            sequence++;
            _counters[counterKey] = sequence;
        }

        var suffix = partial ? $"{extension}.partial" : extension;
        var stamp = datedFileNames
            ? date.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_"
            : string.Empty;

        var name = $"{stamp}{sequence:D6}.{suffix}";
        return Path.Combine(directory, name);
    }

    /// <summary>Registers an already known sequence number so names stay stable across runs.</summary>
    public void Seed(string account, string database, MediaKind kind, string extension, int used)
    {
        var counterKey = $"{account}/{database}/{FolderFor(kind)}.{extension}";
        lock (_gate)
        {
            if (!_counters.TryGetValue(counterKey, out var current) || current < used)
            {
                _counters[counterKey] = used;
            }
        }
    }
}
