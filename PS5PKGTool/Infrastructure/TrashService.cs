using PS5PKGTool.Core.Services;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PS5PKGTool.Infrastructure;

/// <summary>
/// Moves a deleted library item to the platform trash: the normal Windows Recycle Bin, the macOS
/// <c>~/.Trash</c> folder, or the freedesktop.org trash on Linux. Falls back to a permanent delete
/// only when the platform trash cannot be used, so the Delete action still removes the source.
/// </summary>
public static class TrashService
{
    private const string TrashFolderName = ".Trash";

    /// <summary>Moves <paramref name="path"/> to the trash (or deletes it when asked/permanently).</summary>
    public static void Delete(string path, bool permanent)
    {
        if (permanent)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows()) DeleteWindows(path);
            else DeleteToTrash(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            // No usable trash on this machine/filesystem: keep the original promise of removing the
            // source instead of silently doing nothing.
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void DeleteToTrash(string path)
    {
        string files;
        string info;
        if (OperatingSystem.IsMacOS())
        {
            files = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), TrashFolderName);
            info = Path.Combine(files, "info");
        }
        else
        {
            string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            string trash = Path.Combine(dataHome, "Trash");
            files = Path.Combine(trash, "files");
            info = Path.Combine(trash, "info");
        }
        DeleteToFolder(path, files, info);
    }

    private static void DeleteToFolder(string path, string files, string info)
    {
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string target = Path.Combine(files, name);
        string infoPath = Path.Combine(info, name + ".trashinfo");
        for (int index = 2; File.Exists(target) || Directory.Exists(target) || File.Exists(infoPath); index++)
        {
            target = Path.Combine(files, $"{name} ({index})");
            infoPath = Path.Combine(info, $"{name} ({index}).trashinfo");
        }

        // The metadata file records where the item came from so the trash can be restored by hand.
        File.WriteAllText(infoPath,
            "[Trash Info]\n" +
            "Path=" + Uri.EscapeDataString(Path.GetFullPath(path)) + "\n" +
            "DeletionDate=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\n");
        try
        {
            // The trash folder (home / XDG data dir) often sits on another partition than the
            // library, where a plain rename fails; the mover falls back to copy-then-delete.
            LibraryFileMover.Move(path, target, CancellationToken.None);
        }
        catch
        {
            try { File.Delete(infoPath); } catch (IOException) { }
            throw;
        }
    }

    private const int FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_SILENT = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    /// <summary>Moves the item to the normal Windows Recycle Bin.</summary>
    [SupportedOSPlatform("windows")]
    private static void DeleteWindows(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            // Double-null-terminated list, as required by SHFileOperation.
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted != 0)
            throw new IOException($"Windows could not move '{path}' to the Recycle Bin (0x{result:X}).");
    }
}
