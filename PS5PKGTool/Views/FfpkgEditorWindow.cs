using Avalonia.Controls;
using UFS2Tool;

namespace PS5PKGTool.Views;

/// <summary>The FFPKG editor contract used by the image tools.</summary>
internal static class FfpkgEditorWindow
{
    public static Task<bool> ShowAsync(Window owner, string imagePath) =>
        ImageEditorWindow.ShowAsync(owner, new FfpkgEditorBackend(imagePath));
}

internal sealed class FfpkgEditorBackend(string imagePath) : IImageEditorBackend
{
    private readonly List<Ufs2EditOperation> _operations = [];

    public string FormatName => "FFPKG";
    public string ImagePath { get; } = Path.GetFullPath(imagePath);
    public string ApplyMessage =>
        "All changes extract and rebuild the FFPKG image beside the original. The original is replaced only after " +
        "full verification. Large game images require substantial free space and time.";
    public string CloseMessage(string queued) => $"Discard {queued} queued FFPKG change(s)?";
    public string InvalidPathMessage => "Enter the full internal path for the new directory.";
    public int OperationCount => _operations.Count;

    public async Task<IReadOnlyList<ImageEditorEntry>> LoadEntriesAsync()
    {
        return await Task.Run<IReadOnlyList<ImageEditorEntry>>(() =>
        {
            var entries = new List<ImageEditorEntry>();
            using var volume = new Ufs2Volume(ImagePath);
            foreach (Ufs2VolumeEntry entry in volume.Entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
                entries.Add(new ImageEditorEntry(entry.Path, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size));
            return entries;
        });
    }

    public void QueueReplace(string path, string source) =>
        _operations.Add(Ufs2EditOperation.Replace(path, source));

    public void QueueAddFile(string path, string source) =>
        _operations.Add(Ufs2EditOperation.AddFile(path, source));

    public void QueueAddFolder(string path, string source) =>
        _operations.Add(Ufs2EditOperation.AddDirectoryTree(path, source));

    public void QueueNewDirectory(string path) => _operations.Add(Ufs2EditOperation.AddDirectory(path));

    public void QueueDelete(string path) => _operations.Add(Ufs2EditOperation.Delete(path));

    public string NormalizePath(string path) => Ufs2Operations.NormalizeImagePath(path);

    public void RemoveLastOperation()
    {
        if (_operations.Count > 0) _operations.RemoveAt(_operations.Count - 1);
    }

    public async Task<ImageEditorApplyResult> ApplyAsync(
        IProgress<(string Stage, long Processed, long Total)> progress, CancellationToken token)
    {
        var bridge = new Progress<Ufs2Progress>(value =>
        {
            long total = value.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
                ? value.Total
                : value.Total;
            long completed = value.Completed;
            progress.Report((value.Stage, completed, total));
        });
        Ufs2EditResult result = await Ufs2Operations.ApplyEditsAsync(ImagePath, _operations, bridge, token);
        _operations.Clear();
        return new ImageEditorApplyResult("Verified transactional FFPKG rebuild",
            result.Verification.FileCount, result.Verification.DirectoryCount,
            result.Verification.ManifestSha256);
    }
}
