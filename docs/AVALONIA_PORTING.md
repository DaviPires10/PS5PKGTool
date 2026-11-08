# Avalonia porting guide

The port is complete: the former WinForms front end has been removed, and every window is now an
Avalonia view under `PS5PKGTool/Views/`. The `PS5PKGTool.Core` engine is unchanged. This guide is kept
as the reference for the port's architecture and for future UI work.

The only supported build command is:

```
dotnet build PS5PKGTool/PS5PKGTool.csproj -c Debug
```

## Architecture

| Area | Where |
| --- | --- |
| App bootstrap, single instance, theme load | `App.axaml`, `App.axaml.cs`, `Program.cs` |
| Main window XAML (all panes/tabs/grids/menus) | `Views/MainWindow.axaml` |
| Main window shell, scanning, settings, helpers | `Views/MainWindow.axaml.cs` |
| Per-area code-behind | `Views/MainWindow.<Area>.cs` (partial `MainWindow`) |
| Grid control | `Controls/TableView.cs` |
| Multi-select filter control | `Controls/CheckedComboBox.cs` |
| Search field / chip list / section frame | `Controls/SearchBox.cs`, `ChipsPanel.cs`, `SectionPanel.cs` |
| Message boxes | `Views/AppDialog.cs` |
| Themes | `Infrastructure/ThemeManager.cs`, `ThemePalette.cs`, `ThemeCatalog.cs`, `Themes/DarkTheme.axaml` |
| Settings/manifest persistence | `Infrastructure/AppStateStore.cs` |
| Log file | `Infrastructure/Logger.cs` |
| Cross-platform shell open/reveal | `Infrastructure/PlatformService.cs` |
| Cross-platform trash (no native calls) | `Infrastructure/TrashService.cs` |
| Settings dialog | `Views/SettingsWindow.cs` |
| exFAT/FFPKG image editors | `Views/ImageEditorWindow.cs`, `ExfatEditorWindow.cs`, `FfpkgEditorWindow.cs` |

The window is built in code-behind: the XAML only declares and names controls. **Never add `Click=`,
`SelectionChanged=` or other event attributes to the XAML.** Subscribe in code-behind instead. This
keeps the XAML independent of every partial file.

## Control-name contract

Every control keeps the WinForms designer name (`gridOverview`, `splitTasks`, `txtOutExfat`,
`menuLibraryRename`, ...). Avalonia generates a field for each `x:Name`; all partials of
`MainWindow` can use them directly. **Do not rename controls** and do not edit
`Views/MainWindow.axaml` unless your task explicitly owns it. If a control is genuinely missing,
stop and report the name instead of adding a second copy.

WinForms → Avalonia mapping:

| WinForms (DarkUI) | Avalonia |
| --- | --- |
| `DarkForm` / `DarkDialog` | `Window` / `ShowDialog` |
| `DarkDataGridView` | `TableView` |
| `DarkTextBox` | `TextBox` |
| `DarkComboBox` | `ComboBox` (`Items` are strings unless noted) |
| `DarkCheckedComboBox` | `CheckedComboBox` |
| `DarkCheckBox` | `CheckBox` (`IsChecked` is `bool?`) |
| `DarkButton` | `Button` |
| `DarkLabel` | `TextBlock` |
| `DarkNumericUpDown` | `NumericUpDown` (`Value` is `decimal?`) |
| `DarkSectionPanel` | `SectionPanel` (`Header`, `HeaderNote`) |
| `DarkTabControl` / `DarkTabPage` | `TabControl` / `TabItem` |
| `DarkSplitContainer` | `Grid` + `GridSplitter` |
| `DarkStatusStrip` | status bar `Border` in the main XAML |
| `DarkTreeView` | `TreeView` with `TreeViewItem`s |
| `DarkListView` | `TableView` |
| `DarkRichTextBox` | `TextBox` (read-only) or the log `ListBox` |
| `ToolStripMenuItem` | `MenuItem` (`Header`, `Click`, `IsChecked`, `IsEnabled`) |
| `PictureBox` | `Image` (`Source` is an `Avalonia.Media.Imaging.Bitmap`) |
| `MediaElement` (WPF) | `LibVLCSharp.Avalonia.VideoView` (`mediaFileViewer`) |
| `DataTable`/`DataView`-bound grids | `TableView.SetColumns` + `SetRows` |

