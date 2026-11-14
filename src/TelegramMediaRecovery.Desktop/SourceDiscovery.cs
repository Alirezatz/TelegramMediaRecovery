using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TelegramMediaRecovery.Desktop;

internal static class SourceDiscovery
{
    public static IReadOnlyList<string> Find()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) { return; }
            var data = File.Exists(Path.Combine(root, "key_datas")) ? root : Path.Combine(root, "tdata");
            if (File.Exists(Path.Combine(data, "key_datas"))) { paths.Add(Path.GetFullPath(data)); }
        }
        Add(AppContext.BaseDirectory);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Telegram Desktop"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "64Gram"));
        foreach (var name in new[] { "Telegram", "64Gram" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try { Add(Path.GetDirectoryName(process.MainModule?.FileName)); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                }
            }
        }
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        foreach (var name in new[] { "Telegram Desktop", "64Gram" })
        {
            var shortcut = Path.Combine(programs, name, "Telegram.lnk");
            if (!File.Exists(shortcut)) { continue; }
            object? shell = null;
            object? link = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type is null) { continue; }
                shell = Activator.CreateInstance(type);
                link = ((dynamic)shell!).CreateShortcut(shortcut);
                Add(Path.GetDirectoryName((string)((dynamic)link).TargetPath));
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { }
            finally
            {
                if (link is not null) { Marshal.ReleaseComObject(link); }
                if (shell is not null) { Marshal.ReleaseComObject(shell); }
            }
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
