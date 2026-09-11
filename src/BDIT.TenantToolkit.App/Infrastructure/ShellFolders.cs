using System.Diagnostics;
using System.IO;

namespace BDIT.TenantToolkit.App.Infrastructure;

/// <summary>
/// Opens folders and reveals exported files in Windows Explorer. Nothing here elevates, installs or associates file
/// types; it only asks the shell to show a path the toolkit has already written.
/// </summary>
public static class ShellFolders
{
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    }

    /// <summary>Opens the containing folder with the file selected. Falls back to the folder if the file has gone.</summary>
    public static void RevealFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        if (!File.Exists(file))
        {
            var parent = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(parent)) OpenFolder(parent);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true });
    }
}
