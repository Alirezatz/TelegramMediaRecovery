using TelegramMediaRecovery.Core.Extraction;

namespace TelegramMediaRecovery.Core.Reporting;

/// <summary>Shared report output for selected recovery and CLI extraction.</summary>
public static class RecoveryReportWriter
{
    public static void Write(ExtractionReport report, bool csv = true, bool json = true, bool html = true)
    {
        Directory.CreateDirectory(report.OutputDirectory);
        if (csv)
        {
            CsvManifestWriter.WriteCsv(Path.Combine(report.OutputDirectory, "manifest.csv"), report);
            CsvManifestWriter.WriteSkippedCsv(Path.Combine(report.OutputDirectory, "skipped.csv"), report);
        }
        if (json) { JsonManifestWriter.WriteJson(Path.Combine(report.OutputDirectory, "manifest.json"), report); }
        if (html) { HtmlReportWriter.Write(Path.Combine(report.OutputDirectory, "index.html"), report); }
    }
}
