using TelegramMediaRecovery.Core.Media;

namespace TelegramMediaRecovery.Cli;

/// <summary>Parsed command-line options for <c>tmr</c>.</summary>
public sealed class CommandLineArguments
{
    /// <summary>Path of the <c>tdata</c> folder.</summary>
    public string? TdataPath { get; private set; }

    /// <summary>Local passcode.</summary>
    public string? Passcode { get; private set; }

    /// <summary>Where recovered files go.</summary>
    public string OutputDirectory { get; private set; } = "recovered";

    /// <summary>Account filter.</summary>
    public List<string> Accounts { get; } = [];

    /// <summary>Database filter.</summary>
    public List<string> Databases { get; } = [];

    /// <summary>Media-kind filter.</summary>
    public HashSet<MediaKind> Kinds { get; } = [];

    /// <summary>Smallest acceptable value in bytes.</summary>
    public long MinimumSize { get; private set; }

    /// <summary>Largest acceptable value in bytes; 0 means unlimited.</summary>
    public long MaximumSize { get; private set; }

    /// <summary>Minimum share of a streamed file that must be cached.</summary>
    public double MinimumCoverage { get; private set; } = 0.05;

    /// <summary>Require the header slice of a streamed file.</summary>
    public bool RequireStreamHeader { get; private set; }

    /// <summary>Keep unrecognised content as <c>.bin</c>.</summary>
    public bool KeepUnknown { get; private set; }

    /// <summary>Skip the orphan scan.</summary>
    public bool NoOrphans { get; private set; }

    /// <summary>Skip SHA-256 computation.</summary>
    public bool NoHash { get; private set; }

    /// <summary>Do not add a <c>.partial</c> infix to incomplete streamed files.</summary>
    public bool NoPartialSuffix { get; private set; }

    /// <summary>Prefix file names with their estimated date.</summary>
    public bool DatedNames { get; private set; }

    /// <summary>Only read the version folder named by the <c>version</c> marker.</summary>
    public bool LiveVersionOnly { get; private set; }

    /// <summary>Write <c>manifest.json</c> as well as the CSV.</summary>
    public bool JsonManifest { get; private set; }

    /// <summary>Write an HTML gallery.</summary>
    public bool HtmlReport { get; private set; }

    /// <summary>Cap on files per database; 0 means unlimited.</summary>
    public int Limit { get; private set; }

    /// <summary>Worker threads; 0 means one per CPU.</summary>
    public int Threads { get; private set; }

    /// <summary>Print an inventory and exit without writing anything.</summary>
    public bool ListOnly { get; private set; }

    /// <summary>Print usage and exit.</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>Parses the raw argument vector.</summary>
    public static CommandLineArguments Parse(string[] args)
    {
        var result = new CommandLineArguments();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    result.ShowHelp = true;
                    break;
                case "--tdata":
                    result.TdataPath = Next(args, ref i, arg);
                    break;
                case "--passcode":
                    result.Passcode = Next(args, ref i, arg);
                    break;
                case "--out":
                case "--output":
                    result.OutputDirectory = Next(args, ref i, arg);
                    break;
                case "--account":
                    result.Accounts.Add(Next(args, ref i, arg));
                    break;
                case "--db":
                    result.Databases.Add(Next(args, ref i, arg));
                    break;
                case "--kind":
                    foreach (var name in Next(args, ref i, arg).Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        result.Kinds.Add(ParseKind(name));
                    }

                    break;
                case "--min-size":
                    result.MinimumSize = ParseSize(Next(args, ref i, arg));
                    break;
                case "--max-size":
                    result.MaximumSize = ParseSize(Next(args, ref i, arg));
                    break;
                case "--min-coverage":
                    result.MinimumCoverage = double.Parse(
                        Next(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--require-header":
                    result.RequireStreamHeader = true;
                    break;
                case "--keep-unknown":
                    result.KeepUnknown = true;
                    break;
                case "--no-orphans":
                    result.NoOrphans = true;
                    break;
                case "--no-hash":
                    result.NoHash = true;
                    break;
                case "--no-partial-suffix":
                    result.NoPartialSuffix = true;
                    break;
                case "--dated-names":
                    result.DatedNames = true;
                    break;
                case "--live-version-only":
                    result.LiveVersionOnly = true;
                    break;
                case "--json":
                    result.JsonManifest = true;
                    break;
                case "--html":
                    result.HtmlReport = true;
                    break;
                case "--limit":
                    result.Limit = int.Parse(Next(args, ref i, arg));
                    break;
                case "--threads":
                    result.Threads = int.Parse(Next(args, ref i, arg));
                    break;
                case "--list":
                    result.ListOnly = true;
                    break;
                default:
                    throw new CommandLineException($"unknown option {arg}");
            }
        }

        return result;
    }

    /// <summary>Parses a human-readable size such as <c>5MB</c> or <c>512K</c>.</summary>
    public static long ParseSize(string text) =>
        Core.ByteSize.TryParse(text, out var bytes)
            ? bytes
            : throw new CommandLineException($"cannot parse size '{text}'");

    /// <summary>Writes the usage text.</summary>
    public static void WriteHelp(SystemConsole console)
    {
        console.Out("""
            tmr - recover cached media from a Telegram Desktop / 64Gram tdata folder

            Usage:
              tmr --tdata <path> [options]

            Options:
              --tdata <path>        tdata folder (default: ./tdata when it holds key_datas)
              --passcode <text>     local passcode (default: empty)
              --out <dir>           output folder (default: ./recovered)
              --account <name>      only this account; repeatable (e.g. user_data, user_data#2)
              --db <name>           cache | media_cache; repeatable
              --kind <list>         image,animation,video,audio,document (comma separated)
              --min-size <size>     skip smaller values, e.g. 4KB, 512K, 1MB
              --max-size <size>     skip larger values
              --min-coverage <0..1> minimum cached share of a streamed file (default 0.05)
              --require-header      only write streamed files whose header slice survived
              --keep-unknown        also write unrecognised content as .bin
              --no-orphans          skip the unreferenced-file scan
              --no-hash             do not compute SHA-256 (faster, no de-duplication)
              --no-partial-suffix   do not mark incomplete files with .partial
              --dated-names         prefix file names with the estimated date
              --live-version-only   ignore older cache version folders
              --limit <n>           stop after n files per database
              --threads <n>         worker threads (default: one per CPU)
              --json                also write manifest.json
              --html                also write index.html
              --list                print an inventory and exit
              -h, --help            this text

            The tdata folder is only ever read. Nothing inside it is created,
            renamed or deleted.
            """);
    }

    private static MediaKind ParseKind(string name) => name.ToLowerInvariant() switch
    {
        "image" or "images" or "photo" or "photos" => MediaKind.Image,
        "animation" or "animations" or "gif" => MediaKind.Animation,
        "video" or "videos" => MediaKind.Video,
        "audio" or "voice" => MediaKind.Audio,
        "document" or "documents" or "file" => MediaKind.Document,
        "any" or "all" => MediaKind.Unknown,
        _ => throw new CommandLineException($"unknown media kind '{name}'"),
    };

    private static string Next(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new CommandLineException($"{option} needs a value");
        }

        return args[++index];
    }
}