## TableView

```csharp
gridOverview.SetColumns([
    new TableColumn { Name = "Property", Header = "Property", Weight = 28 },
    new TableColumn { Name = "Value", Header = "Value", Weight = 72 }
]);

gridOverview.SetRows(items.Select(item => new TableRow
{
    Cells = [item.Property, item.Value],
    Tag = item,
    Icon = null,        // optional Bitmap for the first column
    Progress = 0.5,     // optional 0..1 progress bar in the progress column
    Error = false
}));
```

`Weight` is only the initial share of the available width. Give a column an explicit `Width` for a
fixed pixel width (the library does this for every column). The first manual resize freezes any
auto-sized columns to their current pixel widths, so resizing one column never changes the others;
when the total exceeds the viewport, the header and rows scroll horizontally together.

Grouping (library):

```csharp
gridLibrary.SetGroups(games, game => GroupKey(game), FillLibraryRow);
// or gridLibrary.SetRows(games.Select(FillLibraryRow));
gridLibrary.GroupLabel = "Group";
```

Selection and events:

- `SelectionChanged` — no arguments; read `SelectedRows` / `CurrentRow`.
- `RowDoubleClicked` — `TableRowEventArgs.Row`.
- `ColumnHeaderClicked` — `TableColumnEventArgs.Column`; update sort indicators yourself.
- `RowContextRequested` — `TableRowEventArgs.Row`; set the row as context target, then let the
  `ContextMenu` declared in XAML open. Context menus are already attached to the grid controls.
- `SelectedRows` excludes group rows. `Tag` carries your model object.
- `ClearSelection()`, `SelectRows(rows)`, `SelectGameRowsByTag(predicate)`.
- `Rows` is the current rendered list (group rows included; check `row.IsGroup`).
- `RowHeight`, `ShowGridLines`, `ShowHeader`, `EmptyText` are plain CLR properties.

`gridLibrary` rows carry `Tag = Ps5GameInfo`; detail tables use their own row types or dictionaries.

## Other custom controls

`CheckedComboBox`:

```csharp
cboFilterCategory.AddItems(["Game", "Patch", "DLC", "App"]);
cboFilterCategory.Placeholder = "Any";
cboFilterCategory.CheckedItemsChanged += (_, _) => ApplyFilter();
IReadOnlyList<string> selected = cboFilterCategory.CheckedItems;
cboFilterCategory.SetItemChecked("DLC", true);
cboFilterCategory.SetAllItemsChecked(false);
```

`SearchBox` derives from `TextBox`; use `SearchText` (never null) and the normal `TextChanged`
event. `SearchBox.Watermark` sets the placeholder.

`ChipsPanel.AddChip(text, clicked)` adds a removable chip; `Chips` lists them.

`SectionPanel` is a `ContentControl` with `Header`/`HeaderNote` strings.

## Dialogs

- Message boxes: `await AppDialog.ShowInfoAsync/ShowWarningAsync/ShowErrorAsync/ShowAsync`; use
  `AppDialog.ConfirmAsync` and compare with `AppDialogResult.Yes`. Fire-and-forget variants
  (`AppDialog.ShowInfo(...)`) exist for synchronous call sites.
- Text prompt: `await TextPromptWindow.AskAsync(this, title, prompt, initial)` (null on cancel).
- Reports: `ShowTextReport(title, body)` in `MainWindow`.
- New dialogs are code-only `Window`s with a static `ShowAsync(Window owner, ...)`. Set
  `WindowStartupLocation = CenterOwner`, `ShowInTaskbar = false`, and return via
  `ShowDialog<T>(owner)` / `Close(value)`.

## Platform services

Never call `explorer.exe` or `Clipboard.SetText` directly.

- Open a folder/file/URL: `PlatformService.OpenPath`, `PlatformService.OpenUrl`.
- Reveal a file: `PlatformService.RevealPath`.
- Clipboard: `CopyText(value)` from `MainWindow` (already status-aware), or `TopLevel.GetTopLevel(this)?.Clipboard`.
- File/folder pickers: the `MainWindow` helpers `PickFolderAsync`, `PickFileAsync`,
  `PickSaveFileAsync` use Avalonia `StorageProvider` and return local paths or null.
