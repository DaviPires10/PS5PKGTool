using PS5PKGTool.Core.Backends;
using PS5PKGTool.Core.Builders;
using PS5PKGTool.Core.Models;
using PS5PKGTool.Core.Services;
using PS5PKGTool.Core.Tasks;
using PS5PKGTool.Ffpfsc;
using PS5PKGTool.Infrastructure;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PS5PKGTool.Controls;

namespace PS5PKGTool.Views;

public partial class MainWindow
{
    private void WireLibraryActions()
    {
        menuLibraryRename.Click += (_, _) => RunRenameWithFormat(RenameFormatOrDefault(_settings.RenameFormat), all: false);
        menuLibraryRenameAll.Click += (_, _) => RunRenameWithFormat(RenameFormatOrDefault(_settings.RenameFormat), all: true);
        menuLibraryRenameByPriority.Click += (_, _) => _ = RenameGamesByInstallOrderAsync();
        menuLibrarySaveArtwork.Click += (_, _) => _ = SaveArtworkForGamesAsync(SelectedGames().ToList());
        menuLibraryGroupArtwork.Click += (_, _) => _ = SaveArtworkForGamesAsync(GroupContextGames());
        menuLibraryGroupExport.Click += (_, _) =>
        {
            IReadOnlyList<Ps5GameInfo> games = GroupContextGames();
            if (games.Count > 0) _ = ExportGamesCsvAsync(games, "PS5-group.csv");
        };
        menuLibraryDelete.Click += (_, _) => _ = DeleteSelectedAsync();

        menuLibraryMoveTitle.Click += (_, _) => _ = MoveGamesToFolderAsync(MoveMode.Title);
        menuLibraryMoveTitleId.Click += (_, _) => _ = MoveGamesToFolderAsync(MoveMode.TitleId);
        menuLibraryMoveCategory.Click += (_, _) => _ = MoveGamesToFolderAsync(MoveMode.Category);
        menuLibraryMoveRegion.Click += (_, _) => _ = MoveGamesToFolderAsync(MoveMode.Region);
        menuLibraryMoveSource.Click += (_, _) => _ = MoveGamesToFolderAsync(MoveMode.Source);
        menuLibraryMoveSingle.Click += (_, _) => _ = MoveGamesToFolderAsync(MoveMode.Flat);
    }

    // ---------------------------------------------------------------- convert selected image

    private void menuLibraryConvertFfpfsc_Click(object? sender, EventArgs e) => _ = ConvertSelectedImageAsync(null);

    private async Task ConvertSelectedImageAsync(Ps5ImageConversionTarget? preferred)
    {
        if (SelectedGame() is not { } game) return;
        string source = game.RootPath;
        bool isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
        {
            AppDialog.ShowInfo("The selected source no longer exists.", "Convert image");
            return;
        }

        List<Ps5ImageConversionTarget> targets = [];
        if (isDirectory)
        {
            targets.AddRange(Enum.GetValues<Ps5ImageConversionTarget>());
        }
        else
        {
            Ps5ImageFormat format = Ps5ImageConversionService.DetectSource(source);
            foreach (Ps5ImageConversionTarget target in Enum.GetValues<Ps5ImageConversionTarget>())
                if (Ps5ImageConversionService.IsSupported(format, target))
                    targets.Add(target);
        }

        if (targets.Count == 0)
        {
            AppDialog.ShowInfo("The selected source cannot be converted.", "Convert image");
            return;
        }

        ConvertImageResult? choice = await ConvertImageWindow.ShowAsync(this, source, targets, preferred);
        if (choice is null) return;
        string output = choice.OutputPath;
        Ps5ImageConversionTarget selected = choice.Target;
        bool overwrite = choice.Overwrite;
        string sourceRoute = isDirectory ? "dump" : Ps5ImageConversionService.DetectSource(source) switch
        {
            Ps5ImageFormat.Exfat => "exFAT",
            Ps5ImageFormat.Ufs2 => "FFPKG",
            Ps5ImageFormat.Pfs => "FFPFSC",
            _ => "image"
        };
        string targetRoute = selected switch
        {
            Ps5ImageConversionTarget.Exfat => "exFAT",
            Ps5ImageConversionTarget.Ffpkg => "FFPKG",
            _ => "FFPFSC"
        };

        EnqueueTask(PackageTaskTypes.ImageConvert, $"Convert {Path.GetFileName(source)}",
            async (progress, token) =>
            {
                var bridge = new Progress<Ps5ImageConversionProgress>(value =>
                    progress.Report(new PackageTaskProgress(value.Stage, 0, 0, value.Completed, value.Total, 0, 0,
                        string.Empty)));
                await Ps5ImageConversionService.ConvertAsync(source, output, selected, overwrite, bridge, token)
                    .ConfigureAwait(false);
            },
            sourcePath: source, outputPath: output,
            operation: "Convert", sourceFormat: sourceRoute, targetFormat: targetRoute,
            stagePlan: PackageTaskPlans.ConvertImage,
            payload: Payload(("source", source), ("output", output), ("target", selected.ToString()),
                ("overwrite", overwrite.ToString())),
            onFinished: task =>
            {
                SetStatus(task.Status == PackageTaskStatus.Completed
                    ? $"Converted to {Path.GetFileName(output)}."
                    : $"Image conversion {StatusText(task.Status).ToLowerInvariant()}.");
            });
        SetStatus("Queued: image conversion. See the Tasks tab.");
    }

