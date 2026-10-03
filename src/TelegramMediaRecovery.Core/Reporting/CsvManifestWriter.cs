using System.Globalization;
using System.Text;
using TelegramMediaRecovery.Core.Extraction;

namespace TelegramMediaRecovery.Core.Reporting;

/// <summary>
/// Writes <c>manifest.csv</c>: one row per recovered file, with everything needed
/// to trace it back to the exact cache entry it came from.
/// </summary>
/// <remarks>
/// Columns:
/// <c>account, database, source, key_high, key_low, place, version, stored_size,
/// size, coverage, kind, extension, sha256, fragments, partial, date,
/// output, first_bytes</c>.
/// </remarks>
public static class CsvManifestWriter
{
    /// <summary>Header row of the CSV file.</summary>
    public static string Header { get; } = string.Join(
        ',',
        "account",
        "database",
        "source",
        "key_high",
        "key_low",
        "place",
        "version",
        "stored_size",
        "size",
        "coverage",
        "kind",
        "extension",
        "mime",
        "sha256",
        "fragments",
        "partial",
        "date",
        "output");

    /// <summary>Formats one item as a CSV row.</summary>
    public static string ToCsvRow(ExtractedItem item)
    {
        return string.Join(
            ',',
            Quote(item.Account),
            Quote(item.Database),
            Quote(item.Source),
            Quote(item.Key.ToString()),
            Quote(item.Place),
            item.Version.ToString(CultureInfo.InvariantCulture),
            item.StoredSize.ToString(CultureInfo.InvariantCulture),
            item.Size.ToString(CultureInfo.InvariantCulture),
            item.Coverage.ToString("0.000", CultureInfo.InvariantCulture),
            Quote(item.Format.Kind.ToString()),
            Quote(item.Format.Extension),
            Quote(item.Format.MimeType),
            Quote(item.Sha256 ?? string.Empty),
            item.FragmentCount.ToString(CultureInfo.InvariantCulture),
            item.IsPartial ? "1" : "0",
            Quote(item.Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            Quote(item.RelativePath));
    }

    /// <summary>Writes the manifest.</summary>
    public static void WriteCsv(string path, ExtractionReport report)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        writer.WriteLine(Header);
        foreach (var item in report.Items)
        {
            writer.WriteLine(ToCsvRow(item));
        }
    }

    /// <summary>Writes the skips to a second CSV, for "why is my photo missing?" debugging.</summary>
    public static void WriteSkippedCsv(string path, ExtractionReport report)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        writer.WriteLine("reason,account,database,key,place,size,detail");
        foreach (var item in report.Skipped)
        {
            writer.WriteLine(string.Join(
                ',',
                Quote(item.Reason.ToString()),
                Quote(item.Account),
                Quote(item.Database),
                Quote(item.Key.ToString()),
                Quote(item.Place),
                item.Size.ToString(CultureInfo.InvariantCulture),
                Quote(item.Detail ?? string.Empty)));
        }
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n']) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
}
