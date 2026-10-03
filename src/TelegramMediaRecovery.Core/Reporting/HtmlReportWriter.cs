using System.Globalization;
using System.Net;
using System.Text;
using TelegramMediaRecovery.Core.Extraction;
using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Core.Reporting;

/// <summary>
/// Writes <c>index.html</c>: a self-contained, offline gallery of everything that
/// was recovered, grouped by account and media kind.
/// </summary>
/// <remarks>
/// No scripts, no CDN, no web fonts &mdash; the report has to work from a USB stick
/// on a machine with no internet. Images, videos and audio are referenced with
/// paths relative to the report itself, so the whole output folder can be zipped
/// and sent to someone.
/// </remarks>
public static class HtmlReportWriter
{
    private const int PreviewsPerGroup = 400;

    /// <summary>Renders the report next to the extracted files.</summary>
    public static void Write(string path, ExtractionReport report)
    {
        var html = Render(report);
        File.WriteAllText(path, html, new UTF8Encoding(false));
    }

    /// <summary>Renders the report to a string.</summary>
    public static string Render(ExtractionReport report)
    {
        var root = report.OutputDirectory;
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("<title>Telegram media recovery report</title>");
        AppendStyle(builder);
        builder.AppendLine("</head><body>");

        builder.AppendLine("<header>");
        builder.AppendLine("<h1>Telegram media recovery report</h1>");
        builder.AppendLine($"<p class=\"sub\">generated {Escape(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}"
            + $" &middot; {report.Counters.FilesWritten} files"
            + $" &middot; {Bytes(report.Counters.BytesWritten)}"
            + $" &middot; {report.Duration.TotalSeconds:0.0}s</p>");
        builder.AppendLine("</header>");

        builder.AppendLine("<section><h2>Databases</h2><table><thead><tr>"
            + "<th>account</th><th>database</th><th>version</th><th>records</th>"
            + "<th>live entries</th><th>from older versions</th><th>orphans recovered</th>"
            + "</tr></thead><tbody>");
        foreach (var database in report.Databases)
        {
            builder.AppendLine("<tr>"
                + $"<td>{Escape(database.Account)}</td>"
                + $"<td>{Escape(database.Database)}</td>"
                + $"<td>{database.Version}</td>"
                + $"<td>{database.RecordsRead:N0}</td>"
                + $"<td>{database.LiveEntries:N0}</td>"
                + $"<td>{database.OlderVersionEntries:N0}</td>"
                + $"<td>{database.OrphanFilesRecovered:N0}</td>"
                + "</tr>");
        }

        builder.AppendLine("</tbody></table>");

        var warnings = report.Databases.SelectMany(static d => d.Warnings.Select(w => $"{d.Account}/{d.Database}: {w}")).ToList();
        if (warnings.Count > 0)
        {
            builder.AppendLine("<h2>Warnings</h2><ul>");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"<li>{Escape(warning)}</li>");
            }

            builder.AppendLine("</ul>");
        }

        builder.AppendLine("</section>");

        foreach (var group in report.Items
                     .GroupBy(static i => (i.Account, i.Database, i.Format.Kind))
                     .OrderByDescending(static g => g.Count()))
        {
            AppendGroup(builder, root, group.Key.Account, group.Key.Database, group.Key.Kind, [.. group]);
        }

        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static void AppendGroup(
        StringBuilder builder,
        string root,
        string account,
        string database,
        MediaKind kind,
        List<ExtractedItem> items)
    {
        builder.AppendLine("<section>");
        builder.AppendLine($"<h2>{Escape(account)} / {Escape(database)} / {Escape(OutputNamer.FolderFor(kind))}"
            + $" <span class=\"count\">{items.Count:N0}</span></h2>");
        builder.AppendLine("<div class=\"grid\">");

        foreach (var item in items.Take(PreviewsPerGroup))
        {
            builder.AppendLine("<figure>");
            builder.AppendLine(Preview(root, item));
            builder.AppendLine(
                $"<figcaption>{Bytes(item.Size)}"
                + (item.IsPartial ? $" &middot; {item.Coverage:P0} cached" : string.Empty)
                + $"<br><span title=\"{Escape(item.Key.ToString())}\">{Escape(item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}</span></figcaption>");
            builder.AppendLine("</figure>");
        }

        builder.AppendLine("</div>");

        if (items.Count > PreviewsPerGroup)
        {
            builder.AppendLine($"<p class=\"sub\">showing the first {PreviewsPerGroup} of {items.Count:N0}; "
                + "see manifest.csv for the complete list</p>");
        }

        builder.AppendLine("</section>");
    }

    private static string Preview(string root, ExtractedItem item)
    {
        if (item.OutputPath is null)
        {
            return "<div class=\"missing\">not written</div>";
        }

        var href = WebUtility.HtmlEncode(Relative(root, item.OutputPath));
        return item.Format.Kind switch
        {
            MediaKind.Image => $"<a href=\"{href}\"><img loading=\"lazy\" src=\"{href}\" alt=\"\"></a>",
            MediaKind.Video => $"<video controls preload=\"metadata\" src=\"{href}\"></video>",
            MediaKind.Audio => $"<audio controls preload=\"metadata\" src=\"{href}\"></audio>",
            _ => $"<a class=\"doc\" href=\"{href}\">{Escape(item.Format.Extension.ToUpperInvariant())}</a>",
        };
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{size:0.#} {units[unit]}";
    }

    private static void AppendStyle(StringBuilder builder)
    {
        builder.AppendLine("<style>");
        builder.AppendLine("""
            :root{color-scheme:dark light;--bg:#14161a;--fg:#e8eaed;--muted:#9aa0a6;--card:#1d2026;--line:#2b2f36}
            @media (prefers-color-scheme:light){:root{--bg:#f6f7f9;--fg:#1b1d21;--muted:#5f6368;--card:#fff;--line:#e2e5e9}}
            *{box-sizing:border-box}
            body{margin:0;padding:24px;background:var(--bg);color:var(--fg);
                 font:14px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}
            h1{font-size:22px;margin:0 0 4px}
            h2{font-size:16px;margin:28px 0 12px;font-weight:600}
            .sub{color:var(--muted);margin:0 0 16px}
            .count{color:var(--muted);font-weight:400}
            table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}
            th,td{text-align:left;padding:6px 12px;border-bottom:1px solid var(--line)}
            th{color:var(--muted);font-weight:600}
            .grid{display:grid;gap:12px;grid-template-columns:repeat(auto-fill,minmax(150px,1fr))}
            figure{margin:0;background:var(--card);border:1px solid var(--line);border-radius:8px;padding:6px;overflow:hidden}
            figure img,figure video{width:100%;height:130px;object-fit:cover;border-radius:4px;display:block;background:#000}
            figure audio{width:100%}
            figcaption{font-size:11px;color:var(--muted);margin-top:6px;text-align:center}
            .doc{display:flex;align-items:center;justify-content:center;height:130px;border-radius:4px;
                 background:var(--line);color:var(--fg);font-weight:600;text-decoration:none}
            .missing{padding:24px;color:var(--muted)}
            ul{padding-left:20px}
            """);
        builder.AppendLine("</style>");
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