    // ---------------------------------------------------------------- check patches missing base

    private void CheckPatchesMissingBase()
    {
        var lines = new List<string>();
        int missing = 0;
        foreach (IGrouping<string, Ps5GameInfo> group in _games
            .Where(game => !string.IsNullOrWhiteSpace(game.TitleId))
            .GroupBy(game => game.TitleId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool hasBase = group.Any(game => CategoryOf(game).Equals("Game", StringComparison.OrdinalIgnoreCase));
            List<Ps5GameInfo> patches = group
                .Where(game => CategoryOf(game).Equals("Patch", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hasBase || patches.Count == 0) continue;

            missing++;
            lines.Add($"{group.Key}: {patches.Count} patch(es) without a base game");
            foreach (Ps5GameInfo patch in patches)
                lines.Add("    " + patch.RootPath);
        }

        if (missing == 0)
        {
            AppDialog.ShowInfo("Every patch has a matching base game.", "Patches missing base");
            return;
        }

        ShowTextReport("Patches missing base",
            $"{missing:N0} title(s) have patches without a base game:{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, lines));
    }

    // ---------------------------------------------------------------- save artwork

    private async Task SaveArtworkForGamesAsync(IReadOnlyList<Ps5GameInfo> games)
    {
        if (games.Count == 0) return;
        string? folder = await PickFolderAsync("Select a folder for the artwork images");
        if (folder is null) return;

        int saved = 0;
        try
        {
            foreach (Ps5GameInfo game in games)
            {
                Ps5GameDetails details = await GetDetailsAsync(game);
                string baseName = MakeSafeFileName(string.IsNullOrWhiteSpace(game.Title) ? LibraryFileName(game) : game.Title);
                if (baseName.Length == 0) baseName = "artwork";
                foreach ((Ps5ImageData? data, string name) in ArtworkData(details))
                {
                    if (data is null || data.IsEmpty) continue;
                    SaveArtworkImage(data, Path.Combine(folder, baseName + "-" + name));
                    saved++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await AppDialog.ShowErrorAsync(ex.Message, "Save artwork");
            return;
        }

        if (saved == 0) AppDialog.ShowInfo("No artwork was available to save.", "Artwork");
        else SetStatus($"Saved {saved:N0} artwork image(s) to {folder}.");
    }

    private static IEnumerable<(Ps5ImageData? Data, string Name)> ArtworkData(Ps5GameDetails details)
    {
        yield return (details.Icon, "icon0.png");
        yield return (details.Background, "pic0.png");
        yield return (details.Background1, "pic1.png");
        yield return (details.Background2, "pic2.png");
    }

    /// <summary>Writes decoded artwork as PNG. RGBA surfaces are written through Avalonia's encoder.</summary>
    private static void SaveArtworkImage(Ps5ImageData data, string pathWithoutExtension)
    {
        string path = pathWithoutExtension.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? pathWithoutExtension
            : pathWithoutExtension + ".png";
        if (!data.IsRgba)
        {
            File.WriteAllBytes(path, data.Bytes);
            return;
        }

        using var bitmap = CreateBitmap(data);
        using FileStream stream = File.Create(path);
        bitmap.Save(stream);
    }

    private static Bitmap CreateBitmap(Ps5ImageData data)
    {
        var writeable = new WriteableBitmap(new PixelSize(data.Width, data.Height),
            new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Unpremul);
        using (Avalonia.Platform.ILockedFramebuffer buffer = writeable.Lock())
        {
            // Source pixels are RGBA; the framebuffer is BGRA.
            var row = new byte[data.Width * 4];
            for (int y = 0; y < data.Height; y++)
            {
                int sourceRow = y * data.Width * 4;
                for (int x = 0; x < data.Width; x++)
                {
                    row[x * 4 + 0] = data.Bytes[sourceRow + x * 4 + 2];
                    row[x * 4 + 1] = data.Bytes[sourceRow + x * 4 + 1];
                    row[x * 4 + 2] = data.Bytes[sourceRow + x * 4 + 0];
                    row[x * 4 + 3] = data.Bytes[sourceRow + x * 4 + 3];
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0,
                    buffer.Address + y * buffer.RowBytes, row.Length);
            }
        }
        return writeable;
    }

    private async Task<Ps5GameDetails> GetDetailsAsync(Ps5GameInfo game)
    {
        if (_detailsCache.TryGetValue(game.RootPath, out Ps5GameDetails? cached)) return cached;
        Ps5GameDetails details = await _detailsLoader.LoadAsync(game, CancellationToken.None);
        _detailsCache[game.RootPath] = details;
        return details;
    }

    // ---------------------------------------------------------------- extract package

    private async Task ExtractPackageAsync(Ps5GameInfo? game)
    {
        if (game is null || game.SourceKind != Ps5SourceKind.SonyPackage || !File.Exists(game.RootPath))
        {
            AppDialog.ShowInfo("Select a Sony PKG file to extract.", "Extract package");
            return;
        }

        string? folder = await PickFolderAsync("Select a folder for package extraction");
        if (folder is null) return;

        string source = game.RootPath;
        string destination = FindAvailableDirectory(Path.Combine(folder,
            MakeSafeFileName(Path.GetFileNameWithoutExtension(source))));
        string passcode = string.IsNullOrEmpty(_settings.DebugPasscode)
            ? SonyDebugPackageCredentials.DefaultPasscode
            : _settings.DebugPasscode;
        IPackageBackend engine = ExtractBackend(_settings.ExtractBackend);

        SonyPackageExtractResult? outcome = null;
        EnqueueTask(PackageTaskTypes.PackageExtract, $"Extract {Path.GetFileName(source)}",
            async (progress, token) =>
            {
                outcome = await engine.ExtractAsync(source, destination, passcode,
                    AdaptSonyExtractProgress(progress), token).ConfigureAwait(false);
            },
            sourcePath: source, outputPath: destination,
            operation: "Extract package", sourceFormat: "FPKG", targetFormat: "folder",
            stagePlan: PackageTaskPlans.Extract,
            payload: Payload(("source", source), ("output", destination), ("kind", "sony"), ("passcode", passcode),
                ("backend", engine.Id)),
            onFinished: task =>
            {
                if (task.Status == PackageTaskStatus.Completed && outcome is not null)
                    SetStatus($"Extracted {outcome.FileCount:N0} file(s) to {outcome.Destination}.");
                else
                    SetStatus($"Package extraction {StatusText(task.Status).ToLowerInvariant()}.");
            });
        SetStatus("Queued: package extraction. See the Tasks tab.");
    }

    // ---------------------------------------------------------------- move to folder

    private enum MoveMode { Title, TitleId, Category, Region, Source, Flat }

    private sealed class MoveOutcome
    {
        public List<(Ps5GameInfo Game, string Source, string Target)> Moved { get; } = [];
        public List<string> Skipped { get; } = [];
    }

    private static string MoveModeLabel(MoveMode mode) => mode switch
    {
        MoveMode.TitleId => "Title ID",
        MoveMode.Category => "Category",
        MoveMode.Region => "Region",
        MoveMode.Source => "Source",
        MoveMode.Flat => "a single folder",
        _ => "Title"
    };

    private async Task MoveGamesToFolderAsync(MoveMode mode)
    {
        List<Ps5GameInfo> games = SelectedGames().ToList();
        if (games.Count == 0)
        {
            AppDialog.ShowInfo("Select one or more items to move.", "Move to folder");
            return;
        }
        if (RefuseIfBusy(games)) return;

        string? destinationRoot = await PickFolderAsync("Select the destination folder");
        if (destinationRoot is null) return;

        bool addToLibrary = false;
        if (!IsUnderLibrary(destinationRoot) && !IsManualSource(destinationRoot))
            addToLibrary = await AppDialog.ConfirmAsync(
                $"Add this folder to the library so the moved items stay listed?\n\n{destinationRoot}",
                "Move to folder") == AppDialogResult.Yes;

        string modeLabel = MoveModeLabel(mode);
        if (_settings.ConfirmMove && !await ConfirmMovePreviewAsync(games, destinationRoot, mode, modeLabel)) return;

        var snapshot = games.ToList();
        var outcome = new MoveOutcome();
        EnqueueTask(PackageTaskTypes.LibraryMove, $"Move {snapshot.Count:N0} item(s) by {modeLabel}",
            (progress, token) =>
            {
                MoveWorker(snapshot, destinationRoot, mode, outcome, progress, token);
                return Task.CompletedTask;
            },
            sourcePath: string.Empty, outputPath: destinationRoot,
            operation: "Move", sourceFormat: "library", targetFormat: modeLabel,
            stagePlan: PackageTaskPlans.Single,
            onFinished: _ => CompleteMove(outcome, destinationRoot, addToLibrary, modeLabel));
    }

    private void CompleteMove(MoveOutcome outcome, string destinationRoot, bool addToLibrary, string modeLabel)
    {
        foreach ((Ps5GameInfo game, string source, string target) in outcome.Moved)
            RewriteGamePath(game, source, target);
        if (addToLibrary &&
            !_settings.LibraryFolders.Contains(destinationRoot, StringComparer.OrdinalIgnoreCase))
            _settings.LibraryFolders.Add(destinationRoot);
        if (outcome.Moved.Count > 0) SaveSettingsQuietly();
        _stateStore.SaveManifest(_games);
        ApplyFilter();
        Logger.Info($"Move by {modeLabel}: {outcome.Moved.Count:N0} moved, {outcome.Skipped.Count:N0} skipped.");
        SetStatus($"Moved {outcome.Moved.Count:N0} item(s); skipped {outcome.Skipped.Count:N0}.");
        if (outcome.Moved.Count == 0 && outcome.Skipped.Count > 0)
            AppDialog.ShowWarning("No items were moved. Check the log for details.", "Move to folder");
    }

    private static void MoveWorker(IReadOnlyList<Ps5GameInfo> games, string destinationRoot, MoveMode mode,
        MoveOutcome outcome, IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        int total = games.Count;
        int index = 0;
        foreach (Ps5GameInfo game in games)
        {
            token.ThrowIfCancellationRequested();
            string source = game.RootPath;
            index++;
            progress.Report(new PackageTaskProgress("Moving", index - 1, total, 0, 0, index, total, game.Title));
            if (string.IsNullOrWhiteSpace(source) || (!Directory.Exists(source) && !File.Exists(source)))
            {
                outcome.Skipped.Add($"{game.Title}: source not found");
                continue;
            }

            string? group = GroupFolder(game, mode);
            if (group is null)
            {
                outcome.Skipped.Add($"{game.Title}: no {MoveModeLabel(mode)} value");
                continue;
            }

            string target = Path.Combine(destinationRoot, group, LibraryFileName(game));
            if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
            {
                outcome.Skipped.Add($"{LibraryFileName(game)}: already in the destination");
                continue;
            }
            if (File.Exists(target) || Directory.Exists(target))
            {
                outcome.Skipped.Add($"{LibraryFileName(game)}: destination already exists");
                Logger.Warn($"Move skipped (exists): {target}");
                continue;
            }

            try
            {
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                LibraryFileMover.Move(source, target, token, progress);
                outcome.Moved.Add((game, source, target));
                Logger.Info($"Moved {source} -> {target}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                outcome.Skipped.Add($"{LibraryFileName(game)}: {ex.Message}");
                Logger.Warn($"Move failed for '{source}': {ex.Message}");
            }
        }
        progress.Report(new PackageTaskProgress("Moving", total, total, 0, 0, total, total, string.Empty));
    }

    /// <summary>
    /// Enumerates every planned move (relative destination, skipped items and conflicts) and asks the
    /// user to confirm. Mirrors the target computation used by the worker so the preview cannot mislead.
    /// </summary>
    private static async Task<bool> ConfirmMovePreviewAsync(IReadOnlyList<Ps5GameInfo> games, string destinationRoot,
        MoveMode mode, string modeLabel)
    {
        var entries = new List<string>();
        int moves = 0, skipped = 0;
        foreach (Ps5GameInfo game in games)
        {
            string source = game.RootPath;
            string name = LibraryFileName(game);
            if (string.IsNullOrWhiteSpace(source) || (!Directory.Exists(source) && !File.Exists(source)))
            {
                skipped++;
                entries.Add($"  x {name} - source not found");
                continue;
            }
            string? group = GroupFolder(game, mode);
            if (group is null)
            {
                skipped++;
                entries.Add($"  x {name} - no {modeLabel} value");
                continue;
            }
            string target = Path.Combine(destinationRoot, group, name);
            if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                entries.Add($"  = {name} (already in the destination)");
                continue;
            }
            if (File.Exists(target) || Directory.Exists(target))
            {
                skipped++;
                entries.Add($"  x {name} - destination already exists");
                continue;
            }
            moves++;
            entries.Add($"  {name} -> {Path.GetRelativePath(destinationRoot, target)}");
        }

        var lines = new List<string>
        {
            $"Move {games.Count:N0} source(s) into:",
            $"  {destinationRoot}",
            string.Empty,
            $"Grouped by: {modeLabel}",
            $"{moves:N0} to move, {skipped:N0} skipped.",
            string.Empty
        };
        const int limit = 40;
        lines.AddRange(entries.Take(limit));
        if (entries.Count > limit) lines.Add($"  ... and {entries.Count - limit:N0} more");
        lines.Add(string.Empty);
        lines.Add("Proceed?");

        if (moves == 0)
        {
            AppDialog.ShowInfo(string.Join(Environment.NewLine, lines), "Move to folder");
            return false;
        }
        return await AppDialog.ConfirmAsync(string.Join(Environment.NewLine, lines), "Move to folder") ==
               AppDialogResult.Yes;
    }

    private static string? GroupFolder(Ps5GameInfo game, MoveMode mode)
    {
        switch (mode)
        {
            case MoveMode.Title:
            {
                string category = CategoryOf(game);
                if (category.Equals("DLC", StringComparison.OrdinalIgnoreCase) ||
                    category.Equals("Add-on", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine("Addon", SafeFolder(game.TitleId, "UNKNOWN_TITLEID"));
                if (category.Equals("App", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine("App", SafeFolder(game.Title, "UNKNOWN_TITLEID"));
                if (category.Equals("Game", StringComparison.OrdinalIgnoreCase) ||
                    category.Equals("Patch", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine("Base + Update", SafeFolder(game.Title, "UNKNOWN_TITLEID"));
                return null;
            }
            case MoveMode.TitleId:
                return SafeFolder(game.TitleId, "UNKNOWN_TITLEID");
            case MoveMode.Category:
                return CategoryOf(game) switch
                {
                    "Game" => "Game",
                    "Patch" => "Patch",
                    "DLC" or "Add-on" => "Dlc",
                    "App" => "App",
                    _ => null
                };
            case MoveMode.Region:
                return SafeFolder(RegionOf(game), "Other");
            case MoveMode.Source:
                return SafeFolder(game.SourceDescription, "Other");
            case MoveMode.Flat:
                return string.Empty;
            default:
                return null;
        }
    }

    private static string SafeFolder(string value, string fallback)
    {
        string safe = Ps5RenameFormatter.Sanitize(value);
        return safe.Length > 0 ? safe : Ps5RenameFormatter.Sanitize(fallback);
    }

    private void RewriteGamePath(Ps5GameInfo game, string source, string target)
    {
        game.RootPath = target;
        game.ParamPath = ReplacePathPrefix(game.ParamPath, source, target);
        ReplaceSettingPath(_settings.LibraryFolders, source, target);
        ReplaceSettingPath(_settings.ManualSources, source, target);
        ReplaceSettingPath(_settings.RecentFolders, source, target);
        _detailsCache.Remove(source);
        ForgetLibraryThumbnail(source);
    }

    private static string ReplacePathPrefix(string value, string source, string target)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= source.Length) return value;
        if (!value.StartsWith(source, StringComparison.OrdinalIgnoreCase)) return value;
        char next = value[source.Length];
        return next is '\\' or '/' or ':' ? target + value[source.Length..] : value;
    }

    // ---------------------------------------------------------------- delete to trash

    private async Task DeleteSelectedAsync()
    {
        List<Ps5GameInfo> games = SelectedGames().ToList();
        if (games.Count == 0) return;
        // Do not modify sources a queued/running task is using.
        if (RefuseIfBusy(games)) return;

        bool permanent = _settings.PermanentDelete;
        // Permanent deletion always confirms, even when the trash prompt is turned off.
        if ((permanent || _settings.ConfirmDelete) && !await ConfirmDeleteAsync(games, permanent)) return;

        int deleted = 0;
        int skipped = 0;
        foreach (Ps5GameInfo game in games)
        {
            string path = game.RootPath;
            if (!IsUnderLibrary(path) && !IsManualSource(path)) { skipped++; continue; }

            try
            {
                if (!Directory.Exists(path) && !File.Exists(path)) { skipped++; continue; }
                TrashService.Delete(path, permanent);

                RemoveSettingPath(_settings.LibraryFolders, path);
                RemoveSettingPath(_settings.ManualSources, path);
                RemoveSettingPath(_settings.RecentFolders, path);
                _detailsCache.Remove(path);
                _games.Remove(game);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
            {
                Logger.Warn($"Delete failed for '{path}': {ex.Message}");
                skipped++;
            }
        }

        SaveSettingsQuietly();
        _stateStore.SaveManifest(_games);
        ApplyFilter();
        string verb = permanent ? "Permanently deleted" : "Moved to trash";
        Logger.Info($"{verb} {deleted:N0} source(s); skipped {skipped:N0}.");
        SetStatus($"{verb} {deleted:N0} source(s); skipped {skipped:N0}.");
    }

    /// <summary>
    /// Warns and returns true when any of the given sources is in use by a queued/running task, so a
    /// rename/move/delete cannot invalidate a running operation's source.
    /// </summary>
    private bool RefuseIfBusy(IReadOnlyList<Ps5GameInfo> games)
    {
        List<Ps5GameInfo> busy = games.Where(game => IsPathBusy(game.RootPath)).ToList();
        if (busy.Count == 0) return false;
        AppDialog.ShowWarning(
            "These sources are in use by a queued or running task and were not changed:\n\n" +
            string.Join(Environment.NewLine, busy.Select(game => "  " + game.RootPath)),
            "Source in use");
        return true;
    }

    /// <summary>True when a queued/running task reads from or writes to the given path (or under it).</summary>
    private bool IsPathBusy(string path)
    {
        foreach (QueuedPackageTask task in _taskQueue.Tasks)
        {
            if (task.Status is not (PackageTaskStatus.Queued or PackageTaskStatus.Running
                or PackageTaskStatus.Cancelling))
                continue;
            if (PathTouches(path, task.SourcePath) || PathTouches(path, task.OutputPath) ||
                PathTouches(task.SourcePath, path))
                return true;
        }
        return false;
    }

    private static bool PathTouches(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar);
        string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar);
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ConfirmDeleteAsync(IReadOnlyList<Ps5GameInfo> games, bool permanent)
    {
        const int maximumListed = 15;
        var listed = new System.Text.StringBuilder();
        foreach (string path in games.Take(maximumListed).Select(game => game.RootPath))
            listed.Append("  ").AppendLine(path);
        if (games.Count > maximumListed)
            listed.AppendLine($"  ... and {games.Count - maximumListed:N0} more");

        string caption = permanent ? "Permanently delete" : "Move to trash";
        string question = permanent
            ? $"Permanently delete {games.Count:N0} source(s)? This cannot be undone.\n\n{listed}"
            : $"Send {games.Count:N0} source(s) to the trash?\n\n{listed}";
        return await AppDialog.ConfirmAsync(question, caption) == AppDialogResult.Yes;
    }

    private static void RemoveSettingPath(List<string> paths, string value) =>
        paths.RemoveAll(path => string.Equals(path, value, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- group scope

    private IReadOnlyList<Ps5GameInfo> GroupContextGames()
    {
        TableRow? row = gridLibrary.ContextRow;
        if (row is not { IsGroup: true }) return [];
        return _visibleGames.Where(game => string.Equals(GroupKey(game), row.GroupKey,
            StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
