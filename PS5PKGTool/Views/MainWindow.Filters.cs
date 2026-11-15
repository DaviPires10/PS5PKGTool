using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PS5PKGTool.Controls;
using PS5PKGTool.Core.Models;
using PS5PKGTool.Core.Services;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

public partial class MainWindow
{
    private bool _suppressFilterEvents;
    private string _libraryGroupBy = string.Empty;
    private readonly List<(MenuItem Item, string Key)> _groupItems = [];
    private MenuItem? _libraryViewsMenu;

    private static readonly string[] FilterGroupKeys =
        ["", "family", "titleid", "category", "region", "source", "firmware"];

    private void InitializeLibraryTools()
    {
        _groupItems.Add((menuLibraryGroupNone, string.Empty));
        _groupItems.Add((menuLibraryGroupFamily, "family"));
        _groupItems.Add((menuLibraryGroupTitleId, "titleid"));
        _groupItems.Add((menuLibraryGroupCategory, "category"));
        _groupItems.Add((menuLibraryGroupRegion, "region"));
        _groupItems.Add((menuLibraryGroupSource, "source"));
        _groupItems.Add((menuLibraryGroupFirmware, "firmware"));

        BuildRenamePresetMenu(menuLibraryRename, all: false);
        BuildRenamePresetMenu(menuLibraryRenameAll, all: true);
        BuildLibraryViewsMenu();
        BuildViewMenu();

        cboFilterCategory.ItemsSource = new[] { "Any", "Game", "Patch", "DLC", "App", "Unknown" };
        cboFilterRegion.ItemsSource = new[]
            { "Any", "Americas", "Europe", "Japan", "Korea", "Asia", "Hong Kong", "Other", "Unknown" };
        cboFilterFormat.ItemsSource = new[] { "Any", "Dump Files", "PKG", "FFPFSC", "exFAT", "FFPKG" };
        cboFilterCategory.SelectedIndex = 0;
        cboFilterRegion.SelectedIndex = 0;
        cboFilterFormat.SelectedIndex = 0;
        cboFilterGroup.ItemsSource = new[] { "None", "Family", "Title ID", "Category", "Region", "Format", "Firmware" };
        cboFilterGroup.SelectedIndex = 0;
        cboFilterPreset.ItemsSource = new[]
        {
            "Presets...", "All", "Games", "Patches", "DLC", "Dumps", "PKG", "FFPKG", "FFPFSC", "exFAT"
        };
        cboFilterPreset.SelectedIndex = 0;

        // Export offers an explicit scope instead of guessing from the current selection.
        menuLibraryExport.Items.Add(new MenuItem { Header = "Export selected..." });
        menuLibraryExport.Items.Add(new MenuItem { Header = "Export visible..." });
        menuLibraryExport.Items.Add(new MenuItem { Header = "Export all..." });
        ((MenuItem)menuLibraryExport.Items[0]!).Click += (_, _) =>
            _ = ExportGamesCsvAsync(SelectedGames().ToList(), "PS5-selected.csv");
        ((MenuItem)menuLibraryExport.Items[1]!).Click += (_, _) =>
            _ = ExportGamesCsvAsync([.. _visibleGames], "PS5-visible.csv");
        ((MenuItem)menuLibraryExport.Items[2]!).Click += (_, _) =>
            _ = ExportGamesCsvAsync([.. _games], "PS5-library.csv");

        cboFilterCategory.SelectionChanged += CboFilterValue_SelectionChanged;
        cboFilterRegion.SelectionChanged += CboFilterValue_SelectionChanged;
        cboFilterFormat.SelectionChanged += CboFilterValue_SelectionChanged;
        cboFilterGroup.SelectionChanged += CboFilterGroup_SelectionChanged;
        cboFilterPreset.SelectionChanged += CboFilterPreset_SelectionChanged;
        searchLibrary.TextChanged += SearchLibrary_TextChanged;
        btnFilterClear.Click += (_, _) => ClearFilters();

        menuLibraryReveal.Click += (_, _) => { if (SelectedGame() is { } game) RevealGame(game); };
        menuLibraryCopyTitle.Click += (_, _) => CopyText(SelectedGame()?.Title);
        menuLibraryCopyTitleId.Click += (_, _) => CopyText(SelectedGame()?.TitleId);
        menuLibraryCopyContentId.Click += (_, _) => CopyText(SelectedGame()?.ContentId);
        menuLibraryCopyFileName.Click += (_, _) =>
            CopyText(SelectedGame() is { } game ? LibraryFileName(game) : null);
        menuLibraryCopyPath.Click += (_, _) => CopyText(SelectedGame()?.RootPath);
        menuLibraryGroupNone.Click += (_, _) => SetGroupBy(string.Empty);
        menuLibraryGroupFamily.Click += (_, _) => SetGroupBy("family");
        menuLibraryGroupTitleId.Click += (_, _) => SetGroupBy("titleid");
        menuLibraryGroupCategory.Click += (_, _) => SetGroupBy("category");
        menuLibraryGroupRegion.Click += (_, _) => SetGroupBy("region");
        menuLibraryGroupSource.Click += (_, _) => SetGroupBy("source");
        menuLibraryGroupFirmware.Click += (_, _) => SetGroupBy("firmware");
        menuLibraryDuplicates.Click += (_, _) => _ = FindDuplicatesAsync();
        menuLibraryExport.Click += (_, _) => ExportLibraryCsv();

        WireLibraryActions();
        WireLibraryEvents();
    }

