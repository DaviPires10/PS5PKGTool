using System.Text;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;
using TableRow = PS5PKGTool.Controls.TableRow;
using TableView = PS5PKGTool.Controls.TableView;
using TableColumn = PS5PKGTool.Controls.TableColumn;
using PS5PKGTool.Core.Models;
using PS5PKGTool.Core.Parsers;
using PS5PKGTool.Core.Services;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

public partial class MainWindow
{
    private sealed record FileBrowserEntry(string RelativePath, bool IsDirectory, long Size = 0);

    private sealed record FileListRow(string Name, string Type, string Path, string SizeText, long Size,
        FileBrowserEntry Entry, bool IsDirectory, string Origin);

    private Ps5GameDetails? _currentDetails;
    private string _currentDetailsRoot = string.Empty;
    private Ps5Artwork? _currentArtwork;
    private readonly HashSet<string> _populatedDetailTabs = new(StringComparer.Ordinal);
    private int _detailVersion;

    private string _rawParamOriginal = string.Empty;
    private string _rawParamFormatted = string.Empty;

    private readonly Dictionary<string, TreeViewItem> _directoryNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Ps5FileInfo>> _filesByDirectory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _directorySizes = new(StringComparer.OrdinalIgnoreCase);
    private string _filesSummaryBase = string.Empty;
    private int _fileSortColumn;
    private bool _fileSortAscending = true;
    private int _filePreviewVersion;
    private FileBrowserEntry? _filePreviewEntry;
    private Ps5FileInfo? _filePreviewInfo;
    private const int MaxFileListRows = 20000;

    private string? _trophySummaryBase;
    private List<Ps5Trophy> _visibleTrophies = [];
    private Ps5UdsSummary? _udsSummary;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;

    private void InitializeDetails()
    {
        gridOverview.SetColumns([
            new TableColumn { Name = "Property", Header = "Property", FixedWidth = 220 },
            new TableColumn { Name = "Value", Header = "Value", Weight = 10 }
        ]);
        gridOverview.ShowGridLines = true;

        btnOverviewCopyAll.Click += (_, _) => CopyOverview(copyAll: true);
        btnOverviewCopySelected.Click += (_, _) => CopyOverview(copyAll: false);
        gridOverview.SelectionChanged += (_, _) =>
            btnOverviewCopySelected.IsEnabled = gridOverview.SelectedRows.Count > 0;

        tabsDetails.SelectionChanged += (_, _) =>
        {
            try { PopulateActiveDetailTab(); }
            catch (Exception ex) { Logger.Warn($"Unable to populate detail tab: {ex.Message}"); }
        };

        btnCopyRawJson.Click += (_, _) =>
        {
            if (txtRawMetadata.Text?.Length is null or 0)
            {
                AppDialog.ShowInfo("There is no param.json to copy.", "Raw param.json");
                return;
            }
            CopyText(txtRawMetadata.Text);
        };
        btnRawFormatted.Click += (_, _) => ShowRawJson(formatted: true);
        btnRawOriginal.Click += (_, _) => ShowRawJson(formatted: false);

        foreach ((Image image, string name, int index) in ArtworkSlots())
            image.ContextMenu = BuildArtworkMenu(image);
        btnArtworkSaveAll.Click += (_, _) => _ = SaveAllArtworkAsync();

        InitializeTrophies();
        InitializeActivities();
        InitializeFiles();
        InitializeExecutable();
    }

    private ContextMenu BuildArtworkMenu(Image image)
    {
        var save = new MenuItem { Header = "Save This Image..." };
        save.Click += (_, _) => _ = SaveThisArtworkAsync(image);
        var all = new MenuItem { Header = "Save All Artwork..." };
        all.Click += (_, _) => _ = SaveAllArtworkAsync();
        return new ContextMenu { ItemsSource = new[] { save, all } };
    }

