using System.Buffers.Binary;

namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// One versioned generation of a cache database: a folder named with a number
/// that holds a binlog and the two-character bucket directories of place files.
/// </summary>
/// <param name="databasePath">The database folder that owns this version.</param>
/// <param name="number">Version number, i.e. the folder name.</param>
/// <param name="binlogPath">Full path of the binlog file.</param>
public sealed class CacheVersion(string databasePath, int number, string binlogPath)
{
    /// <summary>Folder that contains this version.</summary>
    public string VersionPath => System.IO.Path.Combine(DatabasePath, Number.ToString());

    /// <summary>The database folder that owns this version.</summary>
    public string DatabasePath { get; } = databasePath;

    /// <summary>Version number, i.e. the folder name.</summary>
    public int Number { get; } = number;

    /// <summary>Full path of the binlog file.</summary>
    public string BinlogPath { get; } = binlogPath;

    /// <inheritdoc />
    public override string ToString() => $"{System.IO.Path.GetFileName(DatabasePath)}/{Number}";
}

/// <summary>
/// Reads the <c>version</c> marker and enumerates the version folders of a cache
/// database.
/// </summary>
/// <remarks>
/// Reference: lib_storage <c>storage/cache/storage_cache_types.cpp</c> &rarr;
/// <c>ReadVersionValue()</c> (a little-endian <c>int32</c> in a file literally
/// named <c>version</c>) and <c>storage/cache/storage_cache_database_object.cpp</c>
/// &rarr; <c>computePath()</c> (<c>base + QString::number(version) + '/'</c>).
/// <para>
/// The marker can be missing (fresh databases) or stale (the app crashed between
/// writing the folder and the marker), so the enumerator never trusts it alone:
/// every numeric folder that actually contains a binlog is reported.
/// </para>
/// </remarks>
public static class CacheVersionStore
{
    /// <summary>File names that hold the binlog, in the order the client uses them.</summary>
    public static readonly string[] BinlogFileNames = ["binlog", "binlog-ready"];

    /// <summary>Name of the version marker file.</summary>
    public const string VersionFileName = "version";

    /// <summary>Reads the version marker, returning <c>null</c> when it is absent.</summary>
    public static int? ReadVersion(string databasePath)
    {
        var path = Path.Combine(databasePath, VersionFileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var raw = File.ReadAllBytes(path);
            return raw.Length < 4 ? null : BinaryPrimitives.ReadInt32LittleEndian(raw);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists every version folder that contains a binlog, marking which one the
    /// <c>version</c> marker points at and ordering newest first.
    /// </summary>
    public static IReadOnlyList<CacheVersion> EnumerateVersions(string databasePath)
    {
        if (!Directory.Exists(databasePath))
        {
            return [];
        }

        var marked = ReadVersion(databasePath);
        var versions = new List<CacheVersion>();

        foreach (var directory in Directory.EnumerateDirectories(databasePath))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var number))
            {
                continue;
            }

            var binlog = BinlogFileNames
                .Select(name => Path.Combine(directory, name))
                .FirstOrDefault(File.Exists);

            if (binlog is not null)
            {
                versions.Add(new CacheVersion(databasePath, number, binlog));
            }
        }

        return versions
            .OrderByDescending(v => v.Number == marked)
            .ThenByDescending(v => v.Number)
            .ToList();
    }
}
