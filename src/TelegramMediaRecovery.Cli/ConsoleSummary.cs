using TelegramMediaRecovery.Core.Extraction;

namespace TelegramMediaRecovery.Cli;

/// <summary>Prints the end-of-run summary table.</summary>
public static class ConsoleSummary
{
    /// <summary>Writes the summary.</summary>
    public static void Print(ExtractionReport report, SystemConsole console)
    {
        console.Out(new string('=', 68));
        console.Out("Summary");
        console.Out(new string('=', 68));
        console.Out($"  tdata              : {report.OutputDirectory}");
        console.Out($"  databases          : {report.Databases.Count}");
        console.Out($"  binlog entries     : {report.Counters.EntriesSeen}");
        console.Out($"  files written      : {report.Counters.FilesWritten}");
        console.Out($"  bytes written      : {Format.Bytes(report.Counters.BytesWritten)}");
        console.Out($"  duplicates skipped : {report.Counters.Duplicates}");
        console.Out($"  orphan candidates  : {report.Counters.OrphanCandidates}");
        console.Out($"  skipped            : {report.Counters.SkippedTotal} (detailed in skipped.csv)");
        console.Out($"  duration           : {report.Duration.TotalSeconds:0.0}s");

        if (report.ByCategory.Count > 0)
        {
            console.Out(string.Empty);
            console.Out("  by category:");
            foreach (var (kind, count) in report.ByCategory.OrderByDescending(static p => p.Value))
            {
                var bytes = report.BytesByCategory[kind];
                console.Out($"      {kind,-12} {count,7} files  {Format.Bytes(bytes),12}");
            }
        }

        if (report.Counters.SkippedTotal > 0)
        {
            console.Out(string.Empty);
            console.Out("  skipped reasons:");
            foreach (var (reason, count) in report.Counters.Skipped.OrderByDescending(static p => p.Value))
            {
                console.Out($"      {reason,-32} {count,8}");
            }
        }

        var warnings = report.Databases.SelectMany(static d => d.Warnings).ToList();
        if (warnings.Count > 0)
        {
            console.Out(string.Empty);
            console.Out("  warnings:");
            foreach (var warning in warnings)
            {
                console.Out($"      {warning}");
            }
        }

        console.Out(string.Empty);
        console.Out($"  output folder      : {report.OutputDirectory}");
    }
}
