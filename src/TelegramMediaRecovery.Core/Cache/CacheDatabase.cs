namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// One <c>cache</c> or <c>media_cache</c> database of one account.
/// </summary>
public sealed class CacheDatabase(string account, string name, string path)
{
    /// <summary>Account folder name, e.g. <c>user_data</c> or <c>user_data#3</c>.</summary>
    public string Account { get; } = account;

    /// <summary>Database folder name: <c>cache</c> or <c>media_cache</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Absolute path of the database folder.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// True for the streaming database that stores video/audio as slices.
    /// </summary>
    /// <remarks>
    /// <c>Telegram/SourceFiles/storage/storage_account.cpp</c> &rarr;
    /// <c>Account::cachePath()</c> returns <c>_databasePath + "cache"</c> and
    /// <c>Account::cacheBigFilePath()</c> returns <c>_databasePath + "media_cache"</c>.
    /// </remarks>
    public bool IsMediaCache => string.Equals(Name, "media_cache", StringComparison.Ordinal);

    /// <summary>Reads the <c>version</c> file, or <c>null</c> when it is missing/unreadable.</summary>
    public int? ReadVersion() => CacheVersionStore.ReadVersion(Path);

    /// <summary>All numeric version folders that hold a binlog, newest name first.</summary>
    public IReadOnlyList<CacheVersion> Versions() => CacheVersionStore.EnumerateVersions(Path);

    /// <summary>Reads the <c>version</c> marker, or <c>null</c> when it is missing.</summary>
    public int? Version => ReadVersion();

    /// <inheritdoc />
    public override string ToString() => $"{Account}/{Name}";
}
