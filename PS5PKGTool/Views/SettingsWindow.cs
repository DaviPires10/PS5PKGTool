using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using PS5PKGTool.Core.Backends;
using PS5PKGTool.Core.Services;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

/// <summary>
/// The Avalonia port of <c>SettingsForm</c>: library/scanning, appearance, naming, files, paths and
/// tools, safety and maintenance pages, with the same draft/validate/import/export/reset contract.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly int[] DensityRowHeights = [18, 22, 28];

    private readonly Func<AppSettings, (bool Success, string? Error)>? _save;
    private readonly string _originalTheme;
    private bool _loading;
    private bool _syncingDensity;
    private bool _syncingRename;

    private readonly TabControl _tabs = new();
    private readonly TabItem _tabLibrary = new() { Header = "Library" };
    private readonly TabItem _tabAppearance = new() { Header = "Appearance" };
    private readonly TabItem _tabNaming = new() { Header = "Naming" };
    private readonly TabItem _tabFiles = new() { Header = "Files & preview" };
    private readonly TabItem _tabPaths = new() { Header = "Paths & tools" };
    private readonly TabItem _tabSafety = new() { Header = "Safety" };
    private readonly TabItem _tabMaintenance = new() { Header = "Maintenance" };

    private readonly CheckBox _chkRecursive = new() { Content = "Scan subfolders" };
    private readonly CheckBox _chkRefreshOnStartup = new() { Content = "Refresh the library on startup" };
    private readonly CheckBox _chkCheckUpdates = new() { Content = "Check for updates on startup" };
    private readonly ListBox _lstFolders = new() { Height = 150 };
    private readonly ListBox _lstManualSources = new() { Height = 120 };
    private readonly ComboBox _cboTheme = new() { Width = 240 };
    private readonly ComboBox _cboDensity = new() { Width = 140 };
    private readonly NumericUpDown _nudRowHeight = new() { Minimum = 16, Maximum = 48, Width = 100 };
    private readonly CheckBox _chkShowThumbnails = new() { Content = "Show library thumbnails" };
    private readonly CheckBox _chkShowGridLines = new() { Content = "Show grid lines" };
    private readonly CheckBox _chkShowFilePreview = new() { Content = "Show the file preview pane in the Files tab" };
    private readonly ComboBox _cboDefaultGroup = new() { Width = 240 };
    private readonly TextBox _txtRenameFormat = new() { Width = 420 };
    private readonly ComboBox _cboRenamePreset = new() { Width = 260 };
    private readonly ComboBox _cboRenameToken = new() { Width = 200 };
    private readonly TextBlock _lblRenamePreview = new() { Classes = { "muted" } };
    private readonly TextBlock _lblRenameUnknown = new() { TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(230, 180, 80)) };
    private readonly NumericUpDown _nudMaxPreview = new() { Minimum = 1, Maximum = 1024, Width = 110 };
    private readonly NumericUpDown _nudHexPage = new() { Minimum = 1, Maximum = 1024, Width = 110 };
    private readonly NumericUpDown _nudThumbnailCache = new() { Minimum = 0, Maximum = 20000, Width = 110 };
    private readonly TextBox _txtOutput = new() { Width = 480 };
    private readonly TextBox _txtWorkspace = new() { Width = 480 };
    private readonly TextBlock _lblWorkspaceFree = new() { Classes = { "muted" } };
    private readonly ComboBox _cboDefaultBackend = new() { Width = 240 };
    private readonly TextBox _txtPasscode = new() { Width = 480, PasswordChar = '●' };
    private readonly CheckBox _chkShowPasscode = new() { Content = "Show passcode" };
    private readonly TextBlock _lblPasscodeHint = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly CheckBox _chkOpenOutput = new() { Content = "Open the output folder after a successful task" };
    private readonly CheckBox _chkConfirmMove = new() { Content = "Confirm before moving library items" };
    private readonly CheckBox _chkConfirmDelete = new() { Content = "Confirm before recycling containers" };
    private readonly CheckBox _chkPermanentDelete = new() { Content = "Delete permanently instead of moving to the trash" };
    private readonly CheckBox _chkExportCredentials = new() { Content = "Include the debug passcode", IsChecked = false };
    private readonly Button _btnSave = new() { Content = "Save", Width = 100, IsDefault = true };

    private SettingsWindow(AppSettings settings, Func<AppSettings, (bool Success, string? Error)>? save)
    {
        Settings = AppSettingsNormalizer.Normalize(Clone(settings));
        _save = save;
        _originalTheme = Settings.Theme;

        Title = "Settings";
        Width = 820;
        Height = 640;
        MinWidth = 700;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        BuildUi();
        LoadToControls();
        Closing += (_, _) =>
        {
            if (!_committed) ThemeManager.Apply(_originalTheme);
        };
    }

    public AppSettings Settings { get; private set; }
    public bool ResetLayout { get; private set; }
    public bool ClearCaches { get; private set; }
    private bool _committed;

    public static async Task<SettingsDialogResult?> ShowAsync(Window owner, AppSettings settings,
        Func<AppSettings, (bool Success, string? Error)> save)
    {
        var dialog = new SettingsWindow(settings, save);
        return await dialog.ShowDialog<SettingsDialogResult?>(owner);
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions)) ?? new AppSettings();

    // ------------------------------------------------------------------------ UI

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static StackPanel Row(params Control[] controls)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 3)
        };
        foreach (Control control in controls) panel.Children.Add(control);
        return panel;
    }

    private static ScrollViewer Page(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(14) };
        foreach (Control control in controls) panel.Children.Add(control);
        return new ScrollViewer { Content = panel };
    }

    private void BuildUi()
    {
        _cboTheme.ItemsSource = ThemeManager.Presets.Select(theme => theme.Name).ToList();
        _cboTheme.SelectionChanged += (_, _) =>
        {
            if (_cboTheme.SelectedItem is string name) ThemeManager.Apply(name);
        };
        _cboDensity.ItemsSource = new[] { "Compact", "Normal", "Comfortable" };
        _cboDensity.SelectionChanged += (_, _) => ApplyDensityPreset();
        _nudRowHeight.ValueChanged += (_, _) => SyncDensityFromRowHeight();
        _cboDefaultGroup.ItemsSource = new[]
        {
            "None", "Title ID", "Family (base + updates + DLC)", "Category", "Region", "Source format",
            "Required firmware"
        };
        _cboDefaultBackend.ItemsSource = BackendRegistry.All.Select(backend => backend.DisplayName).ToList();
        _cboRenamePreset.ItemsSource = new[] { Ps5RenameFormats.CustomLabel }
            .Concat(Ps5RenameFormats.Presets.Select(preset => preset.Label)).ToList();
        _cboRenamePreset.SelectionChanged += (_, _) => ApplyRenamePreset();
        _cboRenameToken.ItemsSource = Ps5RenameFormatter.TokenNames.ToList();
        _txtRenameFormat.TextChanged += (_, _) => UpdateRenameFeedback();
        _txtWorkspace.TextChanged += (_, _) => _lblWorkspaceFree.Text = FreeSpaceText(_txtWorkspace.Text);
        _txtPasscode.TextChanged += (_, _) => UpdatePasscodeHint();
        _chkShowPasscode.IsCheckedChanged += (_, _) =>
            _txtPasscode.PasswordChar = _chkShowPasscode.IsChecked == true ? '\0' : '●';

        var addFolder = new Button { Content = "Add...", Width = 90 };
        addFolder.Click += async (_, _) =>
        {
            string? path = await PickFolderAsync("Select a library folder");
            if (path is null) return;
            path = AppSettingsNormalizer.NormalizePath(path);
            if (_lstFolders.Items.Cast<string?>().Any(item =>
                    string.Equals(item, path, StringComparison.OrdinalIgnoreCase))) return;
            ((List<string>)_lstFolders.ItemsSource!).Add(path);
            _lstFolders.ItemsSource = null;
            _lstFolders.ItemsSource = _draftFolders;
        };
        var removeFolder = new Button { Content = "Remove", Width = 90 };
        removeFolder.Click += (_, _) =>
        {
            foreach (string item in _lstFolders.SelectedItems?.Cast<string>().ToList() ?? [])
                _draftFolders.Remove(item);
            _lstFolders.ItemsSource = null;
            _lstFolders.ItemsSource = _draftFolders;
        };
        var removeSource = new Button { Content = "Remove", Width = 90 };
        removeSource.Click += (_, _) =>
        {
            foreach (string item in _lstManualSources.SelectedItems?.Cast<string>().ToList() ?? [])
                _draftSources.Remove(item);
            _lstManualSources.ItemsSource = null;
            _lstManualSources.ItemsSource = _draftSources;
        };

        var browseOutput = new Button { Content = "Browse...", Width = 90 };
        browseOutput.Click += async (_, _) =>
        {
            string? path = await PickFolderAsync("Select the default output folder");
            if (path is not null) _txtOutput.Text = path;
        };
        var browseWorkspace = new Button { Content = "Browse...", Width = 90 };
        browseWorkspace.Click += async (_, _) =>
        {
            string? path = await PickFolderAsync("Select the workspace folder");
            if (path is not null) _txtWorkspace.Text = path;
        };
        var insertToken = new Button { Content = "Insert", Width = 80 };
        insertToken.Click += (_, _) =>
        {
            if (_cboRenameToken.SelectedItem is not string token) return;
            int start = _txtRenameFormat.SelectionStart;
            string text = _txtRenameFormat.Text ?? string.Empty;
            _txtRenameFormat.Text = text.Insert(start, token);
            _txtRenameFormat.SelectionStart = start + token.Length;
            _txtRenameFormat.Focus();
        };

        var resetLayout = new Button { Content = "Reset column layout", Width = 160 };
        resetLayout.Click += async (_, _) =>
        {
            ResetLayout = true;
            await AppDialog.ShowInfoAsync(
                "The library columns will return to their default order, widths and visibility when you save.",
                "Reset column layout");
        };
        var clearCaches = new Button { Content = "Clear caches", Width = 130 };
        clearCaches.Click += async (_, _) =>
        {
            ClearCaches = true;
            await AppDialog.ShowInfoAsync(
                "Details and thumbnail caches will be cleared when you save. Cancel discards this.",
                "Clear caches");
        };
        var openLogs = new Button { Content = "Open log folder", Width = 140 };
        openLogs.Click += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Logger.LogDirectory);
                PlatformService.OpenPath(Logger.LogDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await AppDialog.ShowErrorAsync($"Could not open the log folder:\n\n{ex.Message}", "Open log folder");
            }
        };
        var resetSettings = new Button { Content = "Reset preferences", Width = 150 };
        resetSettings.Click += async (_, _) => await ResetPreferencesAsync();
        var export = new Button { Content = "Export preferences...", Width = 170 };
        export.Click += async (_, _) => await ExportSettingsAsync();
        var import = new Button { Content = "Import preferences...", Width = 170 };
        import.Click += async (_, _) => await ImportSettingsAsync();

        _tabLibrary.Content = Page(
            Row(Label("Library folders:"), addFolder, removeFolder),
            _lstFolders,
            Row(_chkRecursive),
            Row(_chkRefreshOnStartup),
            Row(_chkCheckUpdates),
            Row(Label("Manual sources:"), removeSource),
            _lstManualSources);

        _tabAppearance.Content = Page(
            Row(Label("Theme:"), _cboTheme),
            Row(Label("Density:"), _cboDensity),
            Row(Label("Grid row height:"), _nudRowHeight),
            Row(_chkShowThumbnails),
            Row(_chkShowGridLines),
            Row(Label("Default grouping:"), _cboDefaultGroup));

        _tabNaming.Content = Page(
            Row(Label("Rename format:"), _txtRenameFormat),
            Row(Label("Preset:"), _cboRenamePreset),
            Row(Label("Token:"), _cboRenameToken, insertToken),
            _lblRenamePreview,
            _lblRenameUnknown);

        _tabFiles.Content = Page(
            Row(Label("Max preview size (MiB):"), _nudMaxPreview),
            Row(Label("Hex page size (KiB):"), _nudHexPage),
            Row(Label("Thumbnail cache entries:"), _nudThumbnailCache),
            Row(_chkShowFilePreview));

        _tabPaths.Content = Page(
            Row(Label("Default output folder:"), _txtOutput, browseOutput),
            Row(Label("Workspace folder:"), _txtWorkspace, browseWorkspace),
            _lblWorkspaceFree,
            Row(Label("Default builder:"), _cboDefaultBackend),
            Row(Label("Debug passcode:"), _txtPasscode, _chkShowPasscode),
            _lblPasscodeHint,
            Row(_chkOpenOutput));

        _tabSafety.Content = Page(
            Row(_chkConfirmMove),
            Row(_chkConfirmDelete),
            Row(_chkPermanentDelete));

        _tabMaintenance.Content = Page(
            new TextBlock { Text = "Column layout", Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            Row(resetLayout),
            new TextBlock { Text = "Caches", Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            Row(clearCaches),
            new TextBlock { Text = "Logs", Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            Row(openLogs),
            new TextBlock { Text = "Preferences", Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            Row(resetSettings),
            Row(export),
            Row(_chkExportCredentials),
            Row(import));

        _tabs.Items.Add(_tabLibrary);
        _tabs.Items.Add(_tabAppearance);
        _tabs.Items.Add(_tabNaming);
        _tabs.Items.Add(_tabFiles);
        _tabs.Items.Add(_tabPaths);
        _tabs.Items.Add(_tabSafety);
        _tabs.Items.Add(_tabMaintenance);

        var cancel = new Button { Content = "Cancel", Width = 100, IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _btnSave.Click += async (_, _) => await SaveAsync();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(10)
        };
        footer.Children.Add(_btnSave);
        footer.Children.Add(cancel);

        var layout = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(_tabs);
        Content = layout;

    }

    private List<string> _draftFolders = [];
    private List<string> _draftSources = [];

    // ------------------------------------------------------------- load / capture

    private void LoadToControls()
    {
        _loading = true;
        try
        {
            _draftFolders.Clear();
            _draftFolders.AddRange(Settings.LibraryFolders);
            _draftSources.Clear();
            _draftSources.AddRange(Settings.ManualSources);
            _lstFolders.ItemsSource = _draftFolders;
            _lstManualSources.ItemsSource = _draftSources;

            _chkRecursive.IsChecked = Settings.RecursiveScan;
            _chkRefreshOnStartup.IsChecked = Settings.RefreshOnStartup;
            _chkCheckUpdates.IsChecked = Settings.CheckUpdatesOnStartup;

            SelectCombo(_cboTheme, Settings.Theme);
            _nudRowHeight.Value = Math.Clamp(Settings.GridRowHeight, _nudRowHeight.Minimum, _nudRowHeight.Maximum);
            SyncDensityFromRowHeight();
            _chkShowThumbnails.IsChecked = Settings.ShowThumbnails;
            _chkShowGridLines.IsChecked = Settings.ShowGridLines;
            _chkShowFilePreview.IsChecked = Settings.ShowFilePreview;
            SelectCombo(_cboDefaultGroup, GroupLabelFor(Settings.DefaultGroupBy));

            _txtRenameFormat.Text = Settings.RenameFormat;
            SyncRenamePreset();

            _nudMaxPreview.Value = Math.Clamp(Settings.MaxPreviewMb, _nudMaxPreview.Minimum, _nudMaxPreview.Maximum);
            _nudHexPage.Value = Math.Clamp(Settings.HexPageKb, _nudHexPage.Minimum, _nudHexPage.Maximum);
            _nudThumbnailCache.Value = Math.Clamp(Settings.ThumbnailCacheCount, _nudThumbnailCache.Minimum,
                _nudThumbnailCache.Maximum);

            _txtOutput.Text = Settings.OutputDirectory;
            _txtWorkspace.Text = Settings.WorkspaceDirectory;
            SelectBackend(Settings.BuildBackend);
            _txtPasscode.Text = Settings.DebugPasscode;
            _chkOpenOutput.IsChecked = Settings.OpenOutputAfterTask;

            _chkConfirmMove.IsChecked = Settings.ConfirmMove;
            _chkConfirmDelete.IsChecked = Settings.ConfirmDelete;
            _chkPermanentDelete.IsChecked = Settings.PermanentDelete;
        }
        finally
        {
            _loading = false;
        }
        UpdateRenameFeedback();
        UpdatePasscodeHint();
        _lblWorkspaceFree.Text = FreeSpaceText(_txtWorkspace.Text);
    }

    private void ApplyControlsToDraft()
    {
        Settings.LibraryFolders = [.. _draftFolders];
        Settings.ManualSources = [.. _draftSources];
        Settings.RecursiveScan = _chkRecursive.IsChecked == true;
        Settings.RefreshOnStartup = _chkRefreshOnStartup.IsChecked == true;
        Settings.CheckUpdatesOnStartup = _chkCheckUpdates.IsChecked == true;

        Settings.Theme = _cboTheme.SelectedItem as string ?? Settings.Theme;
        Settings.GridRowHeight = (int)(_nudRowHeight.Value ?? 22);
        Settings.ShowThumbnails = _chkShowThumbnails.IsChecked == true;
        Settings.ShowGridLines = _chkShowGridLines.IsChecked == true;
        Settings.ShowFilePreview = _chkShowFilePreview.IsChecked == true;
        Settings.DefaultGroupBy = GroupKeyFor(_cboDefaultGroup.SelectedItem as string);

        Settings.RenameFormat = (_txtRenameFormat.Text ?? string.Empty).Trim();

        Settings.MaxPreviewMb = (int)(_nudMaxPreview.Value ?? 16);
        Settings.HexPageKb = (int)(_nudHexPage.Value ?? 16);
        Settings.ThumbnailCacheCount = (int)(_nudThumbnailCache.Value ?? 512);

        string output = (_txtOutput.Text ?? string.Empty).Trim();
        Settings.OutputDirectory = output.Length == 0 ? string.Empty : AppSettingsNormalizer.NormalizePath(output);
        string workspace = (_txtWorkspace.Text ?? string.Empty).Trim();
        Settings.WorkspaceDirectory = workspace.Length == 0 ? string.Empty : AppSettingsNormalizer.NormalizePath(workspace);
        Settings.BuildBackend = SelectedBackendId();
        Settings.DebugPasscode = _txtPasscode.Text ?? string.Empty;
        Settings.OpenOutputAfterTask = _chkOpenOutput.IsChecked == true;

        Settings.ConfirmMove = _chkConfirmMove.IsChecked == true;
        Settings.ConfirmDelete = _chkConfirmDelete.IsChecked == true;
        Settings.PermanentDelete = _chkPermanentDelete.IsChecked == true;
    }

    private bool TryApplyControls(out string? error)
    {
        string passcode = _txtPasscode.Text ?? string.Empty;
        if (passcode.Length > 0 && !AppSettingsNormalizer.IsValidPasscode(passcode))
        {
            error = "The debug passcode must be blank or exactly 32 printable ASCII characters.";
            _tabs.SelectedItem = _tabPaths;
            _txtPasscode.Focus();
            return false;
        }

        if (!TryValidateDirectory(_txtOutput.Text, "output", out string? outputError))
        {
            error = outputError;
            _tabs.SelectedItem = _tabPaths;
            _txtOutput.Focus();
            return false;
        }

        if (!TryValidateDirectory(_txtWorkspace.Text, "workspace", out string? workspaceError))
        {
            error = workspaceError;
            _tabs.SelectedItem = _tabPaths;
            _txtWorkspace.Focus();
            return false;
        }

        ApplyControlsToDraft();
        error = null;
        return true;
    }

    private static bool TryValidateDirectory(string? value, string label, out string? error)
    {
        string path = (value ?? string.Empty).Trim();
        if (path.Length == 0)
        {
            error = null;
            return true;
        }
        if (File.Exists(path))
        {
            error = $"The {label} path is a file, not a folder:\n\n{path}";
            return false;
        }
        if (!Directory.Exists(path))
        {
            error = $"The {label} folder does not exist:\n\n{path}\n\nChoose an existing folder with Browse...";
            return false;
        }
        error = null;
        return true;
    }

    private static string FreeSpaceText(string? path)
    {
        string value = (path ?? string.Empty).Trim();
        if (value.Length == 0 || !Directory.Exists(value)) return string.Empty;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(value));
            if (string.IsNullOrEmpty(root)) return string.Empty;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return string.Empty;
            return $"{drive.Name} {FormatBytes(drive.AvailableFreeSpace)} free";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double amount = Math.Max(0, value);
        int unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return $"{amount:0.##} {units[unit]}";
    }

    private void ApplyDensityPreset()
    {
        if (_syncingDensity || _loading) return;
        int index = Math.Clamp(_cboDensity.SelectedIndex, 0, DensityRowHeights.Length - 1);
        _syncingDensity = true;
        try
        {
            _nudRowHeight.Value = Math.Clamp(DensityRowHeights[index],
                (int)_nudRowHeight.Minimum, (int)_nudRowHeight.Maximum);
        }
        finally { _syncingDensity = false; }
    }

    private void SyncDensityFromRowHeight()
    {
        if (_syncingDensity) return;
        int height = (int)(_nudRowHeight.Value ?? 22);
        int index = height <= 19 ? 0 : height <= 24 ? 1 : 2;
        _syncingDensity = true;
        try { _cboDensity.SelectedIndex = index; }
        finally { _syncingDensity = false; }
    }

    private void ApplyRenamePreset()
    {
        if (_syncingRename || _cboRenamePreset.SelectedIndex <= 0) return;
        int presetIndex = _cboRenamePreset.SelectedIndex - 1;
        if (presetIndex >= Ps5RenameFormats.Presets.Count) return;
        _txtRenameFormat.Text = Ps5RenameFormats.Presets[presetIndex].Format;
    }

    private void SyncRenamePreset()
    {
        _syncingRename = true;
        try
        {
            int index = -1;
            for (int i = 0; i < Ps5RenameFormats.Presets.Count; i++)
                if (string.Equals(Ps5RenameFormats.Presets[i].Format, Settings.RenameFormat,
                        StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            _cboRenamePreset.SelectedIndex = index >= 0 ? index + 1 : 0;
        }
        finally { _syncingRename = false; }
    }

    private void UpdateRenameFeedback()
    {
        string format = _txtRenameFormat.Text ?? string.Empty;
        string preview = Ps5RenameFormatter.Expand(format, SampleTokens, "PS5_GAME");
        _lblRenamePreview.Text = format.Trim().Length == 0
            ? "Example: (blank uses the default format)"
            : $"Example: {preview}";

        string[] unknown = UnknownTokens(format);
        _lblRenameUnknown.Text = unknown.Length == 0
            ? string.Empty
            : "Unknown token(s): " + string.Join(", ", unknown) + " — they will be left as typed. Valid tokens: "
              + string.Join(" ", Ps5RenameFormatter.TokenNames);
    }

    private static string[] UnknownTokens(string format)
    {
        var known = new HashSet<string>(Ps5RenameFormatter.TokenNames, StringComparer.OrdinalIgnoreCase);
        return Regex.Matches(format, @"\{[A-Za-z0-9_]+\}")
            .Select(match => match.Value)
            .Where(token => !known.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static readonly Ps5RenameTokens SampleTokens = new(
        Title: "Example Game", TitleId: "PPSA12345",
        ContentId: "UP0000-PPSA12345_00-EXAMPLE0000000000", ConceptId: "100000",
        Version: "01.001.000", ContentVersion: "01.001", MasterVersion: "01.001.000",
        Category: "Game", Region: "Europe", Platform: "PS5", SystemVersion: "10.000.000",
        SdkVersion: "0.0.0", Source: "Dump Files", Size: "12.3 GiB", Language: "en-US",
        Drm: "None", Date: "2026-09-17", Tool: "PS5 PKG Tool");

    private void UpdatePasscodeHint()
    {
        if (_loading) return;
        string text = _txtPasscode.Text ?? string.Empty;
        if (text.Length == 0)
        {
            _lblPasscodeHint.Text =
                "Blank uses the default all-zero passcode. Otherwise exactly 32 printable ASCII characters.";
            return;
        }
        _lblPasscodeHint.Text = AppSettingsNormalizer.IsValidPasscode(text)
            ? "Valid passcode (32 printable ASCII characters)."
            : $"Invalid: {text.Length} character(s); needs exactly 32 printable ASCII characters.";
    }

    private void SelectBackend(string? id)
    {
        for (int index = 0; index < BackendRegistry.All.Count; index++)
        {
            if (string.Equals(BackendRegistry.All[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                _cboDefaultBackend.SelectedIndex = index;
                return;
            }
        }
        _cboDefaultBackend.SelectedIndex = 0;
    }

    private string SelectedBackendId() =>
        _cboDefaultBackend.SelectedIndex >= 0 && _cboDefaultBackend.SelectedIndex < BackendRegistry.All.Count
            ? BackendRegistry.All[_cboDefaultBackend.SelectedIndex].Id
            : BackendRegistry.DefaultId;

    private static void SelectCombo(ComboBox combo, string value)
    {
        int index = -1;
        for (int i = 0; i < combo.ItemCount; i++)
            if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        combo.SelectedIndex = index >= 0 ? index : (combo.ItemCount > 0 ? 0 : -1);
    }

    // ---------------------------------------------------------- import / export / reset

    private async Task ResetPreferencesAsync()
    {
        if (await AppDialog.ConfirmAsync(
                "Reset all preferences to their defaults?\n\nLibrary folders, manual sources, recent folders, " +
                "saved views and window/column layout are kept. This does not clear caches.",
                "Reset preferences") != AppDialogResult.Yes)
            return;

        Settings = AppSettingsNormalizer.Normalize(new AppSettings
        {
            LibraryFolders = [.. Settings.LibraryFolders],
            ManualSources = [.. Settings.ManualSources],
            RecentFolders = [.. Settings.RecentFolders],
            SavedViews = [.. Settings.SavedViews],
            LibraryColumnOrder = [.. Settings.LibraryColumnOrder],
            LibraryHiddenColumns = [.. Settings.LibraryHiddenColumns],
            LibraryColumnWeights = new Dictionary<string, float>(Settings.LibraryColumnWeights, StringComparer.Ordinal),
            LibraryColumnWidths = new Dictionary<string, float>(Settings.LibraryColumnWidths, StringComparer.Ordinal),
            LibrarySortKeys = [.. Settings.LibrarySortKeys],
            ShowFilterBar = Settings.ShowFilterBar,
            ShowFilterChips = Settings.ShowFilterChips,
            ShowWorkspacePane = Settings.ShowWorkspacePane,
            ShowStatusBar = Settings.ShowStatusBar,
            LibrarySortColumn = Settings.LibrarySortColumn,
            LibrarySortAscending = Settings.LibrarySortAscending,
            WindowWidth = Settings.WindowWidth,
            WindowHeight = Settings.WindowHeight,
            WindowMaximized = Settings.WindowMaximized
        });
        LoadToControls();
    }

    private async Task ExportSettingsAsync()
    {
        if (!TryApplyControls(out string? error))
        {
            await AppDialog.ShowWarningAsync(error!, "Export preferences");
            return;
        }

        string? path = await PickSaveFileAsync("Export preferences", "PS5PKGTool-settings.json",
            new Avalonia.Platform.Storage.FilePickerFileType("JSON file") { Patterns = ["*.json"] });
        if (path is null) return;
        AppSettings export = Clone(Settings);
        bool includeCredentials = _chkExportCredentials.IsChecked == true;
        if (!includeCredentials) export.DebugPasscode = string.Empty;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(export, JsonOptions));
            await AppDialog.ShowInfoAsync(
                includeCredentials
                    ? "Preferences exported, including the debug passcode."
                    : "Preferences exported. The debug passcode was excluded.",
                "Export preferences");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await AppDialog.ShowErrorAsync(ex.Message, "Export preferences");
        }
    }

    private async Task ImportSettingsAsync()
    {
        string? path = await PickOpenFileAsync("Import preferences",
            new Avalonia.Platform.Storage.FilePickerFileType("JSON file") { Patterns = ["*.json"] });
        if (path is null) return;

        AppSettings candidate;
        try
        {
            string json = File.ReadAllText(path);
            candidate = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? throw new InvalidDataException("The file did not contain settings.");
            candidate = AppSettingsNormalizer.Normalize(candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidDataException)
        {
            await AppDialog.ShowErrorAsync($"The settings file could not be read:\n\n{ex.Message}",
                "Import preferences");
            return;
        }

        List<string> changes = DescribeChanges(Settings, candidate);
        string message = changes.Count == 0
            ? "The file matches the current preferences."
            : "Import these changes?\n\n" + string.Join(Environment.NewLine, changes.Take(20))
              + (changes.Count > 20 ? Environment.NewLine + $"… and {changes.Count - 20:N0} more" : string.Empty);
        if (await AppDialog.ConfirmAsync(message, "Import preferences") != AppDialogResult.Yes) return;

        Settings = candidate;
        LoadToControls();
        await AppDialog.ShowInfoAsync("Preferences imported. Review them and choose Save to keep them.",
            "Import preferences");
    }

    private static List<string> DescribeChanges(AppSettings before, AppSettings after)
    {
        var changes = new List<string>();
        AppendPathChange(changes, "Library folders", before.LibraryFolders, after.LibraryFolders);
        AppendPathChange(changes, "Manual sources", before.ManualSources, after.ManualSources);
        AppendChange(changes, "Scan subfolders", before.RecursiveScan, after.RecursiveScan);
        AppendChange(changes, "Refresh on startup", before.RefreshOnStartup, after.RefreshOnStartup);
        AppendChange(changes, "Theme", before.Theme, after.Theme);
        AppendChange(changes, "Grid row height", before.GridRowHeight, after.GridRowHeight);
        AppendChange(changes, "Show file preview pane", before.ShowFilePreview, after.ShowFilePreview);
        AppendChange(changes, "Default grouping", before.DefaultGroupBy, after.DefaultGroupBy);
        AppendChange(changes, "Rename format", before.RenameFormat, after.RenameFormat);
        AppendChange(changes, "Max preview (MiB)", before.MaxPreviewMb, after.MaxPreviewMb);
        AppendChange(changes, "Hex page (KiB)", before.HexPageKb, after.HexPageKb);
        AppendChange(changes, "Thumbnail cache entries", before.ThumbnailCacheCount, after.ThumbnailCacheCount);
        AppendChange(changes, "Default output folder", before.OutputDirectory, after.OutputDirectory);
        AppendChange(changes, "Default workspace folder", before.WorkspaceDirectory, after.WorkspaceDirectory);
        AppendChange(changes, "Default builder", before.BuildBackend, after.BuildBackend);
        AppendChange(changes, "Debug passcode", Mask(before.DebugPasscode), Mask(after.DebugPasscode));
        AppendChange(changes, "Open output after success", before.OpenOutputAfterTask, after.OpenOutputAfterTask);
        AppendChange(changes, "Confirm moves", before.ConfirmMove, after.ConfirmMove);
        AppendChange(changes, "Confirm recycling", before.ConfirmDelete, after.ConfirmDelete);
        AppendChange(changes, "Permanent deletion", before.PermanentDelete, after.PermanentDelete);
        return changes;
    }

    private static void AppendChange(List<string> changes, string label, object? before, object? after)
    {
        string left = before?.ToString() ?? string.Empty;
        string right = after?.ToString() ?? string.Empty;
        if (!string.Equals(left, right, StringComparison.Ordinal))
            changes.Add($"• {label}: {(left.Length == 0 ? "(none)" : left)} -> {(right.Length == 0 ? "(none)" : right)}");
    }

    private static void AppendPathChange(List<string> changes, string label, List<string> before,
        List<string> after)
    {
        var removed = before.Except(after, StringComparer.OrdinalIgnoreCase).ToList();
        var added = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
        if (removed.Count > 0) changes.Add($"• {label} removed: {string.Join(", ", removed.Take(5))}");
        if (added.Count > 0) changes.Add($"• {label} added: {string.Join(", ", added.Take(5))}");
    }

    private static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : new string('*', value.Length);

    // --------------------------------------------------------------------- save

    private async Task SaveAsync()
    {
        if (!TryApplyControls(out string? error))
        {
            await AppDialog.ShowWarningAsync(error!, "Settings");
            return;
        }

        Settings = AppSettingsNormalizer.Normalize(Settings);
        if (_save is not null)
        {
            while (true)
            {
                (bool success, string? saveError) = _save(Settings);
                if (success) break;
                if (await AppDialog.ConfirmAsync(
                        $"The settings could not be saved:\n\n{saveError}\n\nYour changes are kept. Retry?",
                        "Settings") != AppDialogResult.Yes)
                    return;
            }
        }

        Logger.Info("Settings saved.");
        _committed = true;
        Close(new SettingsDialogResult(Settings, ResetLayout, ClearCaches));
    }

    // ------------------------------------------------------------------ helpers

    private static string GroupKeyFor(string? label) => label switch
    {
        "Title ID" => "titleid",
        "Family (base + updates + DLC)" => "family",
        "Category" => "category",
        "Region" => "region",
        "Source format" => "source",
        "Required firmware" => "firmware",
        _ => string.Empty
    };

    private static string GroupLabelFor(string? key) => key switch
    {
        "titleid" => "Title ID",
        "family" => "Family (base + updates + DLC)",
        "category" => "Category",
        "region" => "Region",
        "source" => "Source format",
        "firmware" => "Required firmware",
        _ => "None"
    };

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders =
            await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveFileAsync(string title, string suggestedName,
        params Avalonia.Platform.Storage.FilePickerFileType[] filters)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = filters
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenFileAsync(string title,
        params Avalonia.Platform.Storage.FilePickerFileType[] filters)
    {
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files =
            await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = filters
            });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
