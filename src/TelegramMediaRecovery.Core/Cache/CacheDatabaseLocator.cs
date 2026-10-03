namespace TelegramMediaRecovery.Core.Cache;

/// <summary>
/// Finds the account folders and cache databases inside a <c>tdata</c> folder.
/// </summary>
/// <remarks>
/// Each account owns <c>tdata/user_data</c> or <c>tdata/user_data#2</c> (etc. for
/// additional accounts, see <c>Storage::Account::serializeName()</c>), and inside
/// it the two lib_storage databases <c>cache</c> and <c>media_cache</c>.
/// </remarks>
public static class CacheDatabaseLocator
{
    private const string AccountPrefix = "user_data";
    private static readonly string[] DatabaseNames = ["cache", "media_cache"];

    /// <summary>Enumerates all databases of all accounts in <paramref name="tdataPath"/>.</summary>
    public static IReadOnlyList<CacheDatabase> Discover(string tdataPath)
    {
        if (!Directory.Exists(tdataPath))
        {
            return [];
        }

        var databases = new List<CacheDatabase>();

        foreach (var directory in Directory.EnumerateDirectories(tdataPath).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (!IsAccountFolder(name))
            {
                continue;
            }

            foreach (var databaseName in DatabaseNames)
            {
                var path = Path.Combine(directory, databaseName);
                if (Directory.Exists(path))
                {
                    databases.Add(new CacheDatabase(name, databaseName, path));
                }
            }
        }

        return databases;
    }

    /// <summary>True for <c>user_data</c> and <c>user_data#N</c>.</summary>
    public static bool IsAccountFolder(string name)
    {
        if (!name.StartsWith(AccountPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = name[AccountPrefix.Length..];
        return suffix.Length == 0
               || (suffix.Length > 1 && suffix[0] == '#' && suffix[1..].All(char.IsAsciiDigit));
    }
}
