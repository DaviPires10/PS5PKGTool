using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using PS5PKGTool.Controls;
using TableView = PS5PKGTool.Controls.TableView;

namespace PS5PKGTool.Views;

/// <summary>One entry shown by the image editors.</summary>
internal readonly record struct ImageEditorEntry(string Path, bool IsDirectory, long Size);

/// <summary>Outcome of applying queued image edits.</summary>
internal sealed record ImageEditorApplyResult(string Mode, int FileCount, int DirectoryCount, string ManifestHash);

/// <summary>
/// The per-format part of the image editor: entry listing, edit operations and the verified apply.
/// Keeping it behind an interface lets the exFAT and FFPKG editors share one window and the same
/// queue/confirm/progress/busy contract without duplicating it.
/// </summary>
internal interface IImageEditorBackend
{
    string FormatName { get; }
    string ImagePath { get; }
    string ApplyMessage { get; }
    string CloseMessage(string queued);
    string InvalidPathMessage { get; }
    Task<IReadOnlyList<ImageEditorEntry>> LoadEntriesAsync();
    void QueueReplace(string path, string source);
    void QueueAddFile(string path, string source);
    void QueueAddFolder(string path, string source);
    void QueueNewDirectory(string path);
    void QueueDelete(string path);
    string NormalizePath(string path);
    int OperationCount { get; }
    void RemoveLastOperation();
    Task<ImageEditorApplyResult> ApplyAsync(IProgress<(string Stage, long Processed, long Total)> progress,
        CancellationToken token);
}

/// <summary>The Avalonia port of the exFAT/FFPKG image editors.</summary>
internal sealed class ImageEditorWindow : Window
{
    private readonly IImageEditorBackend _backend;
    private readonly TableView _grid = new();
    private readonly ListBox _changes = new() { Height = 110 };
    private readonly TextBox _target = new();
    private readonly TextBlock _lblImage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _lblStatus = new();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
    private readonly Button _btnReplace = new() { Content = "Replace..." };
    private readonly Button _btnAddFiles = new() { Content = "Add Files..." };
    private readonly Button _btnAddFolder = new() { Content = "Add Folder..." };
    private readonly Button _btnNewDirectory = new() { Content = "New Directory" };
    private readonly Button _btnDelete = new() { Content = "Delete" };
    private readonly Button _btnUndo = new() { Content = "Undo" };
    private readonly Button _btnApply = new() { Content = "Apply" };
    private readonly Button _btnCancelOperation = new() { Content = "Cancel" };
    private readonly Button _btnClose = new() { Content = "Close" };
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _completed;

    private ImageEditorWindow(IImageEditorBackend backend)
    {
        _backend = backend;
        Title = backend.FormatName + " editor";
        Width = 900;
        Height = 620;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        BuildUi();
        Closing += async (_, args) =>
        {
            if (_busy)
            {
                _cancellation?.Cancel();
                args.Cancel = true;
                return;
            }
            if (_backend.OperationCount > 0 &&
                await AppDialog.ConfirmAsync(_backend.CloseMessage($"{_backend.OperationCount:N0}"),
                    "Close " + _backend.FormatName + " editor?") != AppDialogResult.Yes)
                args.Cancel = true;
        };
        Opened += async (_, _) => await LoadEntriesAsync();
    }

    public static async Task<bool> ShowAsync(Window owner, IImageEditorBackend backend)
    {
        var window = new ImageEditorWindow(backend);
        bool? result = await window.ShowDialog<bool?>(owner);
        return result == true;
    }

    private void BuildUi()
    {
        _grid.SetColumns([
            new TableColumn { Name = "Type", Header = "Type", FixedWidth = 100 },
            new TableColumn { Name = "Path", Header = "Path", Weight = 62 },
            new TableColumn { Name = "Size", Header = "Size", Weight = 20, RightAligned = true }
        ]);
        _grid.SelectionChanged += (_, _) => UpdateState();

        _lblImage.Text = _backend.ImagePath;
        _btnReplace.Click += async (_, _) => await ReplaceAsync();
        _btnAddFiles.Click += async (_, _) => await AddFilesAsync();
        _btnAddFolder.Click += async (_, _) => await AddFolderAsync();
        _btnNewDirectory.Click += async (_, _) => await NewDirectoryAsync();
        _btnDelete.Click += async (_, _) => await DeleteAsync();
        _btnUndo.Click += (_, _) =>
        {
            if (_backend.OperationCount == 0) return;
            _backend.RemoveLastOperation();
            if (_changes.ItemCount > 0) _changes.Items.RemoveAt(_changes.ItemCount - 1);
            UpdateState();
        };
        _btnApply.Click += async (_, _) => await ApplyAsync();
        _btnCancelOperation.Click += (_, _) => _cancellation?.Cancel();
        _btnClose.Click += (_, _) => Close(_completed);

        var toolbar = new WrapPanel { Margin = new Avalonia.Thickness(8) };
        foreach (Button button in new[]
                 { _btnReplace, _btnAddFiles, _btnAddFolder, _btnNewDirectory, _btnDelete, _btnUndo, _btnApply,
                   _btnCancelOperation, _btnClose })
        {
            button.Margin = new Avalonia.Thickness(0, 0, 6, 0);
            toolbar.Children.Add(button);
        }

        var targetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(8, 0, 8, 4)
        };
        targetRow.Children.Add(new TextBlock { Text = "Target folder:", VerticalAlignment = VerticalAlignment.Center });
        _target.Width = 620;
        targetRow.Children.Add(_target);

