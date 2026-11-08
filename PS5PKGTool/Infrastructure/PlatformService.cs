using System.Diagnostics;

namespace PS5PKGTool.Infrastructure;

/// <summary>
/// Platform-neutral replacements for the desktop integration the WinForms build performed with
/// hard-coded Windows commands. .NET's <see cref="ProcessStartInfo.UseShellExecute"/> resolves the
/// desktop's default handler on every supported OS, so no OS-specific executable is named here.
/// Everything is best-effort: a sandboxed desktop is reported through the log instead of throwing
/// into a UI event handler.
/// </summary>
public static class PlatformService
{
    /// <summary>Opens a file, folder or URL with the desktop's default handler.</summary>
    public static bool OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Unable to open '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Shows a path in the desktop file manager by opening its containing folder. Selecting the item
    /// inside the folder is file-manager specific, so the portable behaviour is to open the folder.
    /// </summary>
    public static bool RevealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        if (!Directory.Exists(folder)) return false;
        return OpenPath(folder);
    }

    /// <summary>Opens an http(s) URL in the default browser.</summary>
    public static bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Unable to open '{url}': {ex.Message}");
            return false;
        }
    }
}