    private async Task ShowGameAsync(Ps5GameInfo game)
    {
        PopulateOverview(game);
        _rawParamOriginal = game.RawParamJson;
        _rawParamFormatted = PrettyJson(_rawParamOriginal);
        ShowRawJson(formatted: true);
        ClearDeepDetails();
        SetStatus($"Loading details for {game.Title}...");
        long sizeBefore = game.SourceSize;

        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = new CancellationTokenSource();
        int version = Interlocked.Increment(ref _detailVersion);
        try
        {
            if (!_detailsCache.TryGetValue(game.RootPath, out Ps5GameDetails? details))
            {
                var artworkProgress = new Progress<Ps5Artwork>(snapshot =>
                {
                    if (version != _detailVersion) return;
                    _currentArtwork = snapshot;
                    if (ReferenceEquals(tabsDetails.SelectedItem, tabArtwork)) ApplyArtwork(snapshot);
                });
                details = await _detailsLoader.LoadAsync(game, _detailCancellation.Token, artworkProgress);
                _detailsCache[game.RootPath] = details;
                if (_currentArtwork is null)
                    _currentArtwork = new Ps5Artwork(details.Icon, details.Background, details.Background1, details.Background2);
            }
            if (version != _detailVersion || _detailCancellation.IsCancellationRequested) return;
            _currentDetails = details;
            _currentDetailsRoot = game.RootPath;
            if (_currentArtwork is null)
                _currentArtwork = new Ps5Artwork(details.Icon, details.Background, details.Background1, details.Background2);
            _populatedDetailTabs.Clear();
            game.GameRootBytes = details.Files.TotalSize;
            PopulateOverview(game);
            PopulateActiveDetailTab();
            if (game.SourceSize != sizeBefore)
            {
                RefreshLibrarySizeCell(game);
                _stateStore.SaveManifest(_games);
            }
            SetStatus(details.Errors.Count == 0
                ? $"Loaded {game.Title}."
                : $"Loaded {game.Title} with {details.Errors.Count} detail warning(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _detailVersion) return;
            SetStatus("Unable to load game details.");
            Logger.Warn($"Unable to read '{game.RootPath}': {ex.Message}");
            PopulateOverview(game);
            PopulateActiveDetailTab();
        }
    }

    private static string PrettyJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        try { return System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(text),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); }
        catch (System.Text.Json.JsonException) { return text; }
    }

    private void ShowRawJson(bool formatted)
    {
        if (_rawParamOriginal.Length == 0)
        {
            txtRawMetadata.Text = "No param.json was available for this source.";
            return;
        }
        txtRawMetadata.Text = formatted ? _rawParamFormatted : _rawParamOriginal;
    }

    private void ClearDetails()
    {
        _detailCancellation?.Cancel();
        int version = Interlocked.Increment(ref _detailVersion);
        _ = version;
        _currentDetails = null;
        _currentDetailsRoot = string.Empty;
        _currentArtwork = null;
        _populatedDetailTabs.Clear();
        ClearDeepDetails();
        gridOverview.Clear();
        gridPkgHeader.Clear();
        gridPkgSegments.Clear();
        gridPkgEntries.Clear();
        gridParamSfo.Clear();
        gridKeystone.Clear();
        gridSi.Clear();
        gridPlayGoChunks.Clear();
        gridPlayGoScenarios.Clear();
        gridPlayGoFiles.Clear();
        txtRawMetadata.Text = string.Empty;
        _rawParamOriginal = string.Empty;
        _rawParamFormatted = string.Empty;
        lblPlayGoSummary.Text = string.Empty;
        lblFilesSummary.Text = "Select a game to inventory its files.";
    }

    private void ClearDeepDetails()
    {
        _currentDetails = null;
        _currentDetailsRoot = string.Empty;
        _currentArtwork = null;
        _populatedDetailTabs.Clear();
        gridTrophies.Clear();
        lblTrophySummary.Text = string.Empty;
        gridUdsEvents.Clear();
        gridUdsEventProperties.Clear();
        gridUdsStats.Clear();
        gridUdsEnums.Clear();
        gridUdsRules.Clear();
        lblActivitiesSummary.Text = string.Empty;
        _directoryNodes.Clear();
        _filesByDirectory.Clear();
        _directorySizes.Clear();
        treeFiles.Items.Clear();
        listFiles.Clear();
        ResetFileViewer();
        gridModules.Clear();
        gridElfPrograms.Clear();
        gridElfSections.Clear();
        gridSelfHeader.Clear();
        gridSelfSegments.Clear();
        lblExecutableSummary.Text = string.Empty;
    }

    private void CleanupDetails()
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = null;
        _filePreviewCancellation?.Cancel();
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _libVlc?.Dispose();
        _libVlc = null;
    }

    private void PopulateOverview(Ps5GameInfo game)
    {
        var rows = new List<TableRow>();
        void Add(string property, object? value)
        {
            string text = value?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text)) rows.Add(new TableRow { Cells = [property, text] });
        }

        Add("Title", game.Title);
        Add("Source", game.SourceDescription);
        if (game.SourceSize > 0) Add("Source Size", FormatBytes(game.SourceSize));
        Add("Title ID", game.TitleId);
        Add("Content ID", game.ContentId);
        Add("Concept ID", game.ConceptId);
        Add("Content Version", game.ContentVersion);
        Add("Master Version", game.MasterVersion);
        Add("Target Content Version", game.TargetContentVersion);
        Add("Origin Content Version", game.OriginContentVersion);
        Add("Required System Software", game.RequiredSystemSoftware);
        Add("SDK Version", game.SdkVersion);
        Add("Category", CategoryOf(game));
        Add("DRM Type", game.DrmType);
        Add("Content Badge", game.ContentBadgeType);
        Add("Default Language", game.DefaultLanguage);
        Add("Localized Titles", string.Join(" | ", game.LocalizedTitles.Select(pair => $"{pair.Key}: {pair.Value}")));
        Add("Declared Features", string.Join(", ", game.DeclaredFeatures));
        Add("Game Intents", string.Join(", ", game.GameIntents));
        Add("Shared Add-on IDs", string.Join(", ", game.SharedAddOnServiceIds));
        Add("Feature Attributes",
            $"0x{game.Attribute:X8} / 0x{game.Attribute2:X8} / 0x{game.Attribute3:X8} / 0x{game.Attribute4:X8}");
        Add("Download Data Size", FormatBytes(game.DownloadDataSize));
        if (game.FlexibleMemorySize.HasValue) Add("Flexible Memory", FormatBytes(game.FlexibleMemorySize.Value));
        Add("Creation Date", game.CreationDate);
        Add("Publishing Tool", game.ToolVersion);
        Add("Version URI", game.VersionFileUri);
        Add("Age Levels", string.Join(", ", game.AgeLevels.Select(pair => $"{pair.Key}: {pair.Value}")));
        if (_currentDetails is { Sections.Count: > 0 } loaded && _currentDetailsRoot == game.RootPath)
            foreach (KeyValuePair<string, SectionStatus> section in loaded.Sections)
                Add(section.Key + " state", section.Value.Display);
        Add("Location", game.RootPath);
        if (game.SourceKind is Ps5SourceKind.Ffpfsc or Ps5SourceKind.FilesystemImage or Ps5SourceKind.Ffpkg)
        {
            Add(game.SourceKind == Ps5SourceKind.Ffpfsc ? "Inner Filesystem Image" : "Filesystem Image",
                game.ContainerInnerFileName);
            bool isUfs2 = game.SourceKind == Ps5SourceKind.Ffpkg ||
                          Path.GetExtension(game.ContainerInnerFileName).Equals(".ffpkg", StringComparison.OrdinalIgnoreCase);
            if (game.ContainerFileLength > 0) Add("Container File Length", FormatBytes(game.ContainerFileLength));
            if (game.ContainerLogicalSize > 0)
                Add(isUfs2 ? "Inner UFS2 Logical Length" : "Inner exFAT Logical Length",
                    FormatBytes(game.ContainerLogicalSize));
            if (game.GameRootBytes > 0) Add("Selected Game Files", FormatBytes(game.GameRootBytes));
            if (game.SourceKind == Ps5SourceKind.Ffpfsc)
            {
                if (game.ContainerStoredSize > 0) Add("Stored PFSC Size", FormatBytes(game.ContainerStoredSize));
                Add("PFSC Blocks", game.ContainerBlockCount.ToString("N0"));
            }
            if (!string.IsNullOrWhiteSpace(game.VirtualRoot)) Add("Game Root in Image", game.VirtualRoot);
        }
        if (game.Package is SonyPkgSummary package)
        {
            Add("Package Type", package.KindDisplayName);
            Add("Package Size", FormatBytes(package.FileSize));
            Add("CNT Offset", $"0x{package.EmbeddedCntOffset:X}");
            Add("CNT Entries", $"{package.Entries.Count:N0} ({package.EncryptedEntryCount:N0} encrypted)");
            Add("Embedded CNT", package.Entries.Count > 0
                ? "Present"
                : package.Kind == SonyPkgKind.FinalizedRetail
                    ? "Not present in this retail FIH"
                    : "Empty");
            Add("CNT Header Flags", $"0x{package.HeaderFlags:X8}");
            Add("CNT Content Type", DescribeCntContentType(package.ContentType));
            Add("CNT Content Flags", $"0x{package.ContentFlags:X8}");
            Add("CNT DRM Type", $"0x{package.DrmType:X8}");
            if (package.SignedByte is { } signedByte) Add("FIH Signed Byte", DescribeFihSignedByte(signedByte));
            if (package.FormatVersion.HasValue) Add("FIH Format Version", package.FormatVersion.Value);
            if (package.PfsImageSize > 0)
            {
                Add("PFS Image Offset", $"0x{package.PfsImageOffset:X}");
                Add("PFS Image Size", package.PfsImageSize <= long.MaxValue
                    ? FormatBytes((long)package.PfsImageSize)
                    : $"0x{package.PfsImageSize:X} bytes");
                if (package.NestedPfs is { } nested)
                {
                    Add("PFS Access", nested.AccessState == SonyPfsAccessState.EncryptedKeyRequired
                        ? "Retail image key required"
                        : nested.AccessState.ToString());
                    if (nested.BlockSize > 0) Add("PFS Block Size", FormatBytes(nested.BlockSize));
                    if (nested.InodeCount > 0) Add("PFS Inodes", nested.InodeCount.ToString("N0"));
                    if (nested.Files.Count > 0) Add("PFS Files", nested.Files.Count.ToString("N0"));
                    Add("PFS Status", nested.StatusMessage);
                }
            }
        }
        foreach (string warning in game.DataWarnings) Add("Metadata Warning", warning);
        gridOverview.SetRows(rows);
        btnOverviewCopySelected.IsEnabled = false;
    }

    private void CopyOverview(bool copyAll)
    {
        string text;
        if (copyAll)
        {
            if (gridOverview.Rows.Count == 0)
            {
                AppDialog.ShowInfo("There is no overview information to copy.", "Overview");
                return;
            }
            text = string.Join(Environment.NewLine,
                gridOverview.Rows.Select(row => $"{row.Cells[0]}: {row.Cells[1]}"));
        }
        else
        {
            if (gridOverview.CurrentRow is not { } row) return;
            text = $"{row.Cells[0]}: {row.Cells[1]}";
        }
        CopyText(text);
    }

    private static string DescribeFihSignedByte(byte value) => value switch
    {
        0x00 => "0x00 (finalized debug / FPKG)",
        0x80 => "0x80 (finalized retail)",
        _ => $"0x{value:X2}"
    };

    private static string DescribeCntContentType(uint value) => value switch
    {
        0x20 => "Application (0x00000020)",
        0x21 => "Additional content (0x00000021)",
        _ => $"0x{value:X8}"
    };

    private void PopulateActiveDetailTab()
    {
        if (_selectedGame is not { } game) return;
        string key = game.RootPath;

        if (ReferenceEquals(tabsDetails.SelectedItem, tabPackage))
        {
            if (!_populatedDetailTabs.Add("package|" + key)) return;
            Ps5GameDetails? packageDetails = _currentDetailsRoot == key ? _currentDetails : null;
            _ = RunSilentlyAsync(async () =>
            {
                try { await PopulatePackageAsync(game, packageDetails); }
                catch { _populatedDetailTabs.Remove("package|" + key); throw; }
            });
            return;
        }

        if (_currentDetails is not { } details || _currentDetailsRoot != key) return;

        if (ReferenceEquals(tabsDetails.SelectedItem, tabArtwork))
        {
            ApplyArtwork(_currentArtwork ??
                new Ps5Artwork(details.Icon, details.Background, details.Background1, details.Background2));
        }
        else if (ReferenceEquals(tabsDetails.SelectedItem, tabTrophies))
        {
            if (!_populatedDetailTabs.Add("trophies|" + key)) return;
            PopulateTrophies(details.TrophySet, SectionErrors(details, "Trophies:"));
        }
        else if (ReferenceEquals(tabsDetails.SelectedItem, tabActivities))
        {
            if (!_populatedDetailTabs.Add("activities|" + key)) return;
            PopulateActivities(details.Uds, SectionErrors(details, "Activities:"));
        }
        else if (ReferenceEquals(tabsDetails.SelectedItem, tabFiles))
        {
            if (_populatedDetailTabs.Add("files|" + key))
                PopulateFiles(game, details.Files);
            else
                RefreshFileList();
        }
        else if (ReferenceEquals(tabsDetails.SelectedItem, tabExecutable))
        {
            if (!_populatedDetailTabs.Add("executable|" + key)) return;
            PopulateExecutable(details.Executable, SectionErrors(details, "Executable:"));
        }
    }

    private static async Task RunSilentlyAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn($"Suppressed read error: {ex.Message}"); }
    }

    private static string SectionErrors(Ps5GameDetails? details, string prefix) =>
        details is null
            ? string.Empty
            : string.Join(" | ", details.Errors.Where(error =>
                error.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    // ---------------------------------------------------------------- Artwork

    private void ApplyArtwork(Ps5Artwork artwork)
    {
        sectionIcon.Header = ArtworkSlot("Icon", artwork.Icon);
        sectionBackground.Header =
            $"PIC0: {ArtworkSlotText(artwork.Background)}    " +
            $"PIC1: {ArtworkSlotText(artwork.Background1)}    " +
            $"PIC2: {ArtworkSlotText(artwork.Background2)}";
        pictureIcon.Source = ToBitmap(artwork.Icon);
        pictureBackground0.Source = ToBitmap(artwork.Background);
        pictureBackground1.Source = ToBitmap(artwork.Background1);
        pictureBackground2.Source = ToBitmap(artwork.Background2);
    }

    private static Bitmap? ToBitmap(Ps5ImageData? data)
    {
        if (data is null || data.IsEmpty) return null;
        try
        {
            if (data.IsRgba) return CreateBitmap(data);
            using var stream = new MemoryStream(data.Bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ArtworkSlot(string name, Ps5ImageData? image) =>
        $"{name} - {ArtworkSlotText(image)}";

    private static string ArtworkSlotText(Ps5ImageData? image)
    {
        if (image is null || image.IsEmpty) return "not present";
        string encoding = image.IsRgba ? "DDS (decoded)" : "PNG";
        return image.Width > 0 && image.Height > 0 ? $"{encoding} {image.Width}×{image.Height}" : encoding;
    }

    private IEnumerable<(Image Control, string Name, int Index)> ArtworkSlots()
    {
        yield return (pictureIcon, "icon0.png", 0);
        yield return (pictureBackground0, "pic0.png", 1);
        yield return (pictureBackground1, "pic1.png", 2);
        yield return (pictureBackground2, "pic2.png", 3);
    }

    private Ps5ImageData? ArtworkForControl(Image control) =>
        ReferenceEquals(control, pictureIcon) ? _currentArtwork?.Icon
        : ReferenceEquals(control, pictureBackground0) ? _currentArtwork?.Background
        : ReferenceEquals(control, pictureBackground1) ? _currentArtwork?.Background1
        : _currentArtwork?.Background2;

    private async Task SaveAllArtworkAsync()
    {
        if (_currentArtwork is null)
        {
            AppDialog.ShowInfo("Select a game first to save its artwork.", "Artwork");
            return;
        }
        string? folder = await PickFolderAsync("Select a folder for the artwork images");
        if (folder is null) return;
        string? titleId = SelectedGame()?.TitleId;
        int saved = 0;
        foreach ((Image control, string name, _) in ArtworkSlots())
        {
            Ps5ImageData? data = ArtworkForControl(control);
            if (data is null || data.IsEmpty) continue;
            try
            {
                SaveArtworkImage(data, UniqueArtworkPath(folder, name, titleId));
                saved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.ShowError(ex.Message, "Save artwork");
                return;
            }
        }
        if (saved == 0) AppDialog.ShowInfo("No artwork was available to save.", "Artwork");
        else SetStatus($"Saved {saved} artwork image(s) to {folder}.");
    }

    private async Task SaveThisArtworkAsync(Image control)
    {
        Ps5ImageData? data = ArtworkForControl(control);
        if (data is null || data.IsEmpty)
        {
            AppDialog.ShowInfo("This image is not available for this game.", "Artwork");
            return;
        }
        string name = ArtworkName(control);
        string? path = await PickSaveFileAsync("Save artwork", name,
            new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] });
        if (path is null) return;
        try
        {
            SaveArtworkImage(data, path);
            SetStatus($"Saved {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDialog.ShowError(ex.Message, "Save artwork");
        }
    }

    private static string ArtworkName(Image control) => "artwork.png";

    private static string UniqueArtworkPath(string folder, string name, string? titleId)
    {
        string path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        if (!string.IsNullOrWhiteSpace(titleId))
        {
            path = Path.Combine(folder, $"{titleId}_{name}");
            if (!File.Exists(path)) return path;
            stem = $"{titleId}_{stem}";
        }
        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // --------------------------------------------------------------- Trophies

    private void InitializeTrophies()
    {
        gridTrophies.SetColumns([
            new TableColumn { Name = "ID", Header = "ID", FixedWidth = 48, RightAligned = true },
            new TableColumn { Name = "Icon", Header = "Icon", FixedWidth = 52, IsIcon = true },
            new TableColumn { Name = "Grade", Header = "Grade", FixedWidth = 90 },
            new TableColumn { Name = "Hidden", Header = "Hidden", FixedWidth = 64 },
            new TableColumn { Name = "Name", Header = "Name", Weight = 12 },
            new TableColumn { Name = "Description", Header = "Description", Weight = 18 },
            new TableColumn { Name = "Unlock", Header = "Unlock Condition", Weight = 12 }
        ]);
        gridTrophies.RowHeight = 48;
        chkTrophyPlatinum.IsCheckedChanged += (_, _) => ApplyTrophyFilter();
        chkTrophyGold.IsCheckedChanged += (_, _) => ApplyTrophyFilter();
        chkTrophySilver.IsCheckedChanged += (_, _) => ApplyTrophyFilter();
        chkTrophyBronze.IsCheckedChanged += (_, _) => ApplyTrophyFilter();
        chkTrophyShowHidden.IsCheckedChanged += (_, _) => ApplyTrophyFilter();
        searchTrophy.TextChanged += (_, _) => ApplyTrophyFilter();
        btnTrophySaveIcons.Click += (_, _) => _ = SaveVisibleTrophyIconsAsync();
        menuTrophySaveAllIcons.Click += (_, _) => _ = SaveVisibleTrophyIconsAsync();
        menuTrophySaveIcon.Click += (_, _) => _ = SaveSelectedTrophyIconAsync();
        menuTrophyExportCsv.Click += (_, _) => _ = ExportTrophiesAsync();
        btnTrophyExportCsv.Click += (_, _) => _ = ExportTrophiesAsync();
    }

    private void PopulateTrophies(Ps5TrophySet? set, string sectionError = "")
    {
        gridTrophies.Clear();
        if (set is not null)
        {
            string grades = string.Join(", ", set.Trophies.GroupBy(trophy => trophy.Grade)
                .Select(group => $"{group.Key}: {group.Count()}"));
            string languages = set.Languages.Count > 0 ? string.Join(", ", set.Languages) : "unknown";
            lblTrophySummary.Text = $"{set.Title} - {set.Trophies.Count} trophies ({grades}) - {set.NpCommunicationId} - " +
                                    $"Set version: {set.TrophySetVersion} - Language: {set.SelectedLanguage} (available: {languages}) - " +
                                    $"UCP integrity: {(set.IntegrityValid ? "Valid" : "Failed")} - " +
                                    "source: sce_sys/trophy2/trophy00.ucp";
        }
        else lblTrophySummary.Text = sectionError.Length > 0
            ? "Trophy archive could not be read: " + sectionError
            : "No sce_sys/trophy2/trophy00.ucp archive was found for this source.";
        _trophySummaryBase = lblTrophySummary.Text;
        ApplyTrophyFilter();
    }

    private void ApplyTrophyFilter()
    {
        if (_currentDetails?.TrophySet is not { } set)
        {
            _visibleTrophies = [];
            gridTrophies.Clear();
            return;
        }

        bool AnyGrade() => chkTrophyPlatinum.IsChecked == true || chkTrophyGold.IsChecked == true ||
                           chkTrophySilver.IsChecked == true || chkTrophyBronze.IsChecked == true;
        bool GradeAllowed(string grade) =>
            (chkTrophyPlatinum.IsChecked == true && grade == "Platinum") ||
            (chkTrophyGold.IsChecked == true && grade == "Gold") ||
            (chkTrophySilver.IsChecked == true && grade == "Silver") ||
            (chkTrophyBronze.IsChecked == true && grade == "Bronze");

        string search = searchTrophy.Text?.Trim() ?? string.Empty;
        bool showHidden = chkTrophyShowHidden.IsChecked == true;
        bool gradeFilter = AnyGrade();
        _visibleTrophies = set.Trophies.Where(trophy =>
                (!gradeFilter || GradeAllowed(trophy.Grade)) &&
                (showHidden || !trophy.Hidden) &&
                (search.Length == 0 ||
                 trophy.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                 trophy.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();

        gridTrophies.SetRows(_visibleTrophies.Select(trophy => new TableRow
        {
            Cells =
            [
                trophy.Id.ToString(), string.Empty, trophy.Grade, trophy.Hidden ? "Yes" : "No",
                trophy.Name, trophy.Description, trophy.UnlockCondition
            ],
            Tag = trophy,
            Icon = ToBitmap(trophy.Icon)
        }).ToList());

        bool filtered = gradeFilter || search.Length > 0 || !showHidden;
        lblTrophySummary.Text = filtered
            ? $"{_trophySummaryBase}  —  showing {_visibleTrophies.Count:N0} of {set.Trophies.Count:N0}"
            : _trophySummaryBase;
    }

    private async Task SaveVisibleTrophyIconsAsync()
    {
        if (_visibleTrophies.Count == 0)
        {
            AppDialog.ShowInfo("No trophies are available to save.", "Trophies");
            return;
        }
        string? folder = await PickFolderAsync("Select a folder for the trophy icons");
        if (folder is null) return;
        int saved = 0;
        foreach (Ps5Trophy trophy in _visibleTrophies)
        {
            if (trophy.IconPng is not { Length: > 0 }) continue;
            try
            {
                File.WriteAllBytes(Path.Combine(folder, $"trop{trophy.Id:000}.png"), trophy.IconPng);
                saved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.ShowError(ex.Message, "Save trophy icons");
                return;
            }
        }
        SetStatus(saved == 0
            ? "No trophy icons were available to save."
            : $"Saved {saved} trophy icon(s) to {folder}.");
    }

    private async Task SaveSelectedTrophyIconAsync()
    {
        if (gridTrophies.CurrentRow?.Tag is not Ps5Trophy trophy || trophy.IconPng is not { Length: > 0 })
        {
            AppDialog.ShowInfo("This trophy has no icon to save.", "Trophies");
            return;
        }
        string? path = await PickSaveFileAsync("Save trophy icon", $"trop{trophy.Id:000}.png",
            new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] });
        if (path is null) return;
        try
        {
            File.WriteAllBytes(path, trophy.IconPng);
            SetStatus($"Saved {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDialog.ShowError(ex.Message, "Save trophy icon");
        }
    }

    private async Task ExportTrophiesAsync()
    {
        if (_visibleTrophies.Count == 0)
        {
            AppDialog.ShowInfo("There are no trophies to export.", "Trophies");
            return;
        }
        string? path = await PickSaveFileAsync("Export trophies", "trophies.csv",
            new Avalonia.Platform.Storage.FilePickerFileType("CSV file") { Patterns = ["*.csv"] });
        if (path is null) return;
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("ID,Grade,Hidden,Name,Description,Unlock Condition");
            foreach (Ps5Trophy trophy in _visibleTrophies)
            {
                string[] fields =
                [
                    trophy.Id.ToString(), trophy.Grade, trophy.Hidden.ToString(), trophy.Name,
                    trophy.Description, trophy.UnlockCondition
                ];
                builder.AppendLine(string.Join(',', fields.Select(CsvField)));
            }
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            SetStatus($"Exported {_visibleTrophies.Count:N0} trophies.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppDialog.ShowError(ex.Message, "Export trophies");
        }
    }

    private static string CsvField(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    // ------------------------------------------------------ Activities & UDS

    private void InitializeActivities()
    {
        gridUdsEvents.SetColumns([
            new TableColumn { Name = "Event", Header = "Event", Weight = 12 },
            new TableColumn { Name = "Type", Header = "Type", Weight = 6 },
            new TableColumn { Name = "Group", Header = "Group", Weight = 8 },
            new TableColumn { Name = "Props", Header = "Props", FixedWidth = 70, RightAligned = true }
        ]);
        gridUdsEventProperties.SetColumns([
            new TableColumn { Name = "Property", Header = "Property", Weight = 12 },
            new TableColumn { Name = "Type", Header = "Type", Weight = 6 },
            new TableColumn { Name = "ItemType", Header = "Item type", Weight = 8 },
            new TableColumn { Name = "Mapped", Header = "Mapped", Weight = 10 }
        ]);
        gridUdsStats.SetColumns([
            new TableColumn { Name = "ID", Header = "ID", FixedWidth = 60, RightAligned = true },
            new TableColumn { Name = "Name", Header = "Name", Weight = 12 },
            new TableColumn { Name = "Group", Header = "Group", Weight = 8 },
            new TableColumn { Name = "Type", Header = "Type", Weight = 6 },
            new TableColumn { Name = "Aggregation", Header = "Aggregation", Weight = 8 },
            new TableColumn { Name = "Origin", Header = "Origin", Weight = 8 },
            new TableColumn { Name = "Enum", Header = "Enum", FixedWidth = 60 },
            new TableColumn { Name = "Min", Header = "Min", Weight = 5 },
            new TableColumn { Name = "Max", Header = "Max", Weight = 5 },
            new TableColumn { Name = "Initial", Header = "Initial", Weight = 6 },
            new TableColumn { Name = "TrophyRefs", Header = "Trophy refs", Weight = 7 }
        ]);
        gridUdsEnums.SetColumns([
            new TableColumn { Name = "Enum", Header = "Enum", FixedWidth = 70, RightAligned = true },
            new TableColumn { Name = "Group", Header = "Group", Weight = 10 },
            new TableColumn { Name = "Source", Header = "Source", Weight = 8 },
            new TableColumn { Name = "Values", Header = "Values", FixedWidth = 70, RightAligned = true },
            new TableColumn { Name = "Sample", Header = "Sample", Weight = 20 }
        ]);
        gridUdsRules.SetColumns([
            new TableColumn { Name = "Rule", Header = "Rule", FixedWidth = 60, RightAligned = true },
            new TableColumn { Name = "Group", Header = "Group", Weight = 8 },
            new TableColumn { Name = "Event", Header = "Event", Weight = 12 },
            new TableColumn { Name = "Condition", Header = "Condition", Weight = 12 },
            new TableColumn { Name = "Input", Header = "Input", Weight = 10 },
            new TableColumn { Name = "Convert", Header = "Convert", Weight = 10 },
            new TableColumn { Name = "Output", Header = "Output stat", Weight = 12 }
        ]);

        searchUds.TextChanged += (_, _) => RebuildUdsTables();
        tabsUds.SelectionChanged += (_, _) => RebuildUdsTables();
        gridUdsEvents.SelectionChanged += (_, _) => PopulateEventProperties();
        btnUdsCopyAll.Click += (_, _) => CopyActiveUdsGrid(copyAll: true);
        btnUdsCopySelected.Click += (_, _) => CopyActiveUdsGrid(copyAll: false);
    }

    private void PopulateActivities(Ps5UdsSummary? uds, string sectionError = "")
    {
        _udsSummary = uds;
        searchUds.Text = string.Empty;
        if (uds is null)
        {
            lblActivitiesSummary.Text = sectionError.Length > 0
                ? "Activity archive could not be read: " + sectionError
                : "No UDS activity archive was found.";
            gridUdsEvents.Clear();
            gridUdsEventProperties.Clear();
            gridUdsStats.Clear();
            gridUdsEnums.Clear();
            gridUdsRules.Clear();
            return;
        }

        lblActivitiesSummary.Text =
            $"Events: {uds.EventCount:N0}  |  Stats: {uds.StatCount:N0}  |  Enum groups: {uds.EnumGroupCount:N0}  |  " +
            $"Extraction rules: {uds.ExtractionRuleCount:N0}  |  {uds.NpCommunicationId}  |  " +
            $"UCP integrity: {(uds.IntegrityValid ? "Valid" : "Failed")}";
        RebuildUdsTables();
    }

    private void RebuildUdsTables()
    {
        if (_udsSummary is not { } uds) return;
        string filter = searchUds.Text?.Trim() ?? string.Empty;

        gridUdsEvents.SetRows(uds.Events
            .Where(item => Matches(filter, item.Name, item.Type, item.DefinitionGroup))
            .Select(item => new TableRow
            {
                Cells = [item.Name, item.Type, item.DefinitionGroup, item.PropertyCount.ToString()],
                Tag = item
            }).ToList());

        gridUdsStats.SetRows(uds.Stats
            .Where(stat => Matches(filter, stat.Name, stat.DefinitionGroup, stat.DataType, stat.Aggregation,
                stat.Origin, stat.StatId.ToString(), stat.SourceId))
            .Select(stat => new TableRow
            {
                Cells =
                [
                    stat.StatId.ToString(), stat.Name, stat.DefinitionGroup, stat.DataType, stat.Aggregation,
                    stat.Origin, stat.EnumId.ToString(), stat.MinValue, stat.MaxValue, stat.InitialValue,
                    TrophyRefs(stat.StatId)
                ]
            }).ToList());

        gridUdsEnums.SetRows(uds.EnumGroups
            .Where(group => Matches(filter, group.SourceId, group.DefinitionGroup, group.EnumId.ToString(),
                string.Join(' ', group.Values)))
            .Select(group => new TableRow
            {
                Cells =
                [
                    group.EnumId.ToString(), group.DefinitionGroup, group.SourceId, group.ValueCount.ToString(),
                    string.Join(", ", group.Values.Take(4))
                ]
            }).ToList());

        gridUdsRules.SetRows(uds.Rules
            .Where(rule => Matches(filter, rule.EventName, rule.DefinitionGroup, rule.OutputStatName,
                rule.SourceId, rule.Input, rule.Condition))
            .Select(rule => new TableRow
            {
                Cells =
                [
                    rule.RuleId.ToString(), rule.DefinitionGroup, rule.EventName, rule.Condition, rule.Input,
                    rule.Convert, $"{rule.OutputStatName} (#{rule.OutputStatId})"
                ]
            }).ToList());

        PopulateEventProperties();
    }

    private void PopulateEventProperties()
    {
        var rows = new List<TableRow>();
        if (_udsSummary is { } uds && gridUdsEvents.CurrentRow?.Tag is Ps5UdsEvent selected)
        {
            rows.AddRange(selected.Properties.Select(property => new TableRow
            {
                Cells = [property.Path, property.DataType, property.ItemType, property.MappedProperty]
            }));
        }
        gridUdsEventProperties.SetRows(rows);
    }

    private string TrophyRefs(int statId)
    {
        if (_currentDetails?.TrophySet is not { } set) return string.Empty;
        List<int> ids = set.Trophies.Where(trophy => trophy.UdsStatId == statId).Select(trophy => trophy.Id).ToList();
        return ids.Count == 0 ? string.Empty : string.Join(", ", ids);
    }

    private static bool Matches(string filter, params string?[] values) =>
        filter.Length == 0 ||
        values.Any(value => value is not null && value.Contains(filter, StringComparison.CurrentCultureIgnoreCase));

    private TableView ActiveUdsGrid() =>
        ReferenceEquals(tabsUds.SelectedItem, tabUdsStats) ? gridUdsStats
        : ReferenceEquals(tabsUds.SelectedItem, tabUdsEnums) ? gridUdsEnums
        : ReferenceEquals(tabsUds.SelectedItem, tabUdsRules) ? gridUdsRules
        : gridUdsEvents;

    private void CopyActiveUdsGrid(bool copyAll)
    {
        TableView grid = ActiveUdsGrid();
        IReadOnlyList<TableRow> rows = copyAll ? grid.Rows : grid.SelectedRows;
        var builder = new StringBuilder();
        foreach (TableRow row in rows)
        {
            if (row.IsGroup) continue;
            for (int index = 0; index < row.Cells.Length && index < grid.Columns.Count; index++)
                builder.Append(grid.Columns[index].Header).Append(": ").Append(row.Cells[index]).AppendLine();
            builder.AppendLine();
        }
        if (builder.Length == 0)
        {
            AppDialog.ShowInfo("Nothing to copy.", "Activities & UDS");
            return;
        }
        CopyText(builder.ToString().TrimEnd());
    }

    // ------------------------------------------------------------------ Files

    private void InitializeFiles()
    {
        listFiles.SetColumns([
            new TableColumn { Name = "Name", Header = "Name", Weight = 20 },
            new TableColumn { Name = "Type", Header = "Type", Weight = 9 },
            new TableColumn { Name = "Path", Header = "Path", Weight = 24 },
            new TableColumn { Name = "Size", Header = "Size", Weight = 7, RightAligned = true },
            new TableColumn { Name = "Origin", Header = "Origin", FixedWidth = 90 }
        ]);
        listFiles.ShowGridLines = false;
        listFiles.SelectionChanged += (_, _) => _ = PreviewSelectedFileAsync();
        listFiles.RowDoubleClicked += (_, args) =>
        {
            if (args.Row?.Tag is not FileBrowserEntry entry) return;
            if (entry.IsDirectory) NavigateToDirectory(entry.RelativePath);
            else RevealFile(entry);
        };
        listFiles.ColumnHeaderClicked += (_, args) =>
        {
            int column = listFiles.Columns.ToList().IndexOf(args.Column);
            if (column == _fileSortColumn) _fileSortAscending = !_fileSortAscending;
            else { _fileSortColumn = column; _fileSortAscending = true; }
            RefreshFileList();
        };

        treeFiles.SelectionChanged += (_, _) => RefreshFileList();
        searchFileFilter.TextChanged += (_, _) => RefreshFileList();
        menuTreeExpand.Click += (_, _) => SelectedTreeNode()?.IsExpanded = true;
        menuTreeCollapse.Click += (_, _) => SelectedTreeNode()?.IsExpanded = false;
        menuTreeExpandAll.Click += (_, _) => SetTreeExpansion(treeFiles.Items.OfType<TreeViewItem>(), true);
        menuTreeCollapseAll.Click += (_, _) => SetTreeExpansion(treeFiles.Items.OfType<TreeViewItem>(), false);
        menuTreeCopyPath.Click += (_, _) =>
        {
            string path = SelectedTreeNode()?.Tag as string ?? string.Empty;
            CopyText(path.Length > 0 ? path : _currentGameRoot);
        };
        menuTreeExtract.Click += (_, _) => _ = ExtractFolderAsync();
        btnFileExtractAll.Click += (_, _) => _ = ExtractAllFilesAsync();
        menuFileExtractSelected.Click += (_, _) => _ = ExtractSelectedFilesAsync();
        menuFileExtractContained.Click += (_, _) => _ = ExtractCurrentFileAsync();
        menuFileOpenContained.Click += (_, _) =>
        {
            if (listFiles.CurrentRow?.Tag is FileBrowserEntry entry)
                if (entry.IsDirectory) NavigateToDirectory(entry.RelativePath); else RevealFile(entry);
        };
        menuFileCopyPath.Click += (_, _) =>
        {
            if (listFiles.CurrentRow?.Tag is FileBrowserEntry entry) CopyText(entry.RelativePath);
        };
        menuFileCopyName.Click += (_, _) =>
        {
            if (listFiles.CurrentRow?.Tag is FileBrowserEntry entry)
                CopyText(Path.GetFileName(entry.RelativePath));
        };
        menuFileRevealContainer.Click += (_, _) =>
        {
            if (_selectedGame is { } game) RevealPath(game.RootPath, "Reveal container");
        };
        btnFileCancel.Click += (_, _) =>
        {
            _fileCancellation?.Cancel();
            _isFileBusy = false;
            UpdateOperationState();
        };
        btnHexPrevious.Click += (_, _) => _ = NavigateHexAsync(Math.Max(0, _hexPreviewOffset - HexPageSize()));
        btnHexNext.Click += (_, _) => _ = NavigateHexAsync(_hexPreviewOffset + HexPageSize());
        btnMediaLoad.Click += (_, _) => _ = LoadMediaAsync();
        btnMediaPlay.Click += (_, _) => _mediaPlayer?.Play();
        btnMediaPause.Click += (_, _) => _mediaPlayer?.Pause();
        btnMediaStop.Click += (_, _) => _mediaPlayer?.Stop();
    }

    private TreeViewItem? SelectedTreeNode() => treeFiles.SelectedItem as TreeViewItem;

    private static void SetTreeExpansion(IEnumerable<TreeViewItem> nodes, bool expanded)
    {
        foreach (TreeViewItem node in nodes)
        {
            node.IsExpanded = expanded;
            SetTreeExpansion(node.Items.OfType<TreeViewItem>(), expanded);
        }
    }

    private void PopulateFiles(Ps5GameInfo game, Ps5FileInventory inventory)
    {
        _currentGameRoot = game.RootPath;
        _currentSourceIsContainer = game.SourceKind != Ps5SourceKind.LooseDump;
        _directoryNodes.Clear();
        _filesByDirectory.Clear();
        treeFiles.Items.Clear();

        var root = new TreeViewItem { Header = game.Title, Tag = string.Empty, IsExpanded = true };
        treeFiles.Items.Add(root);
        _directoryNodes[string.Empty] = root;

        foreach (Ps5FileInfo file in inventory.Files)
        {
            string directory = NormalizeDirectory(Path.GetDirectoryName(file.RelativePath));
            if (!_filesByDirectory.TryGetValue(directory, out List<Ps5FileInfo>? directoryFiles))
            {
                directoryFiles = [];
                _filesByDirectory[directory] = directoryFiles;
            }
            directoryFiles.Add(file);

            if (directory.Length == 0) continue;
            string current = string.Empty;
            TreeViewItem parent = root;
            foreach (string segment in directory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? segment : Path.Combine(current, segment);
                if (!_directoryNodes.TryGetValue(current, out TreeViewItem? node))
                {
                    node = new TreeViewItem { Header = segment, Tag = current };
                    parent.Items.Add(node);
                    _directoryNodes[current] = node;
                }
                parent = node;
            }
        }

        _directorySizes.Clear();
        foreach (KeyValuePair<string, List<Ps5FileInfo>> pair in _filesByDirectory)
        {
            long directSize = 0;
            foreach (Ps5FileInfo file in pair.Value) directSize += file.Size;
            string current = pair.Key;
            while (true)
            {
                _directorySizes[current] = _directorySizes.GetValueOrDefault(current) + directSize;
                if (current.Length == 0) break;
                current = NormalizeDirectory(Path.GetDirectoryName(current));
            }
        }

        SortTree(root);
        treeFiles.SelectedItem = root;
        _filesSummaryBase = game.SourceKind switch
        {
            Ps5SourceKind.SonyPackage => game.Package?.NestedPfs?.AccessState == SonyPfsAccessState.PlaintextIndexed
                ? $"{inventory.FileCount:N0} files - {FormatBytes(inventory.TotalSize)} content - inner PFS files with readable CNT metadata merged"
                : $"{inventory.FileCount:N0} readable CNT metadata entries - {FormatBytes(inventory.TotalSize)} package " +
                  "(the game filesystem was not decoded; this is not the full file set" +
                  (game.Package is { EncryptedEntryCount: > 0 } encrypted
                      ? $"; {encrypted.EncryptedEntryCount:N0} encrypted entr{(encrypted.EncryptedEntryCount == 1 ? "y is" : "ies are")} not readable"
                      : string.Empty) + ")",
            Ps5SourceKind.Ffpfsc =>
                $"{inventory.FileCount:N0} files - {FormatBytes(inventory.TotalSize)} logical " +
                $"{(Path.GetExtension(game.ContainerInnerFileName).Equals(".ffpkg", StringComparison.OrdinalIgnoreCase) ? "FFPKG" : "exFAT")} content - read directly from FFPFSC",
            Ps5SourceKind.FilesystemImage =>
                $"{inventory.FileCount:N0} files - {FormatBytes(inventory.TotalSize)} content - read directly from {Path.GetExtension(game.RootPath).TrimStart('.').ToUpperInvariant()}",
            Ps5SourceKind.Ffpkg =>
                $"{inventory.FileCount:N0} files - {FormatBytes(inventory.TotalSize)} logical content - read directly from the FFPKG",
            _ => $"{inventory.FileCount:N0} files - {FormatBytes(inventory.TotalSize)} total - double-click folders to browse or files to reveal them"
        };
        lblFilesSummary.Text = _filesSummaryBase;
        RefreshFileList();
    }

    private static void SortTree(TreeViewItem parent)
    {
        TreeViewItem[] children = parent.Items.OfType<TreeViewItem>()
            .OrderBy(node => node.Header?.ToString(), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        parent.Items.Clear();
        foreach (TreeViewItem child in children) parent.Items.Add(child);
        foreach (TreeViewItem child in children) SortTree(child);
    }

    private string CurrentDirectory() => SelectedTreeNode()?.Tag as string ?? string.Empty;

    private void NavigateToDirectory(string directory)
    {
        if (_directoryNodes.TryGetValue(directory, out TreeViewItem? node))
        {
            node.IsExpanded = true;
            treeFiles.SelectedItem = node;
        }
    }

    private void RefreshFileList()
    {
        string directory = CurrentDirectory();
        string filter = searchFileFilter.SearchText.Trim();
        var rows = new List<FileListRow>();
        bool truncated = false;

        if (filter.Length == 0)
        {
            if (directory.Length > 0)
            {
                string parent = NormalizeDirectory(Path.GetDirectoryName(directory));
                rows.Add(new FileListRow("..", "Folder", parent, string.Empty, 0,
                    new FileBrowserEntry(parent, true), true, string.Empty));
            }

            foreach (string child in _directoryNodes.Keys.Where(path =>
                path.Length > 0 && NormalizeDirectory(Path.GetDirectoryName(path)).Equals(directory, StringComparison.OrdinalIgnoreCase)))
            {
                if (rows.Count >= MaxFileListRows) { truncated = true; break; }
                long size = _directorySizes.GetValueOrDefault(child);
                rows.Add(new FileListRow(Path.GetFileName(child), "Folder", child, FormatBytes(size), size,
                    new FileBrowserEntry(child, true), true, string.Empty));
            }

            if (!truncated && _filesByDirectory.TryGetValue(directory, out List<Ps5FileInfo>? files))
                foreach (Ps5FileInfo file in files)
                {
                    if (rows.Count >= MaxFileListRows) { truncated = true; break; }
                    rows.Add(FileListRowFor(file));
                }
        }
        else
        {
            foreach (string child in _directoryNodes.Keys.Where(path => path.Length > 0))
            {
                if (rows.Count >= MaxFileListRows) { truncated = true; break; }
                string name = Path.GetFileName(child);
                if (!MatchesFileFilter(name, child, filter)) continue;
                long size = _directorySizes.GetValueOrDefault(child);
                rows.Add(new FileListRow(name, "Folder", child, FormatBytes(size), size,
                    new FileBrowserEntry(child, true), true, string.Empty));
            }

            if (!truncated)
                foreach (List<Ps5FileInfo> list in _filesByDirectory.Values)
                {
                    if (truncated) break;
                    foreach (Ps5FileInfo file in list)
                    {
                        if (rows.Count >= MaxFileListRows) { truncated = true; break; }
                        if (MatchesFileFilter(Path.GetFileName(file.RelativePath), file.RelativePath, filter))
                            rows.Add(FileListRowFor(file));
                    }
                }
        }

        rows.Sort(CompareFileRows);
        listFiles.SetRows(rows.Select(row => new TableRow
        {
            Cells = [row.Name, row.Type, row.Path, row.SizeText, row.Origin],
            Tag = row.Entry
        }).ToList());

        if (_filesSummaryBase.Length > 0)
            lblFilesSummary.Text = truncated
                ? $"{_filesSummaryBase} - showing the first {rows.Count:N0} rows; refine the search or open a subfolder"
                : _filesSummaryBase;
    }

    private static FileListRow FileListRowFor(Ps5FileInfo file)
    {
        string name = Path.GetFileName(file.RelativePath);
        string type = string.IsNullOrWhiteSpace(file.Extension)
            ? "File"
            : file.Extension.TrimStart('.').ToUpperInvariant() + " File";
        if (file.IsEncrypted) type += " (Encrypted)";
        return new FileListRow(name, type, file.RelativePath, FormatBytes(file.Size), file.Size,
            new FileBrowserEntry(file.RelativePath, false, file.Size), false, file.Origin);
    }

    private int CompareFileRows(FileListRow left, FileListRow right)
    {
        int group = right.IsDirectory.CompareTo(left.IsDirectory);
        if (group != 0) return group;
        int result = _fileSortColumn switch
        {
            1 => string.Compare(left.Type, right.Type, StringComparison.CurrentCultureIgnoreCase),
            2 => string.Compare(left.Path, right.Path, StringComparison.CurrentCultureIgnoreCase),
            3 => left.Size.CompareTo(right.Size),
            _ => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase)
        };
        if (!_fileSortAscending) result = -result;
        return result != 0
            ? result
            : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool MatchesFileFilter(string name, string path, string filter) =>
        filter.Length == 0 ||
        name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        path.Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    private static string NormalizeDirectory(string? directory) =>
        string.IsNullOrWhiteSpace(directory) || directory == "."
            ? string.Empty
            : directory.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);

    private List<(string RelativePath, long Size)> SelectedFileTargets()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (TableRow row in listFiles.SelectedRows)
        {
            if (row.Tag is not FileBrowserEntry entry) continue;
            if (entry.IsDirectory)
                foreach ((string relativePath, long size) in FilesUnder(entry.RelativePath))
                    result[relativePath] = size;
            else
                result[entry.RelativePath] = entry.Size;
        }
        return result.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    private List<(string RelativePath, long Size)> FilesUnder(string folder)
    {
        var result = new List<(string, long)>();
        string prefix = folder.Length == 0 ? string.Empty : folder + Path.DirectorySeparatorChar;
        foreach (List<Ps5FileInfo> list in _filesByDirectory.Values)
            foreach (Ps5FileInfo file in list)
                if (prefix.Length == 0 ||
                    file.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add((file.RelativePath, file.Size));
        return result;
    }

    private async Task ExtractSelectedFilesAsync()
    {
        List<(string RelativePath, long Size)> files = SelectedFileTargets();
        if (files.Count == 0)
        {
            AppDialog.ShowInfo("Select one or more files to extract.", "Files");
            return;
        }
        string? folder = await PickFolderAsync("Select a folder for the extracted files");
        if (folder is not null) await ExtractFilesAsync(files, folder);
    }

    private async Task ExtractCurrentFileAsync()
    {
        if (listFiles.CurrentRow?.Tag is not FileBrowserEntry entry || entry.IsDirectory) return;
        if (_selectedGame is not { } game || !IsBrowsableFilesystem(game.SourceKind) || _isFileBusy) return;
        string? destination = await PickSaveFileAsync("Extract file", Path.GetFileName(entry.RelativePath),
            Avalonia.Platform.Storage.FilePickerFileTypes.All);
        if (destination is null) return;

        _fileCancellation?.Cancel();
        _fileCancellation?.Dispose();
        _fileCancellation = new CancellationTokenSource();
        _isFileBusy = true;
        UpdateOperationState();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var progress = new Progress<long>(copied =>
                SetStatus($"Extracting {Path.GetFileName(entry.RelativePath)}: {FormatBytes(copied)} / {FormatBytes(entry.Size)}"));
            await Task.Run(() => GameFileSystem.ExtractFileAsync(game, entry.RelativePath, destination, progress,
                _fileCancellation.Token), _fileCancellation.Token);
            SetStatus($"Extracted {Path.GetFileName(destination)}.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Extraction cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException or System.ComponentModel.Win32Exception)
        {
            SetStatus("Extraction failed.");
            AppDialog.ShowError(ex.Message, "Extract file");
        }
        finally
        {
            _isFileBusy = false;
            UpdateOperationState();
        }
    }

    private async Task ExtractFolderAsync()
    {
        string folder = CurrentDirectory();
        List<(string RelativePath, long Size)> files = FilesUnder(folder);
        if (files.Count == 0)
        {
            AppDialog.ShowInfo("There are no files under this folder.", "Files");
            return;
        }
        string? destination = await PickFolderAsync("Select a folder for the extracted files");
        if (destination is not null) await ExtractFilesAsync(files, destination);
    }

    private async Task ExtractAllFilesAsync()
    {
        List<(string RelativePath, long Size)> files = FilesUnder(string.Empty);
        if (files.Count == 0)
        {
            AppDialog.ShowInfo("There are no files to extract.", "Files");
            return;
        }
        string? destination = await PickFolderAsync("Select a folder for the extracted package");
        if (destination is not null) await ExtractFilesAsync(files, destination);
    }

    private async Task ExtractFilesAsync(IReadOnlyList<(string RelativePath, long Size)> files, string destination)
    {
        if (_selectedGame is not { } game || !IsBrowsableFilesystem(game.SourceKind) || _isFileBusy) return;
        _fileCancellation?.Cancel();
        _fileCancellation?.Dispose();
        _fileCancellation = new CancellationTokenSource();
        _isFileBusy = true;
        UpdateOperationState();
        string root = Path.GetFullPath(destination);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long total = files.Sum(file => file.Size);
        long completed = 0;
        int extracted = 0;
        try
        {
            foreach ((string relativePath, long size) in files)
            {
                _fileCancellation.Token.ThrowIfCancellationRequested();
                string target = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!target.StartsWith(rootPrefix, PathIdentity.Comparison))
                    throw new IOException("The extraction path left the destination folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                long baseline = completed;
                var progress = new Progress<long>(copied =>
                    SetStatus($"Extracting {Path.GetFileName(relativePath)}: {FormatBytes(baseline + copied)} / {FormatBytes(total)}"));
                await Task.Run(() => GameFileSystem.ExtractFileAsync(game, relativePath, target, progress,
                    _fileCancellation.Token), _fileCancellation.Token);
                completed += size;
                extracted++;
            }
            SetStatus($"Extracted {extracted:N0} file(s) to {destination}.");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Extraction cancelled after {extracted:N0} file(s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException or System.ComponentModel.Win32Exception)
        {
            SetStatus("Extraction failed.");
            AppDialog.ShowError(ex.Message, "Extract files");
        }
        finally
        {
            _isFileBusy = false;
            UpdateOperationState();
        }
    }

    private void RevealFile(FileBrowserEntry entry)
    {
        if (_selectedGame is not { } game) return;
        if (game.SourceKind == Ps5SourceKind.LooseDump)
        {
            string path = Path.Combine(game.RootPath, entry.RelativePath);
            if (File.Exists(path)) RevealPath(path, "Reveal file");
        }
        else if (entry.Size <= 256L * 1024 * 1024)
        {
            _ = ExtractSingleForPreviewAsync(entry);
        }
    }

    private async Task ExtractSingleForPreviewAsync(FileBrowserEntry entry)
    {
        if (_selectedGame is not { } game) return;
        try
        {
            Directory.CreateDirectory(_previewDirectory);
            string target = Path.Combine(_previewDirectory, Guid.NewGuid().ToString("N"),
                Path.GetFileName(entry.RelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await Task.Run(() => GameFileSystem.ExtractFileAsync(game, entry.RelativePath, target));
            RevealPath(target, "Open file");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException)
        {
            AppDialog.ShowError(ex.Message, "Open file");
        }
    }

    private async Task PreviewSelectedFileAsync()
    {
        if (!_settings.ShowFilePreview) return;
        _filePreviewCancellation?.Cancel();
        _filePreviewCancellation?.Dispose();
        _filePreviewCancellation = new CancellationTokenSource();
        int version = Interlocked.Increment(ref _filePreviewVersion);
        ResetFileViewer();
        if (listFiles.CurrentRow?.Tag is not FileBrowserEntry entry || entry.IsDirectory || _selectedGame is null)
            return;

        _filePreviewEntry = entry;
        Ps5FileInfo? info = _filesByDirectory.Values.SelectMany(list => list)
            .FirstOrDefault(file => string.Equals(file.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase));
        _filePreviewInfo = info;
        string extension = (info?.Extension ?? Path.GetExtension(entry.RelativePath)).ToLowerInvariant();
        lblFileViewerInfo.Text = $"{entry.RelativePath} — {FormatBytes(entry.Size)}";
        try
        {
            if (IsMediaExtension(extension))
            {
                ShowViewer(media: true);
                lblFileViewerInfo.Text += " — load the file to play it.";
                return;
            }
            bool image = IsImageExtension(extension);
            if (image && entry.Size <= 256L * 1024 * 1024 && entry.Size <= int.MaxValue)
            {
                GameFileChunk chunk = await Task.Run(() => GameFileSystem.ReadFileChunk(_selectedGame,
                    entry.RelativePath, 0, (int)entry.Size, _filePreviewCancellation.Token));
                if (version != _filePreviewVersion) return;
                try
                {
                    using var stream = new MemoryStream(chunk.Data);
                    pictureFileViewer.Source = new Bitmap(stream);
                    ShowViewer(image: true);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
                {
                    txtFileViewer.Text = "Image could not be decoded.";
                    ShowViewer(text: true);
                }
                return;
            }
            if (IsTextExtension(extension) && entry.Size <= 4 * 1024 * 1024)
            {
                GameFileChunk chunk = await Task.Run(() => GameFileSystem.ReadFileChunk(_selectedGame,
                    entry.RelativePath, 0, (int)entry.Size, _filePreviewCancellation.Token));
                if (version != _filePreviewVersion) return;
                string text = Encoding.UTF8.GetString(chunk.Data);
                if (LooksLikeText(text))
                {
                    txtFileViewer.Text = text;
                    ShowViewer(text: true);
                    return;
                }
            }
            _hexPreviewOffset = 0;
            await LoadHexAsync(version);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException or ArgumentException)
        {
            if (version == _filePreviewVersion)
                lblFileViewerInfo.Text = $"Preview unavailable: {ex.Message}";
        }
    }

    private static bool IsImageExtension(string extension) => extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".dds";

    private static bool IsTextExtension(string extension) => extension is ".json" or ".txt" or ".xml" or ".csv" or
        ".ini" or ".cfg" or ".log" or ".sfo" or ".param" or ".html" or ".css" or ".js" or ".yml" or ".yaml";

    private static bool IsMediaExtension(string extension) => extension is ".mp4" or ".webm" or ".mkv" or ".mp3" or
        ".wav" or ".ogg" or ".flac" or ".m4a" or ".at9" or ".ac3";

    private static bool LooksLikeText(string text) =>
        text.Length > 0 && text.All(character => character == '\n' || character == '\r' || character == '\t' ||
            !char.IsControl(character));

    private static bool IsBrowsableFilesystem(Ps5SourceKind sourceKind) => sourceKind is
        Ps5SourceKind.LooseDump or Ps5SourceKind.SonyPackage or Ps5SourceKind.Ffpfsc or
        Ps5SourceKind.FilesystemImage or Ps5SourceKind.Ffpkg;

    private int HexPageSize() => Math.Max(1, _settings.HexPageKb) * 1024;

    private async Task NavigateHexAsync(long offset)
    {
        _hexPreviewOffset = Math.Max(0, offset);
        await LoadHexAsync(++_filePreviewVersion);
    }

    private async Task LoadHexAsync(int version)
    {
        if (_selectedGame is not { } game || _filePreviewEntry is not { } entry) return;
        int pageSize = HexPageSize();
        GameFileChunk chunk = await Task.Run(() => GameFileSystem.ReadFileChunk(game, entry.RelativePath,
            _hexPreviewOffset, pageSize));
        if (version != _filePreviewVersion) return;
        txtHexViewer.Text = FormatHexPage(chunk);
        ShowViewer(hex: true);
        lblHexPage.IsVisible = true;
        lblHexPage.Text = $"0x{_hexPreviewOffset:X} - 0x{_hexPreviewOffset + chunk.Data.Length:X} of 0x{chunk.FileSize:X}";
        btnHexPrevious.IsVisible = _hexPreviewOffset > 0;
        btnHexNext.IsVisible = _hexPreviewOffset + chunk.Data.Length < chunk.FileSize;
    }

    private static string FormatHexPage(GameFileChunk chunk)
    {
        var builder = new StringBuilder();
        for (int offset = 0; offset < chunk.Data.Length; offset += 16)
        {
            int count = Math.Min(16, chunk.Data.Length - offset);
            builder.Append((chunk.Offset + offset).ToString("X8")).Append("  ");
            for (int index = 0; index < 16; index++)
            {
                if (index == 8) builder.Append(' ');
                builder.Append(index < count ? chunk.Data[offset + index].ToString("X2") + " " : "   ");
            }
            builder.Append(' ');
            for (int index = 0; index < count; index++)
            {
                byte value = chunk.Data[offset + index];
                builder.Append(value is >= 32 and < 127 ? (char)value : '.');
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private void ShowViewer(bool image = false, bool text = false, bool hex = false, bool media = false)
    {
        pictureFileViewer.IsVisible = image;
        txtFileViewer.IsVisible = text;
        txtHexViewer.IsVisible = hex;
        mediaFileHost.IsVisible = media;
        btnMediaLoad.IsVisible = media;
        bool player = media && _mediaPlayer is not null;
        btnMediaPlay.IsVisible = player;
        btnMediaPause.IsVisible = player;
        btnMediaStop.IsVisible = player;
        btnHexPrevious.IsVisible = hex && _hexPreviewOffset > 0;
        btnHexNext.IsVisible = hex;
        lblHexPage.IsVisible = hex;
    }

    private void ResetFileViewer()
    {
        pictureFileViewer.Source = null;
        pictureFileViewer.IsVisible = false;
        txtFileViewer.Text = string.Empty;
        txtFileViewer.IsVisible = false;
        txtHexViewer.Text = string.Empty;
        txtHexViewer.IsVisible = false;
        lblHexPage.IsVisible = false;
        mediaFileHost.IsVisible = false;
        btnMediaLoad.IsVisible = false;
        btnMediaPlay.IsVisible = false;
        btnMediaPause.IsVisible = false;
        btnMediaStop.IsVisible = false;
        btnHexPrevious.IsVisible = false;
        btnHexNext.IsVisible = false;
        _mediaPlayer?.Stop();
        lblFileViewerInfo.Text = "Select a file to preview it.";
    }

    private async Task LoadMediaAsync()
    {
        if (_selectedGame is not { } game || _filePreviewEntry is not { } entry) return;
        try
        {
            Directory.CreateDirectory(_previewDirectory);
            string target = Path.Combine(_previewDirectory, "media-" + Guid.NewGuid().ToString("N"),
                Path.GetFileName(entry.RelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await Task.Run(() => GameFileSystem.ExtractFileAsync(game, entry.RelativePath, target));
            EnsureMediaPlayer();
            if (_mediaPlayer is null || _libVlc is null) return;
            using var media = new Media(_libVlc, new Uri(target));
            _mediaPlayer.Media = media;
            _mediaPlayer.Play();
            ShowViewer(media: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException or DllNotFoundException or TypeInitializationException)
        {
            lblFileViewerInfo.Text = "Media preview unavailable: " + ex.Message;
        }
    }

    private void EnsureMediaPlayer()
    {
        if (_mediaPlayer is not null) return;
        try
        {
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc);
            mediaFileViewer.MediaPlayer = _mediaPlayer;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            lblFileViewerInfo.Text = "libVLC is not available on this system: " + ex.Message;
        }
    }

    // ------------------------------------------------------------- Executable

    private void InitializeExecutable()
    {
        gridModules.SetColumns([
            new TableColumn { Name = "Name", Header = "Name", Weight = 18 },
            new TableColumn { Name = "Path", Header = "Path", Weight = 26 },
            new TableColumn { Name = "Kind", Header = "Kind", Weight = 8 },
            new TableColumn { Name = "Size", Header = "Size", Weight = 8, RightAligned = true }
        ]);
        gridElfPrograms.SetColumns([
            new TableColumn { Name = "Index", Header = "#", FixedWidth = 50 },
            new TableColumn { Name = "Type", Header = "Type", Weight = 8 },
            new TableColumn { Name = "Flags", Header = "Flags", Weight = 7 },
            new TableColumn { Name = "Offset", Header = "Offset", Weight = 8 },
            new TableColumn { Name = "VAddr", Header = "Virtual address", Weight = 10 },
            new TableColumn { Name = "PAddr", Header = "Physical address", Weight = 10 },
            new TableColumn { Name = "FileSize", Header = "File size", Weight = 8 },
            new TableColumn { Name = "MemorySize", Header = "Memory size", Weight = 8 },
            new TableColumn { Name = "Align", Header = "Align", Weight = 6 }
        ]);
        gridElfSections.SetColumns([
            new TableColumn { Name = "Index", Header = "#", FixedWidth = 50 },
            new TableColumn { Name = "Name", Header = "Name offset", Weight = 8 },
            new TableColumn { Name = "Type", Header = "Type", Weight = 8 },
            new TableColumn { Name = "Flags", Header = "Flags", Weight = 7 },
            new TableColumn { Name = "Address", Header = "Address", Weight = 10 },
            new TableColumn { Name = "Offset", Header = "Offset", Weight = 8 },
            new TableColumn { Name = "Size", Header = "Size", Weight = 8 }
        ]);
        gridSelfHeader.SetColumns([
            new TableColumn { Name = "Property", Header = "Property", FixedWidth = 220 },
            new TableColumn { Name = "Value", Header = "Value", Weight = 10 }
        ]);
        gridSelfSegments.SetColumns([
            new TableColumn { Name = "Index", Header = "#", FixedWidth = 50 },
            new TableColumn { Name = "Flags", Header = "Flags", Weight = 8 },
            new TableColumn { Name = "Offset", Header = "File offset", Weight = 10 },
            new TableColumn { Name = "FileSize", Header = "File size", Weight = 8 },
            new TableColumn { Name = "MemorySize", Header = "Memory size", Weight = 8 }
        ]);

        searchExecutable.TextChanged += (_, _) => _ = PopulateExecutableAsync();
        btnExecExtract.Click += (_, _) => _ = ExtractExecutableAsync();
        btnExecHash.Click += (_, _) => _ = ShowExecutableHashAsync();
        btnExecCopyAll.Click += (_, _) => CopyActiveExecutable(copyAll: true);
        btnExecCopySelected.Click += (_, _) => CopyActiveExecutable(copyAll: false);
    }

    private void PopulateExecutable(Ps5SelfInfo? info, string sectionError = "")
    {
        _executableInfo = info;
        if (info is null)
        {
            lblExecutableSummary.Text = sectionError.Length > 0
                ? "Executable could not be read: " + sectionError
                : "No eboot.bin is available for this source.";
            gridModules.Clear();
            gridElfPrograms.Clear();
            gridElfSections.Clear();
            gridSelfHeader.Clear();
            gridSelfSegments.Clear();
            return;
        }

        lblExecutableSummary.Text =
            $"{(info.IsSelf ? "SELF" : "ELF")} - {FormatBytes(info.FileSize)} - " +
            $"machine {MachineText(info.Machine)} - class {info.ElfClass} - " +
            $"type 0x{info.ElfType:X4} - entry 0x{info.EntryPoint:X} - " +
            $"modules: {info.Modules.Count:N0}";
        _ = PopulateExecutableAsync();

        gridElfPrograms.SetRows(info.ProgramHeaders.Select(header => new TableRow
        {
            Cells =
            [
                header.Index.ToString(), $"0x{header.Type:X8}", $"0x{header.Flags:X8}", $"0x{header.Offset:X}",
                $"0x{header.VirtualAddress:X}", $"0x{header.PhysicalAddress:X}", FormatBytes(header.FileSize),
                FormatBytes(header.MemorySize), $"0x{header.Align:X}"
            ]
        }).ToList());

        gridElfSections.SetRows(info.SectionHeaders.Select(section => new TableRow
        {
            Cells =
            [
                section.Index.ToString(), $"0x{section.Name:X}", $"0x{section.Type:X8}", $"0x{section.Flags:X}",
                $"0x{section.Address:X}", $"0x{section.Offset:X}", FormatBytes(section.Size)
            ]
        }).ToList());

        var headerRows = new List<TableRow>();
        void Add(string property, object? value) =>
            headerRows.Add(new TableRow { Cells = [property, value?.ToString() ?? string.Empty] });
        Add("Container", info.IsSelf ? "SELF (" + info.SelfMagic + ")" : "ELF");
        Add("File size", FormatBytes(info.FileSize));
        Add("ELF offset", $"0x{info.ElfOffset:X}");
        Add("ELF class", info.ElfClass == 2 ? "ELF64" : info.ElfClass.ToString());
        Add("Endianness", info.Endianness == 1 ? "little-endian" : info.Endianness.ToString());
        Add("ELF type", $"0x{info.ElfType:X4}");
        Add("Machine", MachineText(info.Machine));
        Add("Entry point", $"0x{info.EntryPoint:X}");
        Add("Program headers", info.ProgramHeaderCount.ToString());
        Add("Section headers", info.SectionHeaderCount.ToString());
        if (info.IsSelf)
        {
            Add("SELF version", info.SelfVersion.ToString());
            Add("Program type", $"0x{info.SelfProgramType:X8}");
            Add("Header size", FormatBytes(info.SelfHeaderSize));
            Add("Metadata size", FormatBytes(info.SelfMetadataSize));
            Add("Declared file size", FormatBytes((long)Math.Min(info.SelfDeclaredFileSize, long.MaxValue)));
            Add("Segment count", info.SelfSegmentCount.ToString());
            Add("Flags", $"0x{info.SelfFlags:X4}");
        }
        gridSelfHeader.SetRows(headerRows);

        gridSelfSegments.SetRows(info.SelfSegments.Select(segment => new TableRow
        {
            Cells =
            [
                segment.Index.ToString(), $"0x{segment.Flags:X}", $"0x{segment.FileOffset:X}",
                FormatBytes(segment.FileSize), FormatBytes(segment.MemorySize)
            ]
        }).ToList());
    }

    private Ps5SelfInfo? _executableInfo;

    private Task PopulateExecutableAsync()
    {
        if (_executableInfo is not { } info)
        {
            gridModules.Clear();
            return Task.CompletedTask;
        }
        string filter = searchExecutable.SearchText.Trim();
        gridModules.SetRows(info.Modules
            .Where(module => Matches(filter, module.Name, module.RelativePath, module.Kind))
            .Select(module => new TableRow
            {
                Cells = [module.Name, module.RelativePath, module.Kind, FormatBytes(module.Size)],
                Tag = module
            }).ToList());
        return Task.CompletedTask;
    }

    private static string MachineText(ushort machine) => machine switch
    {
        0x3E => "x86-64",
        0xB7 => "aarch64",
        0x03 => "x86",
        _ => $"machine 0x{machine:X4}"
    };

    private async Task ExtractExecutableAsync()
    {
        if (_selectedGame is not { } game) return;
        string? path = await PickSaveFileAsync("Extract eboot.bin", "eboot.bin",
            new Avalonia.Platform.Storage.FilePickerFileType("Binary file") { Patterns = ["*.*"] });
        if (path is null) return;
        try
        {
            await Task.Run(() => GameFileSystem.ExtractFileAsync(game, "eboot.bin", path));
            SetStatus($"Extracted {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException)
        {
            AppDialog.ShowError(ex.Message, "Extract executable");
        }
    }

    private async Task ShowExecutableHashAsync()
    {
        if (_selectedGame is not { } game) return;
        try
        {
            byte[] data = await Task.Run(() =>
                GameFileSystem.ReadFileChunk(game, "eboot.bin", 0, 512 * 1024 * 1024).Data);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
            await AppDialog.ShowInfoAsync($"SHA-256 of eboot.bin ({FormatBytes(data.Length)}):\n\n{hash}",
                "Executable SHA-256");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException)
        {
            AppDialog.ShowError(ex.Message, "Executable SHA-256");
        }
    }

    private void CopyActiveExecutable(bool copyAll)
    {
        TableView grid = ReferenceEquals(tabsExecutable.SelectedItem, tabExecElf) ? gridElfSections
            : ReferenceEquals(tabsExecutable.SelectedItem, tabExecSelf) ? gridSelfHeader
            : gridModules;
        IReadOnlyList<TableRow> rows = copyAll ? grid.Rows : grid.SelectedRows;
        var builder = new StringBuilder();
        foreach (TableRow row in rows)
        {
            if (row.IsGroup) continue;
            for (int index = 0; index < row.Cells.Length && index < grid.Columns.Count; index++)
                builder.Append(grid.Columns[index].Header).Append(": ").Append(row.Cells[index]).AppendLine();
            builder.AppendLine();
        }
        if (builder.Length == 0)
        {
            AppDialog.ShowInfo("Nothing to copy.", "Executable");
            return;
        }
        CopyText(builder.ToString().TrimEnd());
    }

    // --------------------------------------------------------------- Container

    private async Task PopulatePackageAsync(Ps5GameInfo game, Ps5GameDetails? details)
    {
        UpdateContainerTabsForSource(game);
        if (!SourceExists(game))
        {
            gridPkgHeader.Clear();
            gridPkgSegments.Clear();
            gridPkgEntries.Clear();
            gridParamSfo.Clear();
            gridKeystone.Clear();
            gridSi.Clear();
            gridPlayGoChunks.Clear();
            gridPlayGoScenarios.Clear();
            gridPlayGoFiles.Clear();
            lblPlayGoSummary.Text = "The selected source was not found.";
            Logger.Warn($"Package tab skipped: source not found '{game.RootPath}'.");
            return;
        }

        bool Current() => ReferenceEquals(_selectedGame, game);
        SonyPkgSummary? package = game.Package;

        var headerRows = new List<TableRow>();
        void Add(string property, string value)
        {
            if (!string.IsNullOrEmpty(value)) headerRows.Add(new TableRow { Cells = [property, value] });
        }
        Add("Kind", package?.KindDisplayName ?? "PS5 source");
        Add("Location", game.RootPath);
        if (package is not null)
        {
            Add("File size", FormatBytes(package.FileSize));
            Add("Signed byte", package.SignedByte.HasValue ? $"0x{package.SignedByte:X2}" : string.Empty);
            Add("Format version", package.FormatVersion?.ToString() ?? string.Empty);
            Add("PFS offset", $"0x{package.PfsImageOffset:X}");
            Add("PFS size", FormatBytes((long)package.PfsImageSize));
            Add("PFS superblock offset", $"0x{package.PfsSuperblockOffset:X}");
            Add("Embedded CNT offset", $"0x{package.EmbeddedCntOffset:X}");
            Add("Header flags", $"0x{package.HeaderFlags:X8}");
            Add("System entry count", package.SystemEntryCount.ToString());
            Add("Body offset", $"0x{package.BodyOffset:X}");
            Add("Body size", FormatBytes((long)package.BodySize));
            Add("Content ID", package.ContentId);
            Add("DRM type", $"0x{package.DrmType:X8}");
            Add("Content type", package.ContentType == 0x20 ? "0x00000020  GD (game data)" : $"0x{package.ContentType:X8}");
            Add("Content flags", $"0x{package.ContentFlags:X8}");
            Add("CNT entries", package.Entries.Count.ToString("N0"));
            Add("Encrypted entries", package.EncryptedEntryCount.ToString("N0"));
            if (package.NestedPfs is { } pfs)
            {
                Add("PFS access", pfs.AccessState.ToString());
                if (pfs.BlockSize > 0) Add("PFS block size", FormatBytes(pfs.BlockSize));
                if (pfs.InodeCount > 0) Add("PFS inodes", pfs.InodeCount.ToString("N0"));
                Add("PFS status", pfs.StatusMessage);
            }
        }
        gridPkgHeader.SetRows(headerRows);

        gridPkgSegments.SetRows((package?.Segments ?? []).Select(segment => new TableRow
        {
            Cells =
            [
                SegmentName(segment.Name), $"0x{segment.Offset:X}", FormatBytes(segment.Size),
                segment.Size.ToString(), SegmentNote(segment.Name)
            ]
        }).ToList());

        gridPkgEntries.SetRows((package?.Entries ?? []).OrderBy(item => item.Id).Select(entry => new TableRow
        {
            Cells =
            [
                $"0x{entry.Id:X4}", entry.DisplayName, $"0x{entry.DataOffset:X}", FormatBytes(entry.DataSize),
                FormatBytes(entry.StoredSize), entry.IsEncrypted ? "Yes" : "No", entry.KeyIndex.ToString()
            ]
        }).ToList());

        byte[] sfoBytes = await ReadParamSfoAsync(game, package);
        if (!Current()) return;
        gridParamSfo.SetRows(Ps5SfoReader.Read(sfoBytes).Select(entry => new TableRow
        {
            Cells = [entry.Key, entry.Format, entry.Value]
        }).ToList());

        var keystone = new List<TableRow>();
        await AddExtraFileRowAsync(game, keystone, "sce_sys/keystone");
        await AddExtraFileRowAsync(game, keystone, "sce_sys/nptitle.dat");
        await AddExtraFileRowAsync(game, keystone, "sce_sys/about/right.sprx");
        if (!Current()) return;
        gridKeystone.SetRows(keystone);

        SonyPkgSegment? siSegment = package?.Segments.FirstOrDefault(segment =>
            segment.Name.Equals("SI", StringComparison.OrdinalIgnoreCase));
        var siRows = new List<TableRow>();
        if (siSegment is not null)
            siRows.AddRange(Ps5SiReader.List(game.RootPath, siSegment.Offset, siSegment.Size)
                .Select(member => new TableRow
                {
                    Cells = [member.Name, FormatBytes(member.Size), member.Size.ToString()]
                }));
        gridSi.SetRows(siRows);

        await PopulatePlayGoAsync(game, details, siSegment);
    }

    private void UpdateContainerTabsForSource(Ps5GameInfo game)
    {
        bool package = game.SourceKind == Ps5SourceKind.SonyPackage;
        SetContainerPage(tabPkgSegments, package);
        SetContainerPage(tabPkgEntries, package);
        SetContainerPage(tabPkgSi, package);
    }

    private void SetContainerPage(TabItem page, bool present)
    {
        bool attached = tabsPackage.Items.Contains(page);
        if (present && !attached) tabsPackage.Items.Add(page);
        else if (!present && attached) tabsPackage.Items.Remove(page);
    }

    private async Task PopulatePlayGoAsync(Ps5GameInfo game, Ps5GameDetails? details, SonyPkgSegment? siSegment)
    {
        byte[]? plgx = null;
        if (siSegment is not null)
            plgx = await Task.Run(() => Ps5SiReader.ReadMember(
                game.RootPath, siSegment.Offset, siSegment.Size, "playgo-chunk.dat"));
        if (plgx is null || plgx.Length == 0)
        {
            byte[] fromFiles = await ReadGameFileAsync(game, "sce_sys/playgo-chunk.dat");
            if (fromFiles.Length > 0) plgx = fromFiles;
        }
        bool hasPlgx = plgx is { Length: > 0 };
        byte[] hashTable = await ReadGameFileAsync(game, "sce_sys/playgo-hash-table.dat");
        byte[] ficm = await ReadGameFileAsync(game, "sce_sys/playgo-ficm.dat");
        string[] relativePaths = (details?.Files.Files ?? []).Select(file => file.RelativePath).ToArray();
        Ps5PlayGoSummary playGo = await Task.Run(() =>
        {
            IReadOnlyDictionary<ulong, string> pathMap = Ps5PlayGoReader.BuildPathMap(relativePaths);
            return Ps5PlayGoReader.Read(plgx, hashTable, ficm, pathMap);
        });

        gridPlayGoChunks.SetRows(playGo.Chunks.Select(chunk => new TableRow
        {
            Cells =
            [
                chunk.Id.ToString(), chunk.Label, chunk.ExtentCount.ToString(), $"0x{chunk.LanguageMask:X16}",
                FormatBytes(chunk.TotalBytes), chunk.TotalBytes.ToString()
            ]
        }).ToList());
        gridPlayGoScenarios.SetRows(playGo.Scenarios.Select(scenario => new TableRow
        {
            Cells =
            [
                scenario.Id.ToString(), scenario.Label, scenario.InitialChunkCount.ToString(),
                scenario.Chunks.Count.ToString(), string.Join(" ", scenario.Chunks)
            ]
        }).ToList());
        gridPlayGoFiles.SetRows(playGo.Files.Select(file => new TableRow
        {
            Cells = [file.Path, file.ChunkId.ToString(), $"0x{file.PathHash:X16}"]
        }).ToList());

        int resolved = playGo.Files.Count(file => !string.IsNullOrEmpty(file.Path));
        if (playGo.Chunks.Count == 0 && playGo.Files.Count == 0)
        {
            lblPlayGoSummary.Text = string.IsNullOrEmpty(playGo.Notice)
                ? "No PlayGo metadata was found for this source."
                : "No PlayGo metadata was found for this source.   Note: " + playGo.Notice;
            return;
        }

        var summary = new StringBuilder();
        if (hasPlgx)
        {
            summary.Append($"PlayGo v{playGo.VersionMajor}.{playGo.VersionMinor}   ");
            summary.Append($"Header flags: 0x{playGo.HeaderFlags:X8}   ");
        }
        else
        {
            summary.Append("PLGX header: not available (file mapping only)   ");
        }
        summary.Append($"Chunks: {playGo.Chunks.Count:N0}   Scenarios: {playGo.Scenarios.Count:N0}   ");
        summary.Append($"Default scenario: {playGo.DefaultScenarioId}   Files mapped: {resolved:N0} / {playGo.Files.Count:N0}");
        if (!string.IsNullOrEmpty(playGo.ContentId)) summary.Append($"   Content ID: {playGo.ContentId}");
        if (!string.IsNullOrEmpty(playGo.Notice)) summary.Append($"   Note: {playGo.Notice}");
        lblPlayGoSummary.Text = summary.ToString();
    }

    private static string SegmentName(string name) => name switch
    {
        "SC" => "SC (embedded CNT)",
        "CNT" => "CNT (metadata container)",
        "LIH" => "LIH (patch layer)",
        _ => name
    };

    private static string SegmentNote(string name) => name switch
    {
        "SC" or "CNT" => "Block-aligned (64 KiB); not the logical CNT length",
        "LIH" => "Patch layer header",
        _ => string.Empty
    };

    private async Task<byte[]> ReadParamSfoAsync(Ps5GameInfo game, SonyPkgSummary? package)
    {
        const uint ParamSfoEntryId = 0x1000;
        const int MaximumSfoBytes = 1 << 20;
        SonyPkgEntry? entry = package?.Entries.FirstOrDefault(candidate => candidate.Id == ParamSfoEntryId);
        if (entry is not null && !entry.IsEncrypted && entry.DataSize > 0 && entry.DataSize <= MaximumSfoBytes)
        {
            try
            {
                return new SonyPkgReader().ReadEntryBytes(game.RootPath, package!, entry, MaximumSfoBytes);
            }
            catch (InvalidDataException) { }
        }
        return await ReadGameFileAsync(game, "sce_sys/param.sfo");
    }

    private static async Task<byte[]> ReadGameFileAsync(Ps5GameInfo game, string path)
    {
        if (!SourceExists(game)) return [];
        try
        {
            GameFileChunk chunk = await Task.Run(() =>
                GameFileSystem.ReadFileChunk(game, path, 0, 4 * 1024 * 1024));
            return chunk.Data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException or ArgumentException)
        {
            return [];
        }
    }

    private static async Task AddExtraFileRowAsync(Ps5GameInfo game, List<TableRow> table, string path)
    {
        if (!SourceExists(game))
        {
            table.Add(new TableRow { Cells = [path, "source not found", string.Empty, string.Empty] });
            return;
        }
        try
        {
            GameFileChunk chunk = await Task.Run(() => GameFileSystem.ReadFileChunk(game, path, 0, 16));
            table.Add(new TableRow
            {
                Cells = [path, "present", FormatBytes(chunk.FileSize), Convert.ToHexString(chunk.Data)]
            });
        }
        catch (FileNotFoundException)
        {
            table.Add(new TableRow { Cells = [path, "not present", string.Empty, string.Empty] });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   NotSupportedException or ArgumentException)
        {
            table.Add(new TableRow { Cells = [path, "read failed: " + ex.Message, string.Empty, string.Empty] });
        }
    }
}
