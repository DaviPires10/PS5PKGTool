using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using PS5PKGTool.Controls;
using PS5PKGTool.Core.Models;
using PS5PKGTool.Core.Services;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

public partial class MainWindow
{
    private static readonly (string Name, string Header, int Width, double Weight)[] LibraryColumnDefinitions =
    [
        ("Icon", "", 34, 0),
        ("Title", "Title", 240, 26),
        ("TitleId", "Title ID", 110, 12),
        ("ContentId", "Content ID", 190, 20),
        ("Category", "Category", 80, 9),
        ("Role", "Role", 90, 10),
        ("Region", "Region", 80, 9),
        ("Source", "Source", 120, 13),
        ("Size", "Size", 80, 9),
        ("Version", "Version", 80, 9),
        ("Firmware", "Required Firmware", 110, 12),
        ("Features", "Features", 140, 14),
        ("Drm", "DRM", 90, 10),
        ("FileName", "File name", 200, 18),
        ("Location", "Location", 240, 26)
    ];

    private readonly List<(string Column, bool Ascending)> _librarySortKeys = [];
    private string PrimarySortColumn => _librarySortKeys.Count > 0 ? _librarySortKeys[0].Column : "Title";
    private bool PrimarySortAscending => _librarySortKeys.Count > 0 && _librarySortKeys[0].Ascending;
    private bool _suppressLibrarySelection;
    private MenuItem? _libraryColumnMenu;
    private readonly List<(MenuItem Item, string Name)> _contextColumnItems = [];
    private readonly List<(MenuItem Item, string Name)> _viewColumnItems = [];
    private readonly ConcurrentDictionary<string, Bitmap> _libraryThumbnails = new(PathIdentity.Comparer);
    private readonly ConcurrentDictionary<string, byte> _libraryThumbnailAttempts = new(PathIdentity.Comparer);
    private readonly Dictionary<string, TableRow> _libraryRowsByRoot = new(PathIdentity.Comparer);
    private int _libraryThumbnailVersion;

    private void EnsureLibraryColumns()
    {
        if (_libraryColumnsReady) return;
        _libraryColumnsReady = true;

        _settings.LibraryColumnOrder ??= [];
        _settings.LibraryHiddenColumns ??= [];
        _settings.LibraryColumnWidths ??= [];
        _settings.LibraryColumnWeights ??= [];

        RebuildLibraryColumns();
        gridLibrary.ColumnHeaderClicked += GridLibrary_ColumnHeaderClicked;
        gridLibrary.ColumnResized += GridLibrary_ColumnResized;
        gridLibrary.ScrollChanged += (_, _) => LoadLibraryThumbnailBatch();
        BuildLibraryColumnMenu();
        LoadLibrarySortKeys();
    }

    /// <summary>Rebuilds the table columns from the saved order, visibility and weights.</summary>
    private void RebuildLibraryColumns()
    {
        var definitions = LibraryColumnDefinitions.ToList();
        var order = new List<string>();
        foreach (string name in _settings.LibraryColumnOrder)
            if (definitions.Any(definition => definition.Name == name) &&
                !order.Contains(name, StringComparer.Ordinal))
                order.Add(name);
        // Insert any name missing from the saved order at its declared position. Appending it (the
        // old behaviour) moved the Icon and previously hidden columns to the end, which shifted every
        // cell under the wrong header because rows are positional.
        for (int index = 0; index < definitions.Count; index++)
        {
            string name = definitions[index].Name;
            if (!order.Contains(name, StringComparer.Ordinal))
                order.Insert(Math.Min(index, order.Count), name);
        }

        var columns = new List<TableColumn>();
        foreach (string name in order)
        {
            var definition = definitions.First(entry => entry.Name == name);
            bool visible = name == "Icon" ||
                !_settings.LibraryHiddenColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
            columns.Add(new TableColumn
            {
                Name = name,
                Header = definition.Header,
                FixedWidth = name == "Icon" ? definition.Width : 0,
                Width = name == "Icon" ? 0 : ResolveLibraryColumnWidth(name, definition.Width),
                Weight = definition.Weight <= 0 ? 1 : definition.Weight,
                Visible = visible || name == "Icon",
                RightAligned = name == "Size",
                IsIcon = name == "Icon"
            });
        }
        gridLibrary.SetColumns(columns);
    }

