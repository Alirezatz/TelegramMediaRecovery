using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TelegramMediaRecovery.Core.Extraction;

namespace TelegramMediaRecovery.Core.Reporting;

/// <summary>
/// Writes <c>manifest.json</c>: the same information as the CSV, but as one JSON
/// object so it can be diffed between runs and consumed by scripts.
/// </summary>
public static class JsonManifestWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialises the report and writes it.</summary>
    public static void WriteJson(string path, ExtractionReport report)
    {
        var model = new Manifest
        {
            Tool = "TelegramMediaRecovery",
            GeneratedAt = DateTimeOffset.Now,
            TdataOutput = report.OutputDirectory,
            DurationSeconds = Math.Round(report.Duration.TotalSeconds, 3),
            Summary = new ManifestSummary
            {
                Databases = report.Databases.Count,
                BinlogEntries = report.Counters.EntriesSeen,
                FilesWritten = report.Counters.FilesWritten,
                BytesWritten = report.Counters.BytesWritten,
                DuplicatesSkipped = report.Counters.Duplicates,
                OrphanCandidates = report.Counters.OrphanCandidates,
                Skipped = report.Counters.SkippedTotal,
                SkippedByReason = report.Counters.Skipped.ToDictionary(
                    static p => p.Key.ToString(),
                    static p => p.Value),
            },
            Databases = [.. report.Databases.Select(static d => new ManifestDatabase
            {
                Account = d.Account,
                Database = d.Database,
                Version = d.Version,
                TrackEstimatedTime = d.TrackEstimatedTime,
                RecordsRead = d.RecordsRead,
                LiveEntries = d.LiveEntries,
                OlderVersionEntries = d.OlderVersionEntries,
                OrphanFilesRecovered = d.OrphanFilesRecovered,
                Warnings = d.Warnings,
            })],
            Files = [.. report.Items.Select(static i => new ManifestFile
            {
                Account = i.Account,
                Database = i.Database,
                Source = i.Source,
                Key = i.Key.ToString(),
                Place = i.Place,
                Version = i.Version,
                StoredSize = i.StoredSize,
                Size = i.Size,
                Coverage = Math.Round(i.Coverage, 4),
                Kind = i.Format.Kind.ToString(),
                Extension = i.Format.Extension,
                MimeType = i.Format.MimeType,
                Sha256 = i.Sha256,
                Fragments = i.FragmentCount,
                Partial = i.IsPartial,
                Date = i.Date.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                Output = i.RelativePath,
            })],
        };

        File.WriteAllText(path, JsonSerializer.Serialize(model, Options), new UTF8Encoding(false));
    }

    /// <summary>Reads back the SHA-256 column of a CSV manifest, for resuming.</summary>
    public static IReadOnlyList<string> ReadHashes(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            return [];
        }

        var hashes = new List<string>();
        var header = CsvManifestWriter.Header.Split(',');
        var column = Array.IndexOf(header, "sha256");

        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            if (column < 0)
            {
                break;
            }

            var fields = SplitCsvLine(line);
            if (column < fields.Count && fields[column].Length > 0)
            {
                hashes.Add(fields[column]);
            }
        }

        return hashes;
    }

    /// <summary>Splits one CSV line, honouring double-quoted fields.</summary>
    public static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                {
                    current.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private sealed class Manifest
    {
        public required string Tool { get; init; }

        public DateTimeOffset GeneratedAt { get; init; }

        public required string TdataOutput { get; init; }

        public double DurationSeconds { get; init; }

        public required ManifestSummary Summary { get; init; }

        public required IReadOnlyList<ManifestDatabase> Databases { get; init; }

        public required IReadOnlyList<ManifestFile> Files { get; init; }
    }

    private sealed class ManifestSummary
    {
        public int Databases { get; init; }

        public long BinlogEntries { get; init; }

        public long FilesWritten { get; init; }

        public long BytesWritten { get; init; }

        public long DuplicatesSkipped { get; init; }

        public long OrphanCandidates { get; init; }

        public long Skipped { get; init; }

        public required IReadOnlyDictionary<string, long> SkippedByReason { get; init; }
    }

    private sealed class ManifestDatabase
    {
        public required string Account { get; init; }

        public required string Database { get; init; }

        public int Version { get; init; }

        public bool TrackEstimatedTime { get; init; }

        public int RecordsRead { get; init; }

        public int LiveEntries { get; init; }

        public int OlderVersionEntries { get; init; }

        public int OrphanFilesRecovered { get; init; }

        public required IReadOnlyList<string> Warnings { get; init; }
    }

    private sealed class ManifestFile
    {
        public required string Account { get; init; }

        public required string Database { get; init; }

        public required string Source { get; init; }

        public required string Key { get; init; }

        public required string Place { get; init; }

        public int Version { get; init; }

        public int StoredSize { get; init; }

        public long Size { get; init; }

        public double Coverage { get; init; }

        public required string Kind { get; init; }

        public required string Extension { get; init; }

        public required string MimeType { get; init; }

        public string? Sha256 { get; init; }

        public int Fragments { get; init; }

        public bool Partial { get; init; }

        public required string Date { get; init; }

        public required string Output { get; init; }
    }
}
