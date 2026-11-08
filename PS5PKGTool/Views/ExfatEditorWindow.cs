using Avalonia.Controls;
using PS5PKGTool.Ffpfsc;

namespace PS5PKGTool.Views;

/// <summary>The exFAT editor contract used by the image tools.</summary>
internal static class ExfatEditorWindow
{
    public static Task<bool> ShowAsync(Window owner, string imagePath) =>
        ImageEditorWindow.ShowAsync(owner, new ExfatEditorBackend(imagePath));
}

internal sealed class ExfatEditorBackend(string imagePath) : IImageEditorBackend
{
    private readonly List<ExfatEditOperation> _operations = [];

    public string FormatName => "exFAT";
    public string ImagePath { get; } = Path.GetFullPath(imagePath);
    public string ApplyMessage =>
        "A single equal-size replacement uses an on-disk rollback journal. Size changes, additions, and " +
        "deletions extract and rebuild the image beside the original. The original is replaced only after " +
        "full verification. Large game images require substantial free space and time.";
    public string CloseMessage(string queued) => $"Discard {queued} queued exFAT change(s)?";
    public string InvalidPathMessage => "Enter the full internal path for the new directory.";
    public int OperationCount => _operations.Count;

    public async Task<IReadOnlyList<ImageEditorEntry>> LoadEntriesAsync()
    {
        return await Task.Run<IReadOnlyList<ImageEditorEntry>>(() =>
        {
            var entries = new List<ImageEditorEntry>();
            using FileStream image = File.OpenRead(ImagePath);
            using var volume = new ExfatVolume(image);
            foreach (ExfatEntry entry in volume.Entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
                entries.Add(new ImageEditorEntry(entry.Path, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size));
            return entries;
        });
    }

    public void QueueReplace(string path, string source) =>
        _operations.Add(ExfatEditOperation.Replace(path, source));

    public void QueueAddFile(string path, string source) =>
        _operations.Add(ExfatEditOperation.AddFile(path, source));

    public void QueueAddFolder(string path, string source) =>
        _operations.Add(ExfatEditOperation.AddDirectoryTree(path, source));

    public void QueueNewDirectory(string path) => _operations.Add(ExfatEditOperation.AddDirectory(path));

    public void QueueDelete(string path) => _operations.Add(ExfatEditOperation.Delete(path));

    public string NormalizePath(string path) => ExfatImageMaintenance.NormalizeImagePath(path);

    public void RemoveLastOperation()
    {
        if (_operations.Count > 0) _operations.RemoveAt(_operations.Count - 1);
    }

    public async Task<ImageEditorApplyResult> ApplyAsync(
        IProgress<(string Stage, long Processed, long Total)> progress, CancellationToken token)
    {
        var bridge = new Progress<FfpfscProgress>(value =>
            progress.Report((value.Stage, value.BytesProcessed, value.TotalBytes)));
        ExfatEditResult result = await ExfatImageMaintenance.ApplyEditsAsync(ImagePath, _operations, bridge, token);
        _operations.Clear();
        return new ImageEditorApplyResult(
            result.Rebuilt ? "Verified transactional rebuild" : "Journaled in-place replacement",
            result.Verification.FileCount, result.Verification.DirectoryCount,
            result.Verification.ManifestSha256);
    }
}