    /// <summary>Restores the multi-sort keys, falling back to the single legacy sort setting.</summary>
    private void LoadLibrarySortKeys()
    {
        _librarySortKeys.Clear();
        foreach (string entry in _settings.LibrarySortKeys ?? [])
        {
            string[] parts = entry.Split(':');
            if (parts.Length != 2 || gridLibrary.Column(parts[0]) is null) continue;
            if (_librarySortKeys.Any(key => string.Equals(key.Column, parts[0], StringComparison.Ordinal))) continue;
            _librarySortKeys.Add((parts[0], !string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase)));
        }
        if (_librarySortKeys.Count == 0)
        {
            string column = string.IsNullOrWhiteSpace(_settings.LibrarySortColumn) ? "Title" : _settings.LibrarySortColumn;
            if (gridLibrary.Column(column) is null) column = "Title";
            _librarySortKeys.Add((column, _settings.LibrarySortAscending));
        }
    }

    private void PersistLibrarySortKeys()
    {
        _settings.LibrarySortKeys = _librarySortKeys
            .Select(key => $"{key.Column}:{(key.Ascending ? "asc" : "desc")}")
            .ToList();
        _settings.LibrarySortColumn = PrimarySortColumn;
        _settings.LibrarySortAscending = PrimarySortAscending;
    }

    private void PopulateLibraryGrid()
    {
        EnsureLibraryColumns();

        // Capture the whole selection and the focused row by stable root path, not just one.
        string[] selectedRoots = SelectedGames().Select(game => game.RootPath).ToArray();
        string? focusedRoot = SelectedGame()?.RootPath;

        _suppressLibrarySelection = true;
        _libraryRowsByRoot.Clear();
        try
        {
            if (_libraryGroupBy.Length > 0)
            {
                IEnumerable<Ps5GameInfo> grouped = string.Equals(_libraryGroupBy, "family", StringComparison.Ordinal)
                    ? OrderLibraryGamesByFamily()
                    : OrderLibraryGames();
                gridLibrary.SetGroups(grouped, game => GroupKey(game), FillLibraryRow);
            }
            else
            {
                gridLibrary.SetRows(OrderLibraryGames().Select(FillLibraryRow));
            }
        }
        finally
        {
            _suppressLibrarySelection = false;
        }

        RestoreLibrarySelection(selectedRoots, focusedRoot);

        // On the first load there is no previous selection. Promote the first row to a real selection
        // so the details pane and the Tools source load immediately.
        if (selectedRoots.Length == 0 && _visibleGames.Count > 0)
        {
            TableRow? first = gridLibrary.Rows.FirstOrDefault(row => !row.IsGroup && row.Tag is Ps5GameInfo);
            if (first is not null) gridLibrary.SelectRows([first]);
        }

        StartLibraryThumbnailLoad(_visibleGames);
    }

    /// <summary>
    /// Restores the full multi-selection and the focused row by stable root path after a rebuild.
    /// Identities that are no longer visible are dropped.
    /// </summary>
    private void RestoreLibrarySelection(string[] roots, string? focusedRoot)
    {
        if (roots.Length == 0 && focusedRoot is null) return;
        var wanted = new HashSet<string>(roots, PathIdentity.Comparer);
        _suppressLibrarySelection = true;
        try
        {
            var rows = new List<TableRow>();
            TableRow? focused = null;
            foreach (TableRow row in gridLibrary.Rows)
            {
                if (row.Tag is not Ps5GameInfo game) continue;
                if (wanted.Contains(game.RootPath)) rows.Add(row);
                if (focusedRoot is not null && PathIdentity.Equals(game.RootPath, focusedRoot))
                    focused = row;
            }
            gridLibrary.SelectRows(rows);
            if (focused is not null) gridLibrary.ScrollToRow(focused);
        }
        finally
        {
            _suppressLibrarySelection = false;
        }
    }

    private IReadOnlyList<Ps5GameInfo> OrderLibraryGames()
    {
        Comparison<Ps5GameInfo> comparison = LibrarySortComparison();
        var list = new List<Ps5GameInfo>(_visibleGames);
        list.Sort((a, b) =>
        {
            int result = comparison(a, b);
            return result != 0 ? result : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        });
        return list;
    }

    /// <summary>
    /// Family order: grouped by title, then base game, updates (highest version first), DLC and apps, so
    /// a family reads top-down as the install chain.
    /// </summary>
    private IReadOnlyList<Ps5GameInfo> OrderLibraryGamesByFamily()
    {
        var list = new List<Ps5GameInfo>(_visibleGames);
        list.Sort((a, b) =>
        {
            int byFamily = string.Compare(a.TitleId, b.TitleId, StringComparison.OrdinalIgnoreCase);
            if (byFamily != 0) return byFamily;
            int byRole = RolePriority(a).CompareTo(RolePriority(b));
            if (byRole != 0) return byRole;
            return VersionKey.Parse(b.DisplayVersion).CompareTo(VersionKey.Parse(a.DisplayVersion));
        });
        return list;
    }

    private Comparison<Ps5GameInfo> LibrarySortComparison()
    {
        IReadOnlyList<(string Column, bool Ascending)> keys =
            _librarySortKeys.Count > 0 ? _librarySortKeys : [("Title", true)];
        if (keys.Count == 1) return LibraryKeyComparison(keys[0]);

        // Multi-sort: compare by each key in turn; the first non-equal key decides.
        var comparisons = keys.Select(LibraryKeyComparison).ToList();
        return (a, b) =>
        {
            foreach (Comparison<Ps5GameInfo> comparison in comparisons)
            {
                int result = comparison(a, b);
                if (result != 0) return result;
            }
            return 0;
        };
    }

    private Comparison<Ps5GameInfo> LibraryKeyComparison((string Column, bool Ascending) key)
    {
        Func<Ps5GameInfo, object?> selector = key.Column switch
        {
            "TitleId" => game => game.TitleId,
            "ContentId" => game => game.ContentId,
            "Category" => game => CategoryOf(game),
            "Role" => game => RelationshipLabel(game),
            "Region" => game => RegionOf(game),
            "Source" => game => game.SourceDescription,
            "Size" => game => game.SourceSize,
            // Typed keys so 1.10 sorts after 1.9 instead of lexically.
            "Version" => game => VersionKey.Parse(game.DisplayVersion),
            "Firmware" => game => VersionKey.Parse(game.RequiredSystemSoftware),
            "Features" => game => string.Join(", ", game.DeclaredFeatures),
            "Drm" => game => game.DrmType,
            "FileName" => game => LibraryFileName(game),
            "Location" => game => game.RootPath,
            _ => game => game.Title
        };
        int sign = key.Ascending ? 1 : -1;
        return (a, b) =>
        {
            object? left = selector(a);
            object? right = selector(b);
            int result = left is long lx && right is long ly
                ? lx.CompareTo(ly)
                : left is VersionKey vx && right is VersionKey vy
                    ? vx.CompareTo(vy)
                    : string.Compare(left?.ToString(), right?.ToString(), StringComparison.CurrentCultureIgnoreCase);
            return result * sign;
        };
    }

    /// <summary>
    /// Orders dotted numeric version/system strings by value (1.10 after 1.9) with a text tie-break.
    /// Non-numeric or unknown values stay comparable and sort before/after per their text.
    /// </summary>
    private readonly record struct VersionKey(long Packed, string Raw) : IComparable<VersionKey>
    {
        public static VersionKey Parse(string? value)
        {
            string raw = value ?? string.Empty;
            long packed = 0;
            long current = 0;
            bool any = false;
            int groups = 0;
            foreach (char c in raw)
            {
                if (char.IsDigit(c))
                {
                    current = Math.Min(current * 10 + (c - '0'), 9_999);
                    any = true;
                }
                else if (c == '.')
                {
                    packed = packed * 10_000 + current;
                    current = 0;
                    any = false;
                    if (++groups >= 3) break;
                }
            }
            if (any) packed = packed * 10_000 + current;
            return new VersionKey(packed, raw);
        }

        public int CompareTo(VersionKey other)
        {
            int byValue = Packed.CompareTo(other.Packed);
            return byValue != 0 ? byValue : string.Compare(Raw, other.Raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    private TableRow FillLibraryRow(Ps5GameInfo game)
    {
        bool exists = SourceExists(game);
        var row = new TableRow
        {
            Cells =
            [
                string.Empty,
                game.Title,
                game.TitleId,
                game.ContentId,
                CategoryOf(game),
                RelationshipLabel(game),
                RegionOf(game),
                exists ? game.SourceDescription : "Missing",
                game.SourceSize > 0 ? FormatBytes(game.SourceSize) : string.Empty,
                game.DisplayVersion,
                game.RequiredSystemSoftware,
                string.Join(", ", game.DeclaredFeatures),
                game.DrmType,
                LibraryFileName(game),
                game.RootPath
            ],
            Tag = game,
            Icon = LibraryIcon(game),
            Error = !exists
        };
        _libraryRowsByRoot[game.RootPath] = row;
        return row;
    }

    private void RefreshLibrarySizeCell(Ps5GameInfo game)
    {
        if (!_libraryRowsByRoot.TryGetValue(game.RootPath, out TableRow? row)) return;
        int index = IndexOfLibraryColumn("Size");
        if (index < 0 || index >= row.Cells.Length) return;
        row.Cells[index] = game.SourceSize > 0 ? FormatBytes(game.SourceSize) : string.Empty;
        gridLibrary.RefreshRows();
    }

    private static int IndexOfLibraryColumn(string name) =>
        Array.FindIndex(LibraryColumnDefinitions, definition => definition.Name == name);

    private void GridLibrary_ColumnHeaderClicked(object? sender, TableColumnEventArgs e)
    {
        string column = e.Column.Name;
        if (column == "Icon") return;

        if (e.Shift)
        {
            // Shift+click adds or toggles a secondary sort key.
            int index = _librarySortKeys.FindIndex(key => string.Equals(key.Column, column, StringComparison.Ordinal));
            if (index >= 0) _librarySortKeys[index] = (column, !_librarySortKeys[index].Ascending);
            else _librarySortKeys.Add((column, true));
        }
        else
        {
            // A plain click sorts by this column alone, toggling direction when it was already primary.
            bool wasPrimary = _librarySortKeys.Count > 0 &&
                string.Equals(_librarySortKeys[0].Column, column, StringComparison.Ordinal);
            bool ascending = wasPrimary ? !_librarySortKeys[0].Ascending : true;
            _librarySortKeys.Clear();
            _librarySortKeys.Add((column, ascending));
        }

        PersistLibrarySortKeys();
        SaveSettingsQuietly();
        ApplyFilter();
    }

    private void GridLibrary_ColumnResized(object? sender, TableColumnEventArgs e)
    {
        if (!_libraryColumnsReady) return;
        if (e.Column.Name == "Icon" || !e.Column.Visible) return;
        _settings.LibraryColumnWidths ??= [];
        _settings.LibraryColumnWidths[e.Column.Name] = (float)e.Column.Width;
        SaveSettingsQuietly();
    }

    private static string LibraryFileName(Ps5GameInfo game) =>
        Path.GetFileName(game.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private IEnumerable<Ps5GameInfo> SelectedGames()
    {
        foreach (TableRow row in gridLibrary.SelectedRows)
            if (row.Tag is Ps5GameInfo game) yield return game;
    }

    private Ps5GameInfo? SelectedGame()
    {
        // Prefer the focused row so single-row actions target what the user is looking at, not an
        // arbitrary member of a multi-selection.
        if (gridLibrary.CurrentRow is { Tag: Ps5GameInfo focused }) return focused;
        foreach (TableRow row in gridLibrary.SelectedRows)
            if (row.Tag is Ps5GameInfo game) return game;
        return null;
    }

    private Bitmap? LibraryIcon(Ps5GameInfo game)
    {
        if (_settings.ShowThumbnails &&
            _libraryThumbnails.TryGetValue(game.RootPath, out Bitmap? thumbnail) && thumbnail is not null)
            return thumbnail;
        return SourceIcon(game.SourceKind);
    }

    private Bitmap? SourceIcon(Ps5SourceKind kind)
    {
        // Image containers (FFPFSC, exFAT, FFPKG) use the container icon. Debug packages keep the
        // package icon, and loose dumps keep the folder icon.
        string key = kind switch
        {
            Ps5SourceKind.LooseDump => "folder",
            Ps5SourceKind.SonyPackage => "package",
            _ => "container"
        };
        return ThemeManager.LoadBitmap($"avares://PS5PKGTool/Resources/{key}.png")
            ?? ThemeManager.LoadBitmap("avares://PS5PKGTool/Resources/file-unknown.png");
    }

    private readonly List<Ps5GameInfo> _thumbnailQueue = [];
    private int _thumbnailQueueIndex;
    private int _thumbnailLoadBusy;
    private const int ThumbnailBatchSize = 96;

    private void StartLibraryThumbnailLoad(IReadOnlyList<Ps5GameInfo> ordered)
    {
        if (!_settings.ShowThumbnails) return;
        if (_settings.ThumbnailCacheCount > 0 && _libraryThumbnails.Count >= _settings.ThumbnailCacheCount)
            ClearLibraryThumbnails();

        _libraryThumbnailVersion++;
        lock (_thumbnailQueue)
        {
            _thumbnailQueue.Clear();
            _thumbnailQueueIndex = 0;
            var seen = new HashSet<string>(PathIdentity.Comparer);
            foreach (Ps5GameInfo game in ordered)
            {
                if (_libraryThumbnails.ContainsKey(game.RootPath)) continue;
                if (_libraryThumbnailAttempts.ContainsKey(game.RootPath)) continue;
                if (!seen.Add(game.RootPath)) continue;
                _thumbnailQueue.Add(game);
            }
        }
        LoadLibraryThumbnailBatch();
    }

    /// <summary>
    /// Loads the next bounded batch of thumbnails (visible-first, then on scroll). Attempts are recorded
    /// per item as it starts, so a cancelled batch leaves the remaining items retryable.
    /// </summary>
    private void LoadLibraryThumbnailBatch()
    {
        if (!_settings.ShowThumbnails) return;
        if (Interlocked.CompareExchange(ref _thumbnailLoadBusy, 1, 0) != 0) return;

        Ps5GameInfo[] batch;
        lock (_thumbnailQueue)
        {
            if (_thumbnailQueueIndex >= _thumbnailQueue.Count)
            {
                Interlocked.Exchange(ref _thumbnailLoadBusy, 0);
                return;
            }
            int start = _thumbnailQueueIndex;
            int end = Math.Min(_thumbnailQueue.Count, start + ThumbnailBatchSize);
            _thumbnailQueueIndex = end;
            batch = _thumbnailQueue.GetRange(start, end - start).ToArray();
        }

        int version = _libraryThumbnailVersion;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (Ps5GameInfo game in batch)
                {
                    if (version != _libraryThumbnailVersion) return;
                    if (!_libraryThumbnailAttempts.TryAdd(game.RootPath, 0)) continue;
                    Bitmap? thumbnail = LoadLibraryThumbnail(game);
                    if (thumbnail is null) continue;
                    // A newer rebuild may have started while this image decoded; drop the stale result.
                    if (version != _libraryThumbnailVersion) { thumbnail.Dispose(); return; }
                    _libraryThumbnails[game.RootPath] = thumbnail;
                    RunOnUi(() => ApplyLibraryThumbnail(game, thumbnail));
                }
            }
            finally
            {
                Interlocked.Exchange(ref _thumbnailLoadBusy, 0);
                // A newer rebuild may have queued while this batch was busy; start its first batch.
                if (version != _libraryThumbnailVersion && _thumbnailQueueIndex < _thumbnailQueue.Count)
                    RunOnUi(LoadLibraryThumbnailBatch);
            }
        });
    }

    /// <summary>
    /// Drops every cached thumbnail. Images still shown by a row are left for the GC rather than
    /// disposed, so a rebuild can never paint a disposed image; the rest are released immediately.
    /// </summary>
    private void ClearLibraryThumbnails()
    {
        var referenced = new HashSet<Bitmap>();
        foreach (TableRow row in gridLibrary.Rows)
            if (row.Icon is Bitmap image) referenced.Add(image);
        foreach (Bitmap image in _libraryThumbnails.Values)
            if (image is not null && !referenced.Contains(image)) image.Dispose();
        _libraryThumbnails.Clear();
        _libraryThumbnailAttempts.Clear();
        lock (_thumbnailQueue)
        {
            _thumbnailQueue.Clear();
            _thumbnailQueueIndex = 0;
        }
    }

    /// <summary>Drops the cached thumbnail for a single (moved or removed) source.</summary>
    private void ForgetLibraryThumbnail(string rootPath)
    {
        _libraryThumbnails.TryRemove(rootPath, out _);
        _libraryThumbnailAttempts.TryRemove(rootPath, out _);
    }

    private void ApplyLibraryThumbnail(Ps5GameInfo game, Bitmap thumbnail)
    {
        if (!_libraryRowsByRoot.TryGetValue(game.RootPath, out TableRow? row)) return;
        row.Icon = thumbnail;
        gridLibrary.RefreshRows();
    }

    private static Bitmap? LoadLibraryThumbnail(Ps5GameInfo game)
    {
        try
        {
            byte[] png = GameFileSystem.ReadFileChunk(game, "sce_sys/icon0.png", 0, 4 * 1024 * 1024).Data;
            if (png.Length == 0) return null;
            using var stream = new MemoryStream(png, writable: false);
            using var source = new Bitmap(stream);
            return source.CreateScaledBitmap(new Avalonia.PixelSize(30, 30),
                BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidDataException or NotSupportedException or OutOfMemoryException)
        {
            return null;
        }
    }

    private void ApplyLibraryColumnVisibility()
    {
        RebuildLibraryColumns();
    }

    private void BuildLibraryColumnMenu()
    {
        if (_libraryColumnMenu is not null) return;
        _libraryColumnMenu = new MenuItem { Header = "Columns" };
        foreach ((string name, string header, _, _) in LibraryColumnDefinitions)
        {
            if (name == "Icon") continue;
            string captured = name;
            var item = new MenuItem
            {
                Header = header,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = !_settings.LibraryHiddenColumns.Contains(captured, StringComparer.OrdinalIgnoreCase)
            };
            item.Click += (_, _) => SetLibraryColumnVisible(captured, !IsLibraryColumnVisible(captured));
            _contextColumnItems.Add((item, captured));
            _libraryColumnMenu.Items.Add(item);
        }
        var resetView = new MenuItem { Header = "Reset view" };
        resetView.Click += (_, _) => ResetLibraryView();
        contextLibrary.Items.Add(new Separator());
        contextLibrary.Items.Add(resetView);
        contextLibrary.Items.Add(_libraryColumnMenu);
    }

    private bool IsLibraryColumnVisible(string name) =>
        !_settings.LibraryHiddenColumns.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Shows/hides a library column from either the View cascade or the context menu.</summary>
    private void SetLibraryColumnVisible(string name, bool visible)
    {
        if (visible)
            _settings.LibraryHiddenColumns.RemoveAll(entry =>
                string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));
        else if (!_settings.LibraryHiddenColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
            _settings.LibraryHiddenColumns.Add(name);
        ApplyLibraryColumnVisibility();
        SyncColumnMenuChecks();
        SaveSettingsQuietly();
    }

    /// <summary>Builds the View > Columns cascade and the View > Interface toggles.</summary>
    private void BuildViewMenu()
    {
        menuViewColumns.Items.Clear();
        _viewColumnItems.Clear();
        foreach ((string name, string header, _, _) in LibraryColumnDefinitions)
        {
            if (name == "Icon") continue;
            string captured = name;
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox };
            item.Click += (_, _) => SetLibraryColumnVisible(captured, !IsLibraryColumnVisible(captured));
            _viewColumnItems.Add((item, captured));
            menuViewColumns.Items.Add(item);
        }

        menuViewFilterBar.Click += (_, _) =>
        {
            _settings.ShowFilterBar = !_settings.ShowFilterBar;
            ApplyViewMenuState();
            SaveSettingsQuietly();
        };
        menuViewFilterChips.Click += (_, _) =>
        {
            _settings.ShowFilterChips = !_settings.ShowFilterChips;
            ApplyViewMenuState();
            SaveSettingsQuietly();
        };
        menuViewWorkspace.Click += (_, _) =>
        {
            _settings.ShowWorkspacePane = !_settings.ShowWorkspacePane;
            ApplyViewMenuState();
            SaveSettingsQuietly();
        };
        menuViewStatusBar.Click += (_, _) =>
        {
            _settings.ShowStatusBar = !_settings.ShowStatusBar;
            ApplyViewMenuState();
            SaveSettingsQuietly();
        };
        menuViewReset.Click += (_, _) => ResetLibraryView();

        SyncColumnMenuChecks();
        ApplyViewMenuState();
    }

    private void SyncColumnMenuChecks()
    {
        foreach ((MenuItem item, string name) in _contextColumnItems)
            item.IsChecked = IsLibraryColumnVisible(name);
        foreach ((MenuItem item, string name) in _viewColumnItems)
            item.IsChecked = IsLibraryColumnVisible(name);
    }

    private void ApplyViewMenuState()
    {
        menuViewFilterBar.IsChecked = _settings.ShowFilterBar;
        menuViewFilterChips.IsChecked = _settings.ShowFilterChips;
        menuViewWorkspace.IsChecked = _settings.ShowWorkspacePane;
        menuViewStatusBar.IsChecked = _settings.ShowStatusBar;
        ApplyPanelVisibility();
    }

    /// <summary>Applies the Interface submenu. Hiding the workspace also removes its splitter row so
    /// the library takes the full height.</summary>
    private void ApplyPanelVisibility()
    {
        panelFilterBar.IsVisible = _settings.ShowFilterBar;
        chipsFilter.IsVisible = _settings.ShowFilterChips;
        statusMain.IsVisible = _settings.ShowStatusBar;
        splitMainPane2.IsVisible = _settings.ShowWorkspacePane;
        splitMainSplitter.IsVisible = _settings.ShowWorkspacePane;
        splitMainRows.RowDefinitions = _settings.ShowWorkspacePane
            ? new RowDefinitions("*,5,*")
            : new RowDefinitions("*");
    }

    /// <summary>
    /// Restores the default layout - Title ascending sort, no grouping, all columns shown in their
    /// declared order - without touching the filter query or selections.
    /// </summary>
    private void ResetLibraryView()
    {
        _librarySortKeys.Clear();
        _librarySortKeys.Add(("Title", true));
        PersistLibrarySortKeys();

        _settings.LibraryHiddenColumns.Clear();
        _settings.LibraryColumnWeights.Clear();
        _settings.LibraryColumnWidths.Clear();
        _settings.LibraryColumnOrder = LibraryColumnDefinitions.Select(definition => definition.Name).ToList();
        RebuildLibraryColumns();

        SetGroupBy(string.Empty);
        ApplyFilter();
        SaveSettingsQuietly();
        SyncColumnMenuChecks();
        SetStatus("View reset to defaults.");
    }

    private void RestoreLibraryColumnLayout()
    {
        // The saved order is applied when the columns are rebuilt.
        RebuildLibraryColumns();
    }

    private void CaptureLibraryColumnLayout()
    {
        if (!_libraryColumnsReady) return;
        // Keep every column, including the icon and hidden ones, so the positional row cells keep
        // matching their headers after the order is restored on the next start.
        _settings.LibraryColumnOrder = gridLibrary.Columns
            .Select(column => column.Name)
            .ToList();
        CaptureLibraryColumnWidths();
    }

    /// <summary>Remembers the current pixel widths for the next session.</summary>
    private void CaptureLibraryColumnWidths()
    {
        if (!_libraryColumnsReady) return;
        _settings.LibraryColumnWidths ??= [];
        foreach (TableColumn column in gridLibrary.Columns)
        {
            if (column.Name == "Icon" || !column.Visible || column.Width <= 0) continue;
            _settings.LibraryColumnWidths[column.Name] = (float)column.Width;
        }
    }

    /// <summary>The saved pixel width for a column, falling back to the declared default.</summary>
    private double ResolveLibraryColumnWidth(string name, double fallback)
    {
        if (_settings.LibraryColumnWidths is { } widths &&
            widths.TryGetValue(name, out float saved) && saved > 0)
            return Math.Clamp(saved, 40, 4000);
        return Math.Max(40, fallback);
    }

    /// <summary>Re-applies the resolved pixel widths to every column (used after importing settings).</summary>
    private void ApplyLibraryColumnWidths()
    {
        foreach (TableColumn column in gridLibrary.Columns)
        {
            if (column.Name == "Icon") continue;
            double fallback = LibraryColumnDefinitions
                .FirstOrDefault(definition => string.Equals(definition.Name, column.Name, StringComparison.Ordinal)).Width;
            column.Width = ResolveLibraryColumnWidth(column.Name, fallback);
        }
        gridLibrary.RefreshRows();
    }
}