        var layout = new DockPanel();
        var header = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(8, 8, 8, 4) };
        header.Children.Add(_lblImage);
        header.Children.Add(targetRow);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(toolbar);

        var footer = new StackPanel { Spacing = 3, Margin = new Avalonia.Thickness(8) };
        footer.Children.Add(_progress);
        footer.Children.Add(_lblStatus);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var changesPanel = new DockPanel { Height = 120 };
        var changesLabel = new TextBlock { Text = "Queued changes", Margin = new Avalonia.Thickness(8, 0) };
        DockPanel.SetDock(changesLabel, Dock.Top);
        changesPanel.Children.Add(changesLabel);
        changesPanel.Children.Add(_changes);
        DockPanel.SetDock(changesPanel, Dock.Bottom);
        layout.Children.Add(changesPanel);

        layout.Children.Add(_grid);
        Content = layout;
    }

    private async Task LoadEntriesAsync()
    {
        try
        {
            IReadOnlyList<ImageEditorEntry> entries = await _backend.LoadEntriesAsync();
            _grid.SetRows(entries.Select(entry => new TableRow
            {
                Cells =
                [
                    entry.IsDirectory ? "Directory" : "File", entry.Path,
                    entry.IsDirectory ? string.Empty : entry.Size.ToString("N0")
                ],
                Tag = entry
            }).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   ArgumentException)
        {
            await AppDialog.ShowErrorAsync(ex.Message, _backend.FormatName + " editor");
        }
    }

    private ImageEditorEntry? SelectedEntry() =>
        _grid.SelectedRows.Count == 1 && _grid.SelectedRows[0].Tag is ImageEditorEntry entry ? entry : null;

    private async Task ReplaceAsync()
    {
        if (SelectedEntry() is not { IsDirectory: false } selected)
        {
            await AppDialog.ShowWarningAsync("Select one file to replace.", _backend.FormatName + " editor");
            return;
        }
        string? source = await PickOpenFileAsync("Select the replacement file");
        if (source is null) return;
        _backend.QueueReplace(selected.Path, source);
        QueueDescription($"Replace  {selected.Path}  ←  {source}");
    }

    private async Task AddFilesAsync()
    {
        string parent = NormalizeOptionalDirectory();
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files =
            await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select files to add",
                AllowMultiple = true
            });
        foreach (Avalonia.Platform.Storage.IStorageFile file in files)
        {
            string? source = file.TryGetLocalPath();
            if (source is null) continue;
            string target = CombineImagePath(parent, Path.GetFileName(source));
            _backend.QueueAddFile(target, source);
            QueueDescription($"Add file  {target}  ←  {source}");
        }
    }

    private async Task AddFolderAsync()
    {
        string parent = NormalizeOptionalDirectory();
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders =
            await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select a folder to add",
                AllowMultiple = false
            });
        if (folders.Count == 0) return;
        string? source = folders[0].TryGetLocalPath();
        if (source is null) return;
        string name = new DirectoryInfo(source).Name;
        string target = CombineImagePath(parent, name);
        _backend.QueueAddFolder(target, source);
        QueueDescription($"Add tree  {target}  ←  {source}");
    }

    private async Task NewDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(_target.Text))
        {
            await AppDialog.ShowWarningAsync(_backend.InvalidPathMessage, _backend.FormatName + " editor");
            return;
        }
        try
        {
            string target = _backend.NormalizePath(_target.Text);
            _backend.QueueNewDirectory(target);
            QueueDescription($"New directory  {target}");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            await AppDialog.ShowErrorAsync(ex.Message, "Invalid internal path");
        }
    }

    private async Task DeleteAsync()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            await AppDialog.ShowWarningAsync("Select one or more files or directories to delete.",
                _backend.FormatName + " editor");
            return;
        }
        string[] paths = _grid.SelectedRows
            .Select(row => row.Tag).OfType<ImageEditorEntry>()
            .Select(entry => entry.Path)
            .OrderBy(path => path.Count(character => character == '/')).ToArray();
        paths = paths.Where(path => !paths.Any(parent =>
                !parent.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith(parent.TrimEnd('/') + '/', StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        string message = $"Queue deletion of {paths.Length:N0} selected item(s)?\n\n" +
                         string.Join("\n", paths.Take(12)) +
                         (paths.Length > 12 ? "\n..." : string.Empty);
        if (await AppDialog.ConfirmAsync(message, $"Queue {_backend.FormatName} deletion?") !=
            AppDialogResult.Yes) return;
        foreach (string path in paths)
        {
            _backend.QueueDelete(path);
            QueueDescription($"Delete  {path}");
        }
    }

    private void QueueDescription(string description)
    {
        _changes.Items.Add(description);
        UpdateState();
    }

    private async Task ApplyAsync()
    {
        if (_backend.OperationCount == 0) return;
        if (await AppDialog.ConfirmAsync(
                $"Apply {_backend.OperationCount:N0} queued change(s)?\n\n{_backend.ApplyMessage}",
                $"Apply {_backend.FormatName} changes?") != AppDialogResult.Yes) return;

        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var progress = new Progress<(string Stage, long Processed, long Total)>(value =>
            {
                int percentage = value.Total <= 0
                    ? 0
                    : (int)Math.Round(value.Processed * 100.0 / value.Total);
                _progress.Value = Math.Clamp(percentage, _progress.Minimum, _progress.Maximum);
                _lblStatus.Text = $"{value.Stage} - {FormatBytes(value.Processed)} / {FormatBytes(value.Total)}";
            });
            ImageEditorApplyResult result = await _backend.ApplyAsync(progress, _cancellation.Token);
            _progress.Value = _progress.Maximum;
            _lblStatus.Text = $"Completed and verified {result.FileCount:N0} files.";
            await AppDialog.ShowInfoAsync(
                $"All {_backend.FormatName} changes were applied successfully.\n\n" +
                $"Mode: {result.Mode}\n" +
                $"Files: {result.FileCount:N0}\n" +
                $"Directories: {result.DirectoryCount:N0}\n" +
                $"Manifest SHA-256: {result.ManifestHash}",
                _backend.FormatName + " edit complete");
            _completed = true;
            _changes.Items.Clear();
            await LoadEntriesAsync();
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "Operation cancelled; the original image was preserved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   ArgumentException)
        {
            _lblStatus.Text = "No unverified changes were retained.";
            await AppDialog.ShowErrorAsync(ex.Message, _backend.FormatName + " edit failed");
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateState();
    }

    private void UpdateState()
    {
        bool idle = !_busy;
        ImageEditorEntry? selected = SelectedEntry();
        _grid.IsEnabled = idle;
        _target.IsEnabled = idle;
        _btnReplace.IsEnabled = idle && selected is { IsDirectory: false };
        _btnAddFiles.IsEnabled = idle;
        _btnAddFolder.IsEnabled = idle;
        _btnNewDirectory.IsEnabled = idle;
        _btnDelete.IsEnabled = idle && _grid.SelectedRows.Count > 0;
        _btnUndo.IsEnabled = idle && _backend.OperationCount > 0;
        _btnApply.IsEnabled = idle && _backend.OperationCount > 0;
        _btnCancelOperation.IsEnabled = _busy;
        _btnClose.IsEnabled = idle;
    }

    private string NormalizeOptionalDirectory() =>
        string.IsNullOrWhiteSpace(_target.Text) ? string.Empty : _backend.NormalizePath(_target.Text);

    private string CombineImagePath(string parent, string name) =>
        _backend.NormalizePath(parent.Length == 0 ? name : parent + "/" + name);

    private static string FormatBytes(long value)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        double amount = Math.Max(0, value);
        int suffix = 0;
        while (amount >= 1024 && suffix < suffixes.Length - 1) { amount /= 1024; suffix++; }
        return suffix == 0 ? $"{amount:N0} {suffixes[suffix]}" : $"{amount:N2} {suffixes[suffix]}";
    }

    private async Task<string?> PickOpenFileAsync(string title)
    {
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files =
            await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
