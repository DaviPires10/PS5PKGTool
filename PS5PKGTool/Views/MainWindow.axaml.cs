using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PS5PKGTool.Core.Models;
using PS5PKGTool.Core.Services;
using PS5PKGTool.Core.Tasks;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

/// <summary>
/// The Avalonia counterpart of the WinForms <c>MainForm</c>. The window shell, scanning lifecycle,
/// settings plumbing and shared helpers live here; each area of the UI is a sibling partial file
/// (<c>MainWindow.Library.cs</c>, <c>MainWindow.Tasks.cs</c>, ...) so the port keeps the same shape
/// as the original form.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppStateStore _stateStore = new();
    private readonly Ps5LibraryScanner _scanner = new();
    private readonly Ps5DetailsLoader _detailsLoader = new();
    private readonly Dictionary<string, Ps5GameDetails> _detailsCache = new(PathIdentity.Comparer);
    private readonly PackageTaskQueue _taskQueue = new();

    private AppSettings _settings = new();
    private string? _settingsWarning;
    private List<Ps5GameInfo> _games = [];
    private List<Ps5GameInfo> _visibleGames = [];

    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _detailCancellation;
    private CancellationTokenSource? _dumpSizeCancellation;
    private CancellationTokenSource? _fileCancellation;
    private CancellationTokenSource? _filePreviewCancellation;

    private Ps5GameInfo? _selectedGame;
    private string _currentGameRoot = string.Empty;
    private bool _currentSourceIsContainer;
    private bool _isScanning;
    private int _scanGeneration;
    private bool _isFileBusy;
    private bool _libraryColumnsReady;
    private long _hexPreviewOffset;

    private readonly string _previewDirectory = Path.Combine(Path.GetTempPath(), "PS5PKGTool", "Preview",
        Environment.ProcessId.ToString());
    private readonly string? _pendingExternalPath;

    private const string KoFiUrl = "https://ko-fi.com/R6R524N7X";
    private const string PayPalUrl = "https://www.paypal.com/paypalme/pearlxcoree";

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? externalPath)
    {
        InitializeComponent();
        Title = $"PS5 PKG Tool v{UpdateChecker.AppVersion()}";
        _pendingExternalPath = externalPath;

        // Load preferences before initializing any consumer (builder combo, saved-views menu, task
        // list) so those controls start from the persisted values rather than built-in defaults.
        SettingsLoadResult load = _stateStore.LoadSettingsWithDiagnostics();
        _settings = load.Settings;
        _settingsWarning = load.Warning;

        InitializeLog();
        InitializeTaskQueue();
        InitializeTasksLayout();
        WireTaskEvents();
        InitializeLibraryTools();
        InitializeDetails();
        RefreshImageTools();
        InitializeImageSdkList();
        InitializeImageBuildLists();
        InitializeCenteredImageTabs();
        WireImageToolEvents();
        ApplyDefaultCredentials();

        WireShellEvents();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void WireShellEvents()
    {
        menuAddFolder.Click += AddFolder_Click;
        menuOpenDump.Click += OpenDump_Click;
        menuOpenPackage.Click += OpenPackage_Click;
        menuRefresh.Click += Refresh_Click;
        menuSaveManifest.Click += SaveManifest_Click;
        menuEmptyList.Click += EmptyList_Click;
        menuRemoveMissing.Click += RemoveMissing_Click;
        menuSettings.Click += Settings_Click;
        menuExit.Click += (_, _) => Close();
        menuAbout.Click += About_Click;
        menuHelpCheckUpdate.Click += (_, _) => _ = CheckForUpdatesAsync(manual: true);
        menuHelpCheckUpdatesOnStartup.Click += CheckUpdatesOnStartup_Click;
        menuHelpKofi.Click += (_, _) => OpenExternalUrl(KoFiUrl);
        menuHelpPayPal.Click += (_, _) => OpenExternalUrl(PayPalUrl);

        btnFileCancel.Click += (_, _) =>
        {
            if (!_isFileBusy) return;
            _fileCancellation?.Cancel();
            SetStatus("Cancelling extraction...");
        };

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            chkLogAutoScroll.IsChecked = _settings.LogAutoScroll;
            RestoreWindowBounds();
            ApplyRuntimeSettings();
            ApplyDefaultGrouping();
            SyncFilterControls();
            RebuildRecentMenu();

            var configured = new List<Ps5GameInfo>();
            foreach (Ps5GameInfo game in _stateStore.LoadManifest().Games)
            {
                if (string.IsNullOrWhiteSpace(game.RootPath)) continue;
                if (game.SourceKind == Ps5SourceKind.FilesystemImage &&
                    !Path.GetExtension(game.RootPath).Equals(".exfat", StringComparison.OrdinalIgnoreCase)) continue;
                if (game.SourceKind == Ps5SourceKind.Ffpkg &&
                    !Path.GetExtension(game.RootPath).Equals(".ffpkg", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsUnderLibrary(game.RootPath) || IsManualSource(game.RootPath)) configured.Add(game);
            }
            _games = configured.OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            _stateStore.SaveManifest(_games);
            Logger.Info($"Loaded cached library: {_games.Count:N0} game(s).");
            ApplyFilter();
            int missing = _games.Count(game => !SourceExists(game));
            if (_games.Count == 0)
                SetStatus("Add a PS5 dump, PKG, FFPFSC, FFPKG, or exFAT library folder, then choose Refresh.");
            else if (missing > 0)
                SetStatus($"Loaded cached library. {missing:N0} item(s) not found; choose Refresh to rescan.");
            else
                SetStatus("Loaded cached library. Choose Refresh to rescan folders.");

            if (!string.IsNullOrWhiteSpace(_pendingExternalPath))
            {
                string path = _pendingExternalPath;
                if (File.Exists(path) || Directory.Exists(path))
                {
                    Logger.Info($"Opening external path: {path}");
                    AddRecentFolder(path);
                    RememberManualSource(path);
                    await ScanAsync([path], merge: true);
                }
            }
            else if (_settings.RefreshOnStartup && ScanRoots().Count > 0)
            {
                await ScanAsync(ScanRoots(), merge: false);
            }

            int missingNow = _games.Count(game => !SourceExists(game));
            if (missingNow > 0) NotifyMissingSources(missingNow);

            if (!string.IsNullOrWhiteSpace(_settingsWarning))
            {
                string warning = _settingsWarning;
                _settingsWarning = null;
                await AppDialog.ShowWarningAsync(warning, "Settings");
            }

            // Fill in dump folder sizes in the background so the Size column is populated without
            // opening each dump.
            _dumpSizeCancellation = new CancellationTokenSource();
            _ = ComputeMissingDumpSizesAsync(_dumpSizeCancellation.Token);

            // Mirror PkgViewer: check for a newer release once, only speaking up when one exists.
            menuHelpCheckUpdatesOnStartup.IsChecked = _settings.CheckUpdatesOnStartup;
            if (_settings.CheckUpdatesOnStartup) _ = CheckForUpdatesAsync(manual: false);
        }
        catch (Exception ex)
        {
            Logger.Exception("Main window startup", ex);
            SetStatus("The library could not be loaded. See the Log tab for details.");
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ShutdownTaskQueue();
        ShutdownLog();
        CaptureLibraryColumnLayout();
        SaveSettingsQuietly();
        SaveWindowBounds();
        _scanCancellation?.Cancel();
        _detailCancellation?.Cancel();
        _dumpSizeCancellation?.Cancel();
        _fileCancellation?.Cancel();
        _filePreviewCancellation?.Cancel();
        CleanupDetails();
        _scanCancellation?.Dispose();
        _detailCancellation?.Dispose();
        _dumpSizeCancellation?.Dispose();
        _fileCancellation?.Dispose();
        _filePreviewCancellation?.Dispose();
        try
        {
            if (Directory.Exists(_previewDirectory)) Directory.Delete(_previewDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------------------------------
    // Window state and presentation
    // ------------------------------------------------------------------------------------------

    private void RestoreWindowBounds()
    {
        if (_settings.WindowWidth >= 800 && _settings.WindowHeight >= 600)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        ClampToScreen();
    }

    /// <summary>
    /// Keeps the window inside the display that hosts it. The XAML default (1400x880) is wider than
    /// many laptop screens, which used to push the right-hand controls off the desktop.
    /// </summary>
    private void ClampToScreen()
    {
        if (WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        double availableWidth = screen.WorkingArea.Width / scaling - 24;
        double availableHeight = screen.WorkingArea.Height / scaling - 24;
        if (availableWidth <= 0 || availableHeight <= 0) return;
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
    }

    private void SaveWindowBounds()
    {
        if (WindowState == WindowState.Normal)
        {
            if (Width >= 800 && Height >= 600)
            {
                _settings.WindowWidth = (int)Width;
                _settings.WindowHeight = (int)Height;
            }
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        SaveSettingsQuietly();
    }

    private void ApplyRuntimeSettings()
    {
        ThemeManager.Apply(_settings.Theme);
        gridLibrary.RowHeight = Math.Max(16, _settings.GridRowHeight);
        gridLibrary.ShowGridLines = _settings.ShowGridLines;
        RefreshLibraryPresentation();
        SelectBackendFromSettings();
        SelectExtractBackendFromSettings();
        SetFilePreviewPaneVisible(_settings.ShowFilePreview);
    }

    /// <summary>
    /// Shows or hides the inline preview pane in the Files tab. The pane's splitter and viewer columns
    /// collapse to zero so the file list takes the full width.
    /// </summary>
    private void SetFilePreviewPaneVisible(bool visible)
    {
        sectionFileViewer.IsVisible = visible;
        splitFileContentPreview.ColumnDefinitions[1].Width = visible ? new GridLength(5) : new GridLength(0);
        splitFileContentPreview.ColumnDefinitions[2].Width = visible ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
    }

    private async Task ComputeMissingDumpSizesAsync(CancellationToken token)
    {
        List<Ps5GameInfo> pending = _games
            .Where(game => game.SourceKind == Ps5SourceKind.LooseDump && game.SourceSize <= 0 && SourceExists(game))
            .ToList();
        if (pending.Count == 0) return;

        Logger.Info($"Measuring {pending.Count:N0} dump folder(s).");
        bool changed = false;
        foreach (Ps5GameInfo game in pending)
        {
            if (token.IsCancellationRequested) return;
            long size;
            try
            {
                string root = game.RootPath;
                size = await Task.Run(() => MeasureDirectory(root, token), token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Unable to measure '{game.RootPath}': {ex.Message}");
                continue;
            }
            if (size <= 0) continue;
            game.SourceSize = size;
            RefreshLibrarySizeCell(game);
            changed = true;
        }

        if (changed)
        {
            _stateStore.SaveManifest(_games);
            Logger.Info("Dump folder sizes updated.");
        }
    }

    private static long MeasureDirectory(string root, CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*", options))
        {
            token.ThrowIfCancellationRequested();
            try { total = checked(total + new FileInfo(path).Length); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    private void NotifyMissingSources(int missing)
    {
        string[] names = _games
            .Where(game => !SourceExists(game))
            .Select(game => string.IsNullOrWhiteSpace(game.Title) ? Path.GetFileName(game.RootPath) : game.Title)
            .Take(8)
            .ToArray();
        var message = new StringBuilder();
        message.Append(missing == 1
            ? "1 library item could not be found (deleted, moved, or its drive is offline):"
            : $"{missing:N0} library items could not be found (deleted, moved, or their drive is offline):");
        message.AppendLine();
        message.AppendLine();
        foreach (string name in names) message.AppendLine("  - " + name);
        if (missing > names.Length)
            message.AppendLine($"  ... and {missing - names.Length:N0} more");
        message.AppendLine();
        message.Append("They are marked as Missing until the next Refresh. Use File > Remove Missing Items to drop them now.");
        AppDialog.ShowWarning(message.ToString(), "Missing library items");
    }

    // ------------------------------------------------------------------------------------------
    // File menu and scanning
    // ------------------------------------------------------------------------------------------

    private async void AddFolder_Click(object? sender, EventArgs e)
    {
        IReadOnlyList<string> folders = await PickFoldersAsync("Select one or more PS5 library folders");
        if (folders.Count == 0) return;
        bool changed = false;
        foreach (string selected in folders)
        {
            if (!_settings.LibraryFolders.Contains(selected, PathIdentity.Comparer))
            {
                _settings.LibraryFolders.Add(selected);
                changed = true;
            }
            AddRecentFolder(selected);
        }
        if (changed) _stateStore.SaveSettings(_settings);
        await ScanAsync(ScanRoots(), merge: false);
    }

    private async void OpenDump_Click(object? sender, EventArgs e)
    {
        IReadOnlyList<string> folders = await PickFoldersAsync(
            "Select the root of one or more unpacked PS5 game dumps");
        if (folders.Count == 0) return;
        foreach (string folder in folders)
        {
            AddRecentFolder(folder);
            RememberManualSource(folder);
        }
        await ScanAsync(folders, merge: true);
    }

    private async void OpenPackage_Click(object? sender, EventArgs e)
    {
        IReadOnlyList<string> files = await PickFilesAsync("Open PS5 containers or images",
            new FilePickerFileType("PS5 packages and images") { Patterns = ["*.pkg", "*.ffpkg", "*.ffpfsc", "*.exfat", "*.ffpfs"] });
        if (files.Count == 0) return;
        foreach (string file in files)
        {
            AddRecentFolder(file);
            RememberManualSource(file);
        }
        await ScanAsync(files, merge: true);
    }

    private async void Refresh_Click(object? sender, EventArgs e)
    {
        IReadOnlyList<string> roots = ScanRoots();
        Logger.Info($"Refresh requested for {roots.Count} root(s): {string.Join("; ", roots)}");
        if (roots.Count == 0)
        {
            await AppDialog.ShowInfoAsync("Add at least one PS5 dump library folder in Settings.", "PS5 PKG Tool");
            return;
        }
        await ScanAsync(roots, merge: false);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private async Task<IReadOnlyList<string>> PickFoldersAsync(string title)
    {
        IReadOnlyList<IStorageFolder> result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = true
        });
        return result.Select(folder => folder.TryGetLocalPath()).OfType<string>().ToList();
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync(string title, params FilePickerFileType[] filters)
    {
        IReadOnlyList<IStorageFile> result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [.. filters, FilePickerFileTypes.All]
        });
        return result.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
    }

    private async Task<string?> PickFileAsync(string title, params FilePickerFileType[] filters)
    {
        IReadOnlyList<IStorageFile> result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [.. filters, FilePickerFileTypes.All]
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveFileAsync(string title, string suggestedName, params FilePickerFileType[] filters)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [.. filters, FilePickerFileTypes.All],
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            if (menuRefresh.IsEnabled) Refresh_Click(this, EventArgs.Empty);
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            // Ctrl+F searches whichever workspace is active.
            if (tabsWorkspace.SelectedItem == tabTasks) searchTasks.Focus();
            else searchLibrary.Focus();
        }
        else if (e.Key == Key.Escape && searchLibrary.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            searchLibrary.SearchText = string.Empty;
        }
        else if (e.Key == Key.Escape && _isFileBusy)
        {
            e.Handled = true;
            _fileCancellation?.Cancel();
            SetStatus("Cancelling extraction...");
        }
        else if (e.Key == Key.Escape && _isScanning)
        {
            e.Handled = true;
            _scanCancellation?.Cancel();
            SetStatus("Cancelling library refresh...");
        }
    }

    private async Task ScanAsync(IEnumerable<string> folders, bool merge)
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        // Own this scan; a newer scan supersedes an older one, which must not apply results or clear
        // the newer scan's busy state when it finishes.
        int generation = ++_scanGeneration;
        SetScanning(true);
        SetStatus("Scanning for PS5 dumps, packages, and filesystem images...");
        var progress = new Progress<Ps5ScanProgress>(value =>
        {
            if (value.IsDiscovery)
            {
                string path = value.CurrentPath;
                if (path.Length > 100) path = "..." + path[^100..];
                SetStatus(path.Length == 0
                    ? "Scanning for PS5 dumps, packages, and filesystem images..."
                    : $"Scanning {path}");
                return;
            }
            SetStatus(value.Total == 0
                ? "Searching for PS5 dumps, packages, and filesystem images..."
                : string.IsNullOrEmpty(value.CurrentPath)
                    ? $"Reading PS5 sources... {value.Processed:N0} of {value.Total:N0}"
                    : $"Reading {value.Processed + 1:N0} of {value.Total:N0}: {Path.GetFileName(value.CurrentPath)}");
        });

        try
        {
            Ps5ScanResult result = await _scanner.ScanAsync(folders, _settings.RecursiveScan, _games, progress,
                _scanCancellation.Token);
            if (generation != _scanGeneration) return; // a newer scan owns the library now
            Logger.Info($"Scan finished: {result.Games.Count} game(s), {result.Errors.Count} warning(s).");
            if (merge)
            {
                var combined = _games.Concat(result.Games)
                    .GroupBy(game => game.RootPath, PathIdentity.Comparer)
                    .Select(group => group.Last())
                    .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase);
                _games = combined.ToList();
            }
            else
            {
                _games = result.Games;
                _detailsCache.Clear();
            }
            _stateStore.SaveManifest(_games);
            ApplyFilter();
            SetStatus(result.Errors.Count == 0
                ? $"Library refresh complete: {_games.Count:N0} game(s)."
                : $"Refresh complete with {result.Errors.Count:N0} warning(s).");
            if (result.Errors.Count > 0)
                ShowTextReport("PS5 scan warnings",
                    $"The scan finished with {result.Errors.Count:N0} warning(s):" + Environment.NewLine +
                    Environment.NewLine + string.Join(Environment.NewLine, result.Errors));
        }
        catch (OperationCanceledException)
        {
            if (generation == _scanGeneration) SetStatus("Library refresh cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation == _scanGeneration)
            {
                SetStatus("Library refresh failed.");
                await AppDialog.ShowErrorAsync(ex.Message, "PS5 library refresh");
            }
        }
        finally
        {
            if (generation == _scanGeneration) SetScanning(false);
        }
    }

    private void SetScanning(bool scanning)
    {
        _isScanning = scanning;
        UpdateOperationState();
    }

    private void UpdateOperationState()
    {
        bool idle = !_isScanning && !_isFileBusy;
        bool tasksRunning = _taskQueue.Tasks.Any(task =>
            task.Status is PackageTaskStatus.Running or PackageTaskStatus.Queued);
        statusBusy.IsVisible = !idle || tasksRunning;
        menuAddFolder.IsEnabled = idle;
        menuOpenDump.IsEnabled = idle;
        menuOpenPackage.IsEnabled = idle;
        menuRefresh.IsEnabled = idle;
        menuSettings.IsEnabled = idle;
        btnImageRun.IsEnabled = idle && cboImageAction.SelectedItem is not null;
        btnImageCancel.IsEnabled = tasksRunning;
        btnFileCancel.IsEnabled = _isFileBusy;
    }

    private void SetSelectedGame(Ps5GameInfo? game)
    {
        _selectedGame = game;
        UpdateOperationState();
    }

    // ------------------------------------------------------------------------------------------
    // File menu commands
    // ------------------------------------------------------------------------------------------

    private void SaveManifest_Click(object? sender, EventArgs e)
    {
        _stateStore.SaveManifest(_games);
        Logger.Info($"Manifest saved with {_games.Count:N0} game(s).");
        SetStatus($"Saved manifest with {_games.Count:N0} game(s).");
    }

    private async void EmptyList_Click(object? sender, EventArgs e)
    {
        if (_games.Count == 0)
        {
            await AppDialog.ShowInfoAsync("The library is already empty.", "Empty list");
            return;
        }
        AppDialogResult result = await AppDialog.ConfirmAsync(
            "Clear the current library list?\n\nThe cached manifest is emptied, so the list stays empty until you refresh. " +
            "Files on disk are not deleted.", "Empty list");
        if (result != AppDialogResult.Yes) return;

        _scanCancellation?.Cancel();
        _detailCancellation?.Cancel();
        _games = [];
        _visibleGames = [];
        _detailsCache.Clear();
        _stateStore.SaveManifest(_games);
        SetSelectedGame(null);
        ClearDetails();
        ApplyFilter();
        statusCount.Text = "0 games";
        SetStatus("Library list emptied. Use Refresh to scan again.");
        Logger.Info("Library list emptied.");
    }

    private async void RemoveMissing_Click(object? sender, EventArgs e)
    {
        int removed = _games.RemoveAll(game => !SourceExists(game));
        if (removed == 0)
        {
            await AppDialog.ShowInfoAsync("No cached library items are missing.", "Remove missing items");
            return;
        }
        _stateStore.SaveManifest(_games);
        ApplyFilter();
        SetStatus($"Removed {removed:N0} missing item(s).");
    }

    // ------------------------------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------------------------------

    private async void Settings_Click(object? sender, EventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        // The settings dialog owns its draft and calls back to persist it, so Cancel never writes.
        SettingsDialogResult? result = await SettingsWindow.ShowAsync(this, _settings, SaveSettingsFromDialog);
        if (result is null) return;

        AppSettings previous = _settings;
        // Import can change manual sources without touching library folders, so compare both.
        bool libraryChanged =
            !previous.LibraryFolders.SequenceEqual(result.Settings.LibraryFolders, PathIdentity.Comparer) ||
            !previous.ManualSources.SequenceEqual(result.Settings.ManualSources, PathIdentity.Comparer) ||
            previous.RecursiveScan != result.Settings.RecursiveScan;
        bool presentationChanged = previous.ShowThumbnails != result.Settings.ShowThumbnails ||
                                   previous.DefaultGroupBy != result.Settings.DefaultGroupBy ||
                                   previous.GridRowHeight != result.Settings.GridRowHeight;
        bool layoutChanged = !previous.LibraryColumnOrder.SequenceEqual(result.Settings.LibraryColumnOrder) ||
                             !previous.LibraryHiddenColumns.SequenceEqual(result.Settings.LibraryHiddenColumns) ||
                             !previous.LibrarySortKeys.SequenceEqual(result.Settings.LibrarySortKeys);
        _settings = result.Settings;
        UpdateWorkspaceLabels();
        ApplyRuntimeSettings();
        ApplyDefaultGrouping();
        SyncFilterControls();
        RebuildRecentMenu();
        RebuildLibraryViewsMenu();
        if (result.ResetLayout) ResetLibraryColumnLayout();
        else if (layoutChanged) ReconcileLibraryLayout();
        if (result.ClearCaches) ClearRuntimeCaches();
        if (presentationChanged) ApplyFilter();

        if (!libraryChanged)
        {
            SetStatus("Settings saved.");
            return;
        }
        if (ScanRoots().Count == 0)
        {
            _games = [];
            _stateStore.SaveManifest(_games);
            _detailsCache.Clear();
            ApplyFilter();
            SetStatus("All library folders were removed.");
            return;
        }
        SetStatus("Settings saved. Rescanning configured folders...");
        await ScanAsync(ScanRoots(), merge: false);
    }

    /// <summary>Persists the dialog's draft, returning the failure reason so the dialog can offer Retry.</summary>
    private (bool Success, string? Error) SaveSettingsFromDialog(AppSettings settings)
    {
        try
        {
            _stateStore.SaveSettings(settings);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Re-applies an imported or reset column layout to the live grid, so the layout captured when the
    /// window closes cannot write the old arrangement back over the new preference.
    /// </summary>
    private void ReconcileLibraryLayout()
    {
        if (!_libraryColumnsReady) return;
        ApplyLibraryColumnVisibility();
        ApplyLibraryColumnWidths();
        RestoreLibraryColumnLayout();
        LoadLibrarySortKeys();
        ApplyFilter();
    }

    private void ResetLibraryColumnLayout()
    {
        _settings.LibraryColumnOrder = [];
        _settings.LibraryHiddenColumns = [];
        _settings.LibraryColumnWeights = [];
        _settings.LibraryColumnWidths = [];
        if (_libraryColumnsReady)
        {
            _libraryColumnsReady = false;
            EnsureLibraryColumns();
            ApplyFilter();
        }
        SetStatus("Column layout reset.");
    }

    private void ClearRuntimeCaches()
    {
        // Cancel in-flight reads so they cannot repopulate the caches just cleared.
        _detailCancellation?.Cancel();
        _filePreviewCancellation?.Cancel();
        _detailsCache.Clear();
        ClearLibraryThumbnails();
        SetStatus("Details and thumbnail caches cleared. Sources and settings are untouched.");
    }

    // ------------------------------------------------------------------------------------------
    // Help, links and updates
    // ------------------------------------------------------------------------------------------

    private async void About_Click(object? sender, EventArgs e) =>
        await AboutWindow.ShowAsync(this, UpdateChecker.AppVersion());

    private void OpenExternalUrl(string url)
    {
        if (!PlatformService.OpenUrl(url))
            AppDialog.ShowError($"Unable to open {url}", "Open link");
    }

    private void CheckUpdatesOnStartup_Click(object? sender, EventArgs e)
    {
        _settings.CheckUpdatesOnStartup = menuHelpCheckUpdatesOnStartup.IsChecked;
        SaveSettingsQuietly();
    }

    /// <summary>
    /// Checks GitHub for a newer release. A manual check always reports the outcome; the automatic
    /// startup check only speaks up when a newer version exists.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        try
        {
            if (manual) SetStatus("Checking for updates...");
            Logger.Info($"Update check start (manual={manual}).");
            UpdateCheck? check = await UpdateChecker.CheckAsync();
            if (check is null)
            {
                if (manual) await AppDialog.ShowInfoAsync("No published releases were found.", "Check for updates");
                return;
            }

            string current = UpdateChecker.AppVersion();
            if (!check.IsNewerThan(current))
            {
                Logger.Info($"Update check: up to date (running {current}, latest {check.Latest}).");
                if (manual) await AppDialog.ShowInfoAsync("PS5 PKG Tool is up to date.", "Check for updates");
                return;
            }

            Logger.Info($"Update check: newer version available ({check.Latest}, running {current}).");
            if (await UpdateAvailableWindow.ShowAsync(this, check, current))
                OpenExternalUrl(check.PageUrl);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or
                                   System.Text.Json.JsonException)
        {
            Logger.Exception("Update check", ex);
            if (manual) await AppDialog.ShowErrorAsync("Could not check for updates.\n\n" + ex.Message, "Check for updates");
        }
        finally
        {
            if (manual) SetStatus("Ready");
        }
    }

    // ------------------------------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------------------------------

    private void SetStatus(string text) => statusLabel.Text = text;

    private void AddRecentFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _settings.RecentFolders.RemoveAll(existing => PathIdentity.Equals(existing, path));
        _settings.RecentFolders.Insert(0, path);
        if (_settings.RecentFolders.Count > 10)
            _settings.RecentFolders.RemoveRange(10, _settings.RecentFolders.Count - 10);
        SaveSettingsQuietly();
        RebuildRecentMenu();
    }

    private IReadOnlyList<string> ScanRoots() => _settings.LibraryFolders
        .Concat(_settings.ManualSources)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(PathIdentity.Comparer)
        .ToArray();

    private bool IsUnderLibrary(string path)
    {
        foreach (string folder in _settings.LibraryFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            if (PathIdentity.IsSameOrUnder(path, folder))
                return true;
        }
        return false;
    }

    private bool IsManualSource(string path) =>
        _settings.ManualSources.Contains(path, PathIdentity.Comparer);

    private void RememberManualSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!_settings.ManualSources.Contains(path, PathIdentity.Comparer))
        {
            _settings.ManualSources.Add(path);
            SaveSettingsQuietly();
        }
    }

    private void CaptureVolatileSettings()
    {
        // Remember the task list/details split (only while the details panel is shown).
        if (!_settings.TaskDetailsCollapsed && taskDetailLayout.IsVisible)
        {
            double height = taskDetailLayout.Bounds.Height;
            if (height > 0) _settings.TaskSplitterDistance = (int)height;
        }
    }

    /// <summary>Persists preferences, reporting I/O failures so an explicit Save can react to them.</summary>
    private bool TrySaveSettings(out string? error)
    {
        CaptureVolatileSettings();
        try
        {
            _stateStore.SaveSettings(_settings);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private void SaveSettingsQuietly() => TrySaveSettings(out _);

    private void CopyText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        _ = CopyTextAsync(clipboard, value);
        SetStatus("Copied to clipboard.");
    }

    private static async Task CopyTextAsync(IClipboard clipboard, string value)
    {
        try { await clipboard.SetTextAsync(value); }
        catch (Exception ex) { Logger.Warn($"Clipboard copy failed: {ex.Message}"); }
    }

    /// <summary>Runs a callback on the UI thread, whether or not the caller is already on it.</summary>
    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>Opens the folder containing a produced file, cross-platform.</summary>
    private static void OpenOutputFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string target = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        if (Directory.Exists(target)) PlatformService.OpenPath(target);
    }

    private Task<string?> PromptTextAsync(string title, string prompt, string initial) =>
        TextPromptWindow.AskAsync(this, title, prompt, initial);

    private void ShowTextReport(string title, string body) =>
        _ = TextReportWindow.ShowAsync(this, title, body);

    private static bool SourceExists(Ps5GameInfo game) =>
        game.SourceKind == Ps5SourceKind.LooseDump ? Directory.Exists(game.RootPath) : File.Exists(game.RootPath);

    private static string MakeSafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        result = result.Trim(' ', '.');
        return result.Length == 0 ? "PS5_GAME" : result;
    }

    private static bool IsFfpfscOperationException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or
            InvalidOperationException or NotSupportedException or OverflowException;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FindAvailableDirectory(string preferredPath)
    {
        string fullPath = Path.GetFullPath(preferredPath);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath)) return fullPath;
        for (int index = 2; index < 10_000; index++)
        {
            string candidate = fullPath + $" ({index})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to find an available extraction directory name.");
    }

    private static string RegionOf(Ps5GameInfo game)
    {
        string id = !string.IsNullOrWhiteSpace(game.ContentId) ? game.ContentId : game.TitleId;
        if (id.Length < 1) return "Unknown";
        return char.ToUpperInvariant(id[0]) switch
        {
            'U' => "Americas",
            'E' => "Europe",
            'J' => "Japan",
            'K' => "Korea",
            'A' => "Asia",
            'H' => "Hong Kong",
            _ => "Other"
        };
    }

    private static string CategoryOf(Ps5GameInfo game)
    {
        string raw = game.ApplicationCategory;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string token = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? raw;
            if (int.TryParse(token, out int number))
            {
                // Only 1 selects additional content; publisher-specific encodings such as 0x01000000
                // are base applications, matching the engine's ProsperoParam normalization.
                return number switch
                {
                    1 => "DLC",
                    2 => "Patch",
                    3 => "App",
                    _ => "Game"
                };
            }
            return raw;
        }
        return game.Package?.ContentType switch
        {
            0x20 => "Game",
            // 0x21/0x22 are PS5 additional content. Patches use the LIH patch-layer envelope.
            0x21 => "DLC",
            0x22 => "DLC",
            _ => game.SourceKind == Ps5SourceKind.LooseDump ? "Game" : "Unknown"
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

/// <summary>The result of the settings dialog: the edited draft plus the one-shot maintenance flags.</summary>
internal sealed record SettingsDialogResult(AppSettings Settings, bool ResetLayout, bool ClearCaches);
