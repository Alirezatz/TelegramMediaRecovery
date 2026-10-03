using TelegramMediaRecovery.Core.Cache;
using TelegramMediaRecovery.Core.Crypto;
using TelegramMediaRecovery.Core.Extraction;
using TelegramMediaRecovery.Core.Media;


namespace TelegramMediaRecovery.Cli;

/// <summary>
/// Headless front end. The WPF app is the friendly one; this exists for scripting,
/// for CI and for running on a machine where you would rather not open a window.
/// </summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        var console = new SystemConsole();
        try
        {
            return Run(args, console);
        }
        catch (CommandLineException ex)
        {
            console.Error($"error: {ex.Message}");
            console.Error("Run 'tmr --help' for usage.");
            return 2;
        }
        catch (LocalBlobCipher.WrongPasscodeException ex)
        {
            console.Error($"error: {ex.Message}");
            return 3;
        }
        catch (OperationCanceledException)
        {
            console.Error("cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            console.Error($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args, SystemConsole console)
    {
        var arguments = CommandLineArguments.Parse(args);

        if (arguments.ShowHelp)
        {
            CommandLineArguments.WriteHelp(console);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            console.Warn("cancelling ...");
            cancellation.Cancel();
        };

        var tdata = arguments.TdataPath ?? DefaultTdataPath();
        if (!Directory.Exists(tdata))
        {
            throw new CommandLineException($"--tdata is not a directory: {tdata}");
        }

        var passcode = arguments.Passcode ?? ReadPasscode(console);

        console.Info($"tdata: {Path.GetFullPath(tdata)}");
        var key = LocalKeyLoader.Load(tdata, passcode, console.Info);
        console.Info($"local key: {key.Length} bytes");

        if (arguments.ListOnly)
        {
            return Inventory(tdata, key, arguments, console);
        }

        var options = new ExtractionOptions
        {
            OutputDirectory = arguments.OutputDirectory,
            Accounts = arguments.Accounts,
            Databases = arguments.Databases,
            MinimumSize = arguments.MinimumSize,
            MaximumSize = arguments.MaximumSize,
            KeepUnknownContent = arguments.KeepUnknown,
            Kinds = arguments.Kinds,
            MinimumCoverage = arguments.MinimumCoverage,
            RequireStreamHeader = arguments.RequireStreamHeader,
            MaxFilesPerDatabase = arguments.Limit,
            MaxDegreeOfParallelism = arguments.Threads,
            ScanOrphans = !arguments.NoOrphans,
            ComputeHashes = !arguments.NoHash,
            DatedFileNames = arguments.DatedNames,
            MarkPartialFiles = !arguments.NoPartialSuffix,
            IncludeOldVersions = !arguments.LiveVersionOnly,
            WriteManifest = true,
            WriteJsonManifest = arguments.JsonManifest,
            WriteHtmlReport = arguments.HtmlReport,
            Log = console.Debug,
            Progress = new ConsoleProgress(),
            CancellationToken = cancellation.Token,
        };

        // The engine writes manifest.csv, skipped.csv and, when asked,
        // manifest.json and index.html next to the recovered files.
        var report = new ExtractionEngine(options).Run(tdata, key);

        ConsoleSummary.Print(report, console);
        return 0;
    }

    private static int Inventory(string tdata, LocalKey key, CommandLineArguments arguments, SystemConsole console)
    {
        foreach (var database in CacheDatabaseLocator.Discover(tdata))
        {
            var versions = database.Versions();
            console.Info($"{database}: {versions.Count} version folder(s)");
            foreach (var version in versions)
            {
                try
                {
                    var parsed = CacheBinlogReader.ReadFile(version.BinlogPath, key, version.Number);
                    console.Info(
                        $"  version {version.Number}: {parsed.Entries.Count} live entries, "
                        + $"{parsed.RecordsRead} records, trackTime={parsed.Header.TrackEstimatedTime}");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    console.Warn($"  version {version.Number}: {ex.Message}");
                }
            }
        }

        _ = arguments;
        return 0;
    }

    private static string DefaultTdataPath()
    {
        var here = AppContext.BaseDirectory;
        var probe = new DirectoryInfo(here);
        for (var level = 0; level < 4 && probe is not null; level++, probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "tdata");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "key_datas")))
            {
                return candidate;
            }
        }

        return Path.Combine(Environment.CurrentDirectory, "tdata");
    }

    private static string ReadPasscode(SystemConsole console)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMR_PASSCODE")))
        {
            return Environment.GetEnvironmentVariable("TMR_PASSCODE")!;
        }

        console.Info("No passcode given; press Enter if the local passcode is empty.");
        var value = Console.ReadLine();
        return value ?? string.Empty;
    }
}

/// <summary>Raised for bad command-line input.</summary>
public sealed class CommandLineException(string message) : Exception(message);