- Settings paths use `Environment.SpecialFolder.LocalApplicationData`; never hard-code `%AppData%`.
- Deletion uses `TrashService`, which only uses managed APIs: freedesktop trash on Linux and a
  per-user trash folder on macOS/Windows. There is no registry, shell32 or `explorer.exe` code.

## Threading

- Core scans/builds run on background threads. Marshal UI work with `Dispatcher.UIThread.Post`,
  `RunOnUi(...)`, or a `Progress<T>` created on the UI thread.
- Cancellation: `_scanCancellation`, `_detailCancellation`, `_fileCancellation`,
  `_filePreviewCancellation` are shared and cancelled by the shell key handlers.
- `Logger.Info/Warn/Error/Exception` is the file log; `InitializeLog` also feeds the Log tab.

## Shared members already provided by `MainWindow.axaml.cs`

State: `_stateStore`, `_scanner`, `_detailsLoader`, `_detailsCache`, `_taskQueue`, `_settings`,
`_games`, `_visibleGames`, `_selectedGame`, `_currentGameRoot`, `_currentSourceIsContainer`,
`_isScanning`, `_isFileBusy`, `_libraryColumnsReady`, `_hexPreviewOffset`, `_previewDirectory`.

Helpers: `SetStatus`, `SetScanning`, `UpdateOperationState`, `SetSelectedGame`,
`SourceExists`, `FormatBytes`, `CategoryOf`, `RegionOf`, `MakeSafeFileName`,
`IsFfpfscOperationException`, `TryDeleteFile`, `TryDeleteDirectory`, `FindAvailableDirectory`,
`AddRecentFolder`, `ScanRoots`, `IsUnderLibrary`, `IsManualSource`, `RememberManualSource`,
`SaveSettingsQuietly`, `TrySaveSettings`, `CopyText`, `PromptTextAsync`, `ShowTextReport`,
`RunOnUi`, `OpenOutputFolder`, `PickFolderAsync`, `PickFileAsync`, `PickSaveFileAsync`,
`OpenExternalUrl`, `ScanAsync`, `ApplyFilter` (filters partial), `ClearDetails` (details partial).

Cross-area contracts that must keep their exact signature:

```csharp
private QueuedPackageTask EnqueueTask(string type, string displayName,
    Func<IProgress<PackageTaskProgress>, CancellationToken, Task> execute,
    string sourcePath = "", string outputPath = "", Action<QueuedPackageTask>? onFinished = null,
    string? payload = null, string operation = "", string sourceFormat = "", string targetFormat = "",
    PackageTaskStage[]? stagePlan = null, string targetQualifier = "");   // MainWindow.Tasks.cs
```

Settings dialog:

```csharp
Task<SettingsDialogResult?> SettingsWindow.ShowAsync(Window owner, AppSettings settings,
    Func<AppSettings, (bool Success, string? Error)> save);
// SettingsDialogResult(AppSettings Settings, bool ResetLayout, bool ClearCaches)
```

## Behaviour parity rules

1. Preserve every user-visible string, confirmation, warning and log message from the WinForms
   implementation unless it mentions an OS-specific path or UI toolkit.
2. Preserve defaults, validation, overwrite prompts, cancellation points and restore-on-failure
   behaviour. Read the original method before rewriting it.
3. Do not silently drop a feature because Avalonia lacks the exact control; implement the closest
   equivalent (e.g. hex preview in a monospace `TextBox`, thumbnails as `Image` rows).
4. Keep expensive work off the UI thread and report progress in `statusLabel` / task rows.
5. Everything must work on Windows, Linux and macOS. Use `PlatformService`, `Path.Combine`,
   `Environment.SpecialFolder`, and forward slashes in archive/inner paths.
6. Theme every new window with the active palette; prefer `{DynamicResource Theme.*}` in XAML or
   `ThemeManager.Active` brushes in code.
7. No WinForms types, no `System.Drawing`, no `explorer.exe`, and no `Microsoft.Win32`, P/Invoke or
   other Windows-only API in the UI projects.