    private void WireLibraryEvents()
    {
        gridLibrary.SelectionChanged += GridLibrary_SelectionChanged;
        gridLibrary.RowDoubleClicked += GridLibrary_RowDoubleClicked;
        gridLibrary.RowContextRequested += (_, e) => UpdateLibraryContextMenu(e.Row);
        gridLibrary.ContextMenu?.Opening += (_, _) => UpdateLibraryContextMenu(gridLibrary.ContextRow);
    }

    private async void GridLibrary_SelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressLibrarySelection) return;
        if (SelectedGame() is not { } game) return;
        try
        {
            SetSelectedGame(game);
            RefreshImageTools();
            await ShowGameAsync(game);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Unable to open '{game.RootPath}': {ex.Message}");
        }
    }

    private void GridLibrary_RowDoubleClicked(object? sender, TableRowEventArgs e)
    {
        if (e.Row?.Tag is Ps5GameInfo game) RevealGame(game);
    }

    private void UpdateLibraryContextMenu(TableRow? row)
    {
        bool groupContext = row?.IsGroup == true;
        bool hasGame = row?.Tag is Ps5GameInfo;
        bool single = hasGame && SelectedGames().Count() == 1;

        menuLibraryReveal.IsEnabled = single;
        menuLibraryCopy.IsEnabled = single;
        menuLibraryCopyTitle.IsEnabled = single;
        menuLibraryCopyTitleId.IsEnabled = single;
        menuLibraryCopyContentId.IsEnabled = single;
        menuLibraryCopyPath.IsEnabled = single;
        menuLibraryCopyFileName.IsEnabled = single;
        menuLibraryRename.IsEnabled = hasGame;
        menuLibraryRenameAll.IsEnabled = _games.Count > 0;
        menuLibraryRenameByPriority.IsEnabled = hasGame;
        menuLibrarySaveArtwork.IsEnabled = single;
        menuLibraryMove.IsEnabled = hasGame;
        menuLibraryDelete.IsEnabled = hasGame;
        menuLibraryDuplicates.IsEnabled = _games.Count > 1;
        menuLibraryExport.IsEnabled = _games.Count > 0;

        menuLibraryGroupExport.IsVisible = groupContext;
        menuLibraryGroupArtwork.IsVisible = groupContext;
        menuLibraryGroupBy.IsEnabled = true;
    }

    private void BuildLibraryViewsMenu()
    {
        if (_libraryViewsMenu is not null) return;
        _libraryViewsMenu = new MenuItem { Header = "Views" };
        contextLibrary.Items.Add(new Separator());
        contextLibrary.Items.Add(_libraryViewsMenu);
        RebuildLibraryViewsMenu();
    }

    private void RebuildLibraryViewsMenu()
    {
        if (_libraryViewsMenu is null) return;
        _libraryViewsMenu.Items.Clear();

        var save = new MenuItem { Header = "Save current view..." };
        save.Click += (_, _) => _ = SaveCurrentViewAsync();
        _libraryViewsMenu.Items.Add(save);
        _libraryViewsMenu.Items.Add(new Separator());

        if (_settings.SavedViews.Count == 0)
        {
            _libraryViewsMenu.Items.Add(new MenuItem { Header = "(none saved)", IsEnabled = false });
            return;
        }

        foreach (SavedLibraryView view in _settings.SavedViews)
        {
            var item = new MenuItem { Header = view.Name };
            item.Click += (_, _) => ApplyView(view);
            _libraryViewsMenu.Items.Add(item);
        }

        _libraryViewsMenu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Delete view" };
        foreach (SavedLibraryView view in _settings.SavedViews)
        {
            var item = new MenuItem { Header = view.Name };
            item.Click += (_, _) =>
            {
                _settings.SavedViews.Remove(view);
                SaveSettingsQuietly();
                RebuildLibraryViewsMenu();
            };
            remove.Items.Add(item);
        }
        _libraryViewsMenu.Items.Add(remove);
    }

    private async Task SaveCurrentViewAsync()
    {
        string? name = await PromptTextAsync("Save view", "Name for the current view:", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        _settings.SavedViews.RemoveAll(view => string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.SavedViews.Add(new SavedLibraryView
        {
            Name = name,
            Query = searchLibrary.SearchText,
            Categories = SelectedFilterValues(cboFilterCategory),
            Regions = SelectedFilterValues(cboFilterRegion),
            Formats = SelectedFilterValues(cboFilterFormat),
            GroupBy = _libraryGroupBy,
            SortKeys = [.. _settings.LibrarySortKeys ?? []],
            HiddenColumns = [.. _settings.LibraryHiddenColumns ?? []],
            ColumnOrder = [.. _settings.LibraryColumnOrder ?? []]
        });
        SaveSettingsQuietly();
        RebuildLibraryViewsMenu();
        SetStatus($"Saved view '{name}'.");
    }

    private void ApplyView(SavedLibraryView view)
    {
        _suppressFilterEvents = true;
        try
        {
            searchLibrary.SearchText = view.Query;
            SetFilterValue(cboFilterCategory, view.Categories.FirstOrDefault());
            SetFilterValue(cboFilterRegion, view.Regions.FirstOrDefault());
            SetFilterValue(cboFilterFormat, view.Formats.FirstOrDefault());
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        _settings.LibraryHiddenColumns = [.. view.HiddenColumns];
        _settings.LibraryColumnOrder = [.. view.ColumnOrder];
        _settings.LibrarySortKeys = [.. view.SortKeys];
        ApplyLibraryColumnVisibility();
        RestoreLibraryColumnLayout();
        LoadLibrarySortKeys();
        SetGroupBy(view.GroupBy, persist: false);
        ApplyFilter();
        SaveSettingsQuietly();
        SetStatus($"Applied view '{view.Name}'.");
    }

    private void RebuildRecentMenu()
    {
        menuRecent.Items.Clear();
        if (_settings.RecentFolders.Count == 0)
        {
            menuRecent.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        foreach (string folder in _settings.RecentFolders.ToArray())
        {
            string captured = folder;
            var item = new MenuItem { Header = captured };
            item.Click += async (_, _) =>
            {
                try { await ScanAsync([captured], merge: true); }
                catch (OperationCanceledException) { }
            };
            menuRecent.Items.Add(item);
        }
        menuRecent.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear recent folders" };
        clear.Click += (_, _) =>
        {
            _settings.RecentFolders.Clear();
            SaveSettingsQuietly();
            RebuildRecentMenu();
        };
        menuRecent.Items.Add(clear);
    }

    private void UpdateWorkspaceLabels()
    {
        lblWsExfat.Text = "Workspace: " + WorkspaceLabel();
        lblWsFfpkg.Text = "Workspace: " + WorkspaceLabel();
        lblWsFfpfsc.Text = "Workspace: " + WorkspaceLabel();
        lblImageTemp.Text = "Workspace: " + WorkspaceLabel();
    }

    private string WorkspaceLabel() =>
        string.IsNullOrWhiteSpace(_settings.WorkspaceDirectory)
            ? Path.Combine(Path.GetTempPath(), "PS5PKGTool")
            : _settings.WorkspaceDirectory;

    private void ApplyDefaultGrouping()
    {
        _libraryGroupBy = FilterGroupKeys.Contains(_settings.DefaultGroupBy) ? _settings.DefaultGroupBy : string.Empty;
        foreach ((MenuItem item, string key) in _groupItems)
            item.IsChecked = string.Equals(key, _libraryGroupBy, StringComparison.Ordinal);
        SyncFilterGroupCombo();
    }

    private void SetGroupBy(string key, bool persist = true)
    {
        _libraryGroupBy = key;
        foreach ((MenuItem item, string itemKey) in _groupItems)
            item.IsChecked = string.Equals(itemKey, key, StringComparison.Ordinal);
        SyncFilterGroupCombo();
        ApplyFilter();
        // Remember the grouping across restarts, but only for direct user choices (menu / combo).
        // Applying a saved view keeps the view's own grouping without overwriting the default.
        if (persist && !string.Equals(_settings.DefaultGroupBy, key, StringComparison.Ordinal))
        {
            _settings.DefaultGroupBy = key;
            SaveSettingsQuietly();
        }
        SetStatus(key.Length == 0 ? "Grouping disabled." : $"Grouped by {GroupLabel(key)}.");
    }

    private static string GroupLabel(string key) => key switch
    {
        "family" => "Family",
        "titleid" => "Title ID",
        "category" => "Category",
        "region" => "Region",
        "source" => "Source format",
        "firmware" => "Required firmware",
        _ => "None"
    };

    internal string GroupKey(Ps5GameInfo game) => _libraryGroupBy switch
    {
        // A family is everything sharing a title: base, updates and DLC.
        "family" => !string.IsNullOrWhiteSpace(game.TitleId) ? game.TitleId
            : !string.IsNullOrWhiteSpace(game.ContentId) ? game.ContentId
            : LibraryFileName(game),
        "titleid" => game.TitleId,
        "category" => CategoryOf(game),
        "region" => RegionOf(game),
        "source" => game.SourceDescription,
        "firmware" => game.RequiredSystemSoftware,
        _ => string.Empty
    };

    /// <summary>The item's role in its title family: Base, Update, DLC or App.</summary>
    private string RelationshipLabel(Ps5GameInfo game)
    {
        string role = CategoryOf(game) switch
        {
            "Game" => "Base",
            "Patch" => "Update",
            "DLC" or "Add-on" => "DLC",
            "App" => "App",
            _ => "Unknown"
        };
        if (role == "Update" && IsSupersededUpdate(game)) role = "Update (older)";
        return role;
    }

    private readonly HashSet<string> _supersededUpdates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rebuilds the set of patches superseded by a higher version of the same title. Computed once per
    /// filter pass so neither row filling nor Role sorting walks the whole library per comparison.
    /// </summary>
    private void RebuildFamilyIndex()
    {
        _supersededUpdates.Clear();
        var highest = new Dictionary<string, VersionKey>(StringComparer.OrdinalIgnoreCase);
        foreach (Ps5GameInfo game in _games)
        {
            if (string.IsNullOrWhiteSpace(game.TitleId)) continue;
            if (!string.Equals(CategoryOf(game), "Patch", StringComparison.OrdinalIgnoreCase)) continue;
            VersionKey key = VersionKey.Parse(game.DisplayVersion);
            if (!highest.TryGetValue(game.TitleId, out VersionKey best) || key.CompareTo(best) > 0)
                highest[game.TitleId] = key;
        }
        foreach (Ps5GameInfo game in _games)
        {
            if (string.IsNullOrWhiteSpace(game.TitleId)) continue;
            if (!string.Equals(CategoryOf(game), "Patch", StringComparison.OrdinalIgnoreCase)) continue;
            if (highest.TryGetValue(game.TitleId, out VersionKey best) &&
                VersionKey.Parse(game.DisplayVersion).CompareTo(best) < 0)
                _supersededUpdates.Add(game.RootPath);
        }
    }

    /// <summary>True when another installed patch for the same title has a higher version.</summary>
    private bool IsSupersededUpdate(Ps5GameInfo game) => _supersededUpdates.Contains(game.RootPath);

    /// <summary>Base first, then updates, then DLC, then apps; used to order a family view.</summary>
    internal static int RolePriority(Ps5GameInfo game) => CategoryOf(game) switch
    {
        "Game" => 0,
        "Patch" => 1,
        "DLC" or "Add-on" => 2,
        "App" => 3,
        _ => 4
    };

    private void RevealGame(Ps5GameInfo game)
    {
        if (!Directory.Exists(game.RootPath) && !File.Exists(game.RootPath))
        {
            AppDialog.ShowInfo("The source path no longer exists.", "PS5 PKG Tool");
            return;
        }
        if (!PlatformService.RevealPath(game.RootPath))
            AppDialog.ShowError("Unable to reveal the source path.", "Reveal in Explorer");
    }

    private void ApplyFilter()
    {
        string query = searchLibrary.SearchText.Trim();
        ParseQueryTokens(query);
        RebuildFamilyIndex();
        _visibleGames = _games
            .Where(MatchesFilters)
            .Where(MatchesQueryTokens)
            .ToList();

        PopulateLibraryGrid();

        statusCount.Text = $"{_visibleGames.Count:N0} of {_games.Count:N0} games";
        menuLibraryRenameAll.IsEnabled = _games.Count > 0;
        lblFilterEmpty.IsVisible = _visibleGames.Count == 0 && _games.Count > 0;
        UpdateFilterBadges();
        RebuildFilterChips();
        if (_visibleGames.Count == 0)
        {
            SetSelectedGame(null);
            ClearDetails();
        }
    }

    private void RefreshLibraryPresentation()
    {
        gridLibrary.RowHeight = Math.Max(16, _settings.GridRowHeight);
        gridLibrary.ShowGridLines = _settings.ShowGridLines;
        gridLibrary.RefreshRows();
    }

    // ------------------------------------------------------------------------------------------
    // Filter controls
    // ------------------------------------------------------------------------------------------

    /// <summary>The value selected in a single-select filter, or null for "Any". Kept as a one-item
    /// list so existing saved views stay readable.</summary>
    private static List<string> SelectedFilterValues(ComboBox combo) =>
        combo.SelectedIndex > 0 && combo.SelectedItem is string value && value.Length > 0
            ? [value]
            : [];

    private static string? SelectedFilterValue(ComboBox combo) =>
        combo.SelectedIndex > 0 ? combo.SelectedItem as string : null;

    private static void SetFilterValue(ComboBox combo, string? value)
    {
        if (string.IsNullOrEmpty(value)) { combo.SelectedIndex = 0; return; }
        for (int index = 0; index < combo.Items.Count; index++)
        {
            if (string.Equals(combo.Items[index] as string, value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void CboFilterValue_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_suppressFilterEvents) ApplyFilter();
    }

    private static string SourceFilterValue(Ps5GameInfo game) => game.SourceKind switch
    {
        Ps5SourceKind.LooseDump => "Dump Files",
        Ps5SourceKind.SonyPackage => "PKG",
        Ps5SourceKind.Ffpfsc => "FFPFSC",
        Ps5SourceKind.FilesystemImage => "exFAT",
        Ps5SourceKind.Ffpkg => "FFPKG",
        _ => "Other"
    };

    private bool MatchesFilters(Ps5GameInfo game)
    {
        string? category = SelectedFilterValue(cboFilterCategory);
        if (category is not null && !string.Equals(category, CategoryOf(game), StringComparison.OrdinalIgnoreCase))
            return false;

        string? region = SelectedFilterValue(cboFilterRegion);
        if (region is not null && !string.Equals(region, RegionOf(game), StringComparison.OrdinalIgnoreCase))
            return false;

        string? source = SelectedFilterValue(cboFilterFormat);
        if (source is not null && !string.Equals(source, SourceFilterValue(game), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void ClearFilters()
    {
        _suppressFilterEvents = true;
        try
        {
            cboFilterCategory.SelectedIndex = 0;
            cboFilterRegion.SelectedIndex = 0;
            cboFilterFormat.SelectedIndex = 0;
            searchLibrary.SearchText = string.Empty;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
        ApplyFilter();
    }

    private void CboFilterGroup_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents) return;
        int index = cboFilterGroup.SelectedIndex;
        SetGroupBy(index >= 0 && index < FilterGroupKeys.Length ? FilterGroupKeys[index] : string.Empty);
    }

    private void CboFilterPreset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int preset = cboFilterPreset.SelectedIndex;
        if (_suppressFilterEvents || preset <= 0) return;
        _suppressFilterEvents = true;
        try
        {
            cboFilterCategory.SelectedIndex = 0;
            cboFilterRegion.SelectedIndex = 0;
            cboFilterFormat.SelectedIndex = 0;
            switch (preset)
            {
                // All means all: clear the query too, not just the dropdown filters.
                case 1: searchLibrary.SearchText = string.Empty; break;
                case 2: SetFilterValue(cboFilterCategory, "Game"); break;
                case 3: SetFilterValue(cboFilterCategory, "Patch"); break;
                case 4: SetFilterValue(cboFilterCategory, "DLC"); break;
                case 5: SetFilterValue(cboFilterFormat, "Dump Files"); break;
                case 6: SetFilterValue(cboFilterFormat, "PKG"); break;
                case 7: SetFilterValue(cboFilterFormat, "FFPKG"); break;
                case 8: SetFilterValue(cboFilterFormat, "FFPFSC"); break;
                case 9: SetFilterValue(cboFilterFormat, "exFAT"); break;
            }
        }
        finally
        {
            _suppressFilterEvents = false;
            cboFilterPreset.SelectedIndex = 0;
        }
        ApplyFilter();
    }

    private void SyncFilterGroupCombo()
    {
        _suppressFilterEvents = true;
        try
        {
            int index = Array.IndexOf(FilterGroupKeys, _libraryGroupBy);
            cboFilterGroup.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private void SyncFilterControls()
    {
        _suppressFilterEvents = true;
        try
        {
            int index = Array.IndexOf(FilterGroupKeys, _libraryGroupBy);
            cboFilterGroup.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private void UpdateFilterBadges()
    {
        UpdateFilterCaption(lblFilterCategory, "Category", cboFilterCategory);
        UpdateFilterCaption(lblFilterRegion, "Region", cboFilterRegion);
        UpdateFilterCaption(lblFilterFormat, "Format", cboFilterFormat);
        // Grouping is a view setting, not a filter, so it does not keep the reset button active.
        bool active = cboFilterCategory.SelectedIndex > 0 || cboFilterRegion.SelectedIndex > 0 ||
                      cboFilterFormat.SelectedIndex > 0 || searchLibrary.SearchText.Length > 0;
        btnFilterClear.Content = "Reset filters";
        btnFilterClear.IsVisible = active;
    }

    private static void UpdateFilterCaption(TextBlock label, string caption, ComboBox combo) =>
        label.Text = combo.SelectedIndex > 0 ? $"{caption} (1)" : caption;

    private void RebuildFilterChips()
    {
        chipsFilter.ClearChips();
        string query = searchLibrary.SearchText.Trim();
        if (query.Length > 0)
            chipsFilter.AddChip("Search: " + query, (_, _) => searchLibrary.SearchText = string.Empty);
        if (ValidateQuery(query) is string warning)
            chipsFilter.AddChip("Check query: " + warning, (_, _) => { });
        AddChips(cboFilterCategory);
        AddChips(cboFilterRegion);
        AddChips(cboFilterFormat);
    }

    private void AddChips(ComboBox combo)
    {
        if (SelectedFilterValue(combo) is string value)
            chipsFilter.AddChip(value, (_, _) => combo.SelectedIndex = 0);
    }

    private CancellationTokenSource? _searchDebounce;

    private async void SearchLibrary_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        try
        {
            await Task.Delay(200, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        if (!cts.IsCancellationRequested) ApplyFilter();
    }

    // ------------------------------------------------------------------------------------------
    // Query grammar
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Matches a game against the search box. Supports space-separated AND tokens, quoted phrases,
    /// field prefixes (<c>title:</c>, <c>id:</c>, <c>content:</c>, <c>category:</c>, <c>region:</c>,
    /// <c>size:</c>, <c>version:</c>, <c>fw:</c>, <c>feature:</c>, <c>drm:</c>, <c>path:</c>),
    /// OR groups with <c>|</c>, numeric comparisons (<c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>,
    /// <c>&lt;=</c>, <c>=</c>) and <c>-</c> negation.
    /// </summary>
    private readonly List<(string Body, bool Negate)> _queryTokens = [];

    /// <summary>Tokenizes the query once per filter pass instead of once per record.</summary>
    private void ParseQueryTokens(string query)
    {
        _queryTokens.Clear();
        if (string.IsNullOrWhiteSpace(query)) return;
        foreach (string token in TokenizeQuery(query))
        {
            bool negate = token.Length > 1 && token[0] == '-';
            string body = negate ? token[1..] : token;
            if (body.Length == 0) continue;
            _queryTokens.Add((body, negate));
        }
    }

    private bool MatchesQueryTokens(Ps5GameInfo game)
    {
        foreach ((string body, bool negate) in _queryTokens)
        {
            int colon = body.IndexOf(':');
            bool match = colon > 0
                ? MatchField(game, body[..colon].ToLowerInvariant(), body[(colon + 1)..])
                : MatchFreeText(game, body);
            if (negate) match = !match;
            if (!match) return false;
        }
        return true;
    }

    private static IEnumerable<string> TokenizeQuery(string query)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in query)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static readonly string[] QueryFieldKeys =
    [
        "title", "id", "titleid", "title-id", "content", "contentid", "content-id",
        "category", "role", "region", "source", "format", "drm", "path", "location",
        "feature", "features", "size", "version", "fw", "firmware"
    ];

    /// <summary>
    /// Returns a human-readable hint when the query cannot be understood (unbalanced quotes, an unknown
    /// field prefix, a missing value, or a malformed size/version comparison), or null when it is fine.
    /// The query still runs - this only surfaces the likely mistake inline.
    /// </summary>
    private static string? ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (query.Count(c => c == '"') % 2 != 0)
            return "unbalanced quotation marks.";

        foreach (string token in TokenizeQuery(query))
        {
            string body = token.Length > 1 && token[0] == '-' ? token[1..] : token;
            if (body.Length == 0) continue;
            int colon = body.IndexOf(':');
            if (colon <= 0) continue;

            string field = body[..colon].ToLowerInvariant();
            string value = body[(colon + 1)..];
            if (Array.IndexOf(QueryFieldKeys, field) < 0)
                return $"unknown field '{field}:'. Valid fields: title, id, content, category, role, region, source, size, version, fw, feature, drm, path.";
            if (value.Length == 0)
                return $"'{field}:' needs a value.";

            if (field == "size" &&
                !(TryParseComparison(value, out _, out string sizeRest) && TryParseSize(sizeRest, out _)))
                return $"'{value}' is not a valid size (try size:>50GB).";
            if (field is "version" or "fw" or "firmware")
            {
                if (!TryParseComparison(value, out _, out string versionRest) || versionRest.Length == 0)
                    return $"'{field}:' is missing a value after the comparison.";
                if (CompareVersions(versionRest, versionRest) is null)
                    return $"'{versionRest}' is not a valid version.";
            }
        }
        return null;
    }

    private static bool MatchFreeText(Ps5GameInfo game, string value) =>
        MatchAny(value, part =>
            ContainsText(game.Title, part) ||
            ContainsText(game.TitleId, part) ||
            ContainsText(game.ContentId, part) ||
            ContainsText(game.RootPath, part) ||
            ContainsText(game.SourceDescription, part));

    private bool MatchField(Ps5GameInfo game, string field, string value) => field switch
    {
        "title" => MatchText(game.Title, value),
        "id" or "titleid" or "title-id" => MatchText(game.TitleId, value),
        "content" or "contentid" or "content-id" => MatchText(game.ContentId, value),
        "category" => MatchAny(value, part => ContainsText(CategoryOf(game), part)),
        "role" => MatchAny(value, part => ContainsText(RelationshipLabel(game), part)),
        "region" => MatchAny(value, part => ContainsText(RegionOf(game), part)),
        "source" or "format" => MatchAny(value, part =>
            ContainsText(game.SourceDescription, part) || ContainsText(SourceFilterValue(game), part)),
        "drm" => MatchAny(value, part => ContainsText(game.DrmType, part)),
        "path" or "location" => MatchAny(value, part => ContainsText(game.RootPath, part)),
        "feature" or "features" => MatchAny(value, part =>
            game.DeclaredFeatures.Any(feature => ContainsText(feature, part))),
        "size" => MatchSize(value, game.SourceSize),
        "version" => MatchVersion(value, game.DisplayVersion),
        "fw" or "firmware" => MatchVersion(value, game.RequiredSystemSoftware),
        _ => MatchFreeText(game, value)
    };

    /// <summary>
    /// Text field match: a leading <c>=</c> means exact identity (<c>id:=PPSA12345</c>), otherwise a
    /// value-level <c>|</c> contains match.
    /// </summary>
    private static bool MatchText(string? source, string value)
    {
        if (value.StartsWith('='))
            return string.Equals(source, value[1..].Trim(), StringComparison.OrdinalIgnoreCase);
        return MatchAny(value, part => ContainsText(source, part));
    }

    private static bool MatchAny(string value, Func<string, bool> predicate)
    {
        foreach (string part in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
            if (predicate(part)) return true;
        return false;
    }

    private static bool ContainsText(string? source, string value) =>
        !string.IsNullOrEmpty(source) && source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool MatchSize(string value, long size)
    {
        if (TryParseComparison(value, out string op, out string rest) && TryParseSize(rest, out long target))
            return Satisfies(size, target, op);
        return TryParseSize(value, out long minimum) && size >= minimum;
    }

    private static bool MatchVersion(string value, string candidate)
    {
        if (TryParseComparison(value, out string op, out string rest))
            return CompareVersions(candidate, rest) is int comparison && Satisfies(comparison, op);
        return ContainsText(candidate, value);
    }

    private static bool TryParseComparison(string value, out string op, out string rest)
    {
        foreach (string candidate in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (value.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                rest = value[candidate.Length..].Trim();
                return true;
            }
        }
        op = "=";
        rest = value;
        return false;
    }

    private static bool Satisfies(long value, long target, string op) => op switch
    {
        ">" => value > target,
        "<" => value < target,
        ">=" => value >= target,
        "<=" => value <= target,
        _ => value == target
    };

    private static bool Satisfies(int comparison, string op) => op switch
    {
        ">" => comparison > 0,
        "<" => comparison < 0,
        ">=" => comparison >= 0,
        "<=" => comparison <= 0,
        _ => comparison == 0
    };

    private static int? CompareVersions(string? left, string? right)
    {
        long[]? a = ParseVersion(left);
        long[]? b = ParseVersion(right);
        if (a is null || b is null) return null;
        int length = Math.Max(a.Length, b.Length);
        for (int i = 0; i < length; i++)
        {
            long x = i < a.Length ? a[i] : 0;
            long y = i < b.Length ? b[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    private static long[]? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = new List<long>();
        foreach (string piece in value.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            int digits = 0;
            while (digits < piece.Length && char.IsDigit(piece[digits])) digits++;
            if (digits == 0) break;
            // Guard against numeric overflow from user input instead of throwing out of the search.
            if (!long.TryParse(piece[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out long number))
                return null;
            parts.Add(number);
        }
        return parts.Count > 0 ? parts.ToArray() : null;
    }

    private static bool TryParseSize(string value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        int digits = 0;
        while (digits < text.Length && (char.IsDigit(text[digits]) || text[digits] == '.')) digits++;
        if (digits == 0 ||
            !double.TryParse(text[..digits], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return false;
        double multiplier = text[digits..] switch
        {
            "" or "B" => 1d,
            "K" or "KB" => 1024d,
            "M" or "MB" => 1024d * 1024d,
            "G" or "GB" => 1024d * 1024d * 1024d,
            "T" or "TB" => 1024d * 1024d * 1024d * 1024d,
            _ => 0d
        };
        if (multiplier == 0d) return false;
        bytes = (long)(number * multiplier);
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // Duplicates and export
    // ------------------------------------------------------------------------------------------

    private async Task FindDuplicatesAsync()
    {
        List<IGrouping<string, Ps5GameInfo>> groups = _games
            .GroupBy(DuplicateKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ToList();
        if (groups.Count == 0)
        {
            AppDialog.ShowInfo("No duplicate sources were found.", "Find Duplicates");
            return;
        }

        // Confirming byte-identical copies needs a content hash; read each candidate's param.json off the
        // UI thread. Cheap (param.json is small) but real: equal hash + equal size means the same build.
        List<Ps5GameInfo> candidates = groups.SelectMany(group => group).ToList();
        SetStatus($"Comparing {candidates.Count:N0} duplicate candidate(s)...");
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Run(() =>
            {
                foreach (Ps5GameInfo game in candidates)
                    fingerprints[game.RootPath] = ComputeDuplicateFingerprint(game);
            });
        }
        finally
        {
            Cursor = Avalonia.Input.Cursor.Default;
        }

        int identical = 0;
        var lines = new List<string> { $"{groups.Count:N0} duplicate group(s):", string.Empty };
        foreach (IGrouping<string, Ps5GameInfo> group in groups)
        {
            bool allHashed = group.All(game =>
                fingerprints.TryGetValue(game.RootPath, out string? hash) && hash.Length > 0);
            bool sameHash = allHashed &&
                group.Select(game => fingerprints[game.RootPath]).Distinct(StringComparer.Ordinal).Count() == 1;
            if (sameHash) identical++;

            string verdict = sameHash ? "identical" : "possible";
            lines.Add($"[{verdict}] {group.Key}  ({group.Count()} copies)");
            foreach (Ps5GameInfo game in group)
            {
                string hash = fingerprints.TryGetValue(game.RootPath, out string? value) && value.Length > 0
                    ? "sha256:" + value[..Math.Min(16, value.Length)]
                    : "(hash unavailable)";
                lines.Add($"    {hash}  {game.RootPath}");
            }
            lines.Add(string.Empty);
        }
        lines.Insert(1,
            $"{identical:N0} byte-identical (matching param.json hash), " +
            $"{groups.Count - identical:N0} possible (same metadata only).");
        ShowTextReport("Duplicate PS5 sources", string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// SHA-256 of <c>sce_sys/param.json</c> as a cheap content fingerprint, or an empty string when it
    /// cannot be read. Two sources with the same size and fingerprint are the same build.
    /// </summary>
    private static string ComputeDuplicateFingerprint(Ps5GameInfo game)
    {
        try
        {
            byte[] param = GameFileSystem.ReadFileChunk(game, "sce_sys/param.json", 0, 4 * 1024 * 1024).Data;
            return param.Length == 0 ? string.Empty : Convert.ToHexString(SHA256.HashData(param));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string DuplicateKey(Ps5GameInfo game)
    {
        if (!string.IsNullOrWhiteSpace(game.ContentId)) return "content:" + game.ContentId;
        if (!string.IsNullOrWhiteSpace(game.TitleId)) return $"title:{game.TitleId}:{game.SourceSize}";
        return $"file:{Path.GetFileName(game.RootPath)}:{game.SourceSize}";
    }

    private void ExportLibraryCsv()
    {
        List<Ps5GameInfo> games = SelectedGames().ToList();
        if (games.Count == 0) games = [.. _visibleGames];
        _ = ExportGamesCsvAsync(games, "PS5-library.csv");
    }

    private async Task ExportGamesCsvAsync(IReadOnlyList<Ps5GameInfo> games, string defaultFileName)
    {
        if (games.Count == 0)
        {
            AppDialog.ShowInfo("There is nothing to export.", "Export library");
            return;
        }
        string? fileName = await PickSaveFileAsync("Export PS5 library", defaultFileName,
            new Avalonia.Platform.Storage.FilePickerFileType("CSV") { Patterns = ["*.csv"] });
        if (string.IsNullOrEmpty(fileName)) return;
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("Title,TitleId,ContentId,Category,Region,Source,SizeBytes,Version,RequiredFirmware,DRM,FileName,Path");
            foreach (Ps5GameInfo game in games)
            {
                string[] fields =
                [
                    game.Title, game.TitleId, game.ContentId, CategoryOf(game), RegionOf(game),
                    game.SourceDescription, game.SourceSize.ToString(), game.DisplayVersion,
                    game.RequiredSystemSoftware, game.DrmType,
                    Path.GetFileName(game.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    game.RootPath
                ];
                builder.AppendLine(string.Join(',', fields.Select(Csv)));
            }
            await File.WriteAllTextAsync(fileName, builder.ToString(), new UTF8Encoding(true));
            SetStatus($"Exported {games.Count:N0} games.");
            await AppDialog.ShowInfoAsync($"Exported {games.Count:N0} games to:\n{fileName}", "Export complete");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await AppDialog.ShowErrorAsync(ex.Message, "Export library");
        }
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static void ReplaceSettingPath(List<string> paths, string source, string target)
    {
        for (int index = 0; index < paths.Count; index++)
            if (PathIdentity.Equals(paths[index], source))
                paths[index] = target;
    }
}
