using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Controls;

/// <summary>One column in a <see cref="TableView"/>.</summary>
public sealed class TableColumn
{
    public required string Name { get; init; }
    public string Header { get; init; } = string.Empty;
    /// <summary>
    /// Explicit pixel width. When zero the column starts out as a share of the free width (from
    /// <see cref="Weight"/>); the first manual resize freezes every column to a pixel width so later
    /// resizing only changes the dragged column.
    /// </summary>
    public double Width { get; set; }
    /// <summary>Relative share of the free width used until a column is given an explicit width.</summary>
    public double Weight { get; set; } = 10;
    public double MinWidth { get; init; } = 40;
    public double FixedWidth { get; init; }
    public bool Visible { get; set; } = true;
    public bool RightAligned { get; init; }
    public bool IsIcon { get; init; }
}

/// <summary>One rendered row. Group rows span every column and can be collapsed.</summary>
public sealed class TableRow
{
    public required string[] Cells { get; init; }
    public object? Tag { get; init; }
    public bool IsGroup { get; init; }
    public string GroupKey { get; init; } = string.Empty;
    public int GroupCount { get; init; }
    public double? Progress { get; init; }
    public bool Error { get; init; }
    public Bitmap? Icon { get; set; }

}

public sealed class TableRowEventArgs(TableRow? row) : EventArgs
{
    public TableRow? Row { get; } = row;
}

public sealed class TableColumnEventArgs(TableColumn column, bool shift) : EventArgs
{
    public TableColumn Column { get; } = column;
    public bool Shift { get; } = shift;
}

/// <summary>
/// A lightweight, fully themed, virtualized table. The WinForms build used a heavily customized
/// DarkDataGridView with collapsible groups, per-row icons and progress cells; Avalonia has no
/// equivalent, so this control draws those rows directly. It is shared by the library, task list and
/// the read-only detail grids.
/// </summary>
public class TableView : UserControl
{
    private readonly List<TableColumn> _columns = [];
    private readonly List<TableRow> _rows = [];
    private readonly ListBox _list;
    private readonly TableHeaderView _header;
    private readonly ScrollViewer _scroll;
    private readonly DockPanel _content;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressSelection;
    private TableRow? _contextRow;
    private double _viewportWidth;
    private double _scrollbarAllowance;

    public TableView()
    {
        _header = new TableHeaderView(this);
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<TableRow>((row, _) => new TableRowView(this, row))
        };
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        _list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);
        _list.AddHandler(DoubleTappedEvent, OnListDoubleTapped, RoutingStrategies.Tunnel);
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => ScrollChanged?.Invoke(this, EventArgs.Empty));

        _content = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        _content.Children.Add(_header);
        _content.Children.Add(_list);

        // One horizontal scroller wraps the header and the rows so both move together; the list keeps
        // its own vertical scroller. Without this the header and rows would scroll independently and
        // the table would squeeze every column into the viewport.
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _content
        };
        _scroll.SizeChanged += (_, e) =>
        {
            _viewportWidth = e.NewSize.Width;
            UpdateContentWidth();
        };
        Content = _scroll;
        _list.Loaded += (_, _) =>
        {
            ScrollViewer? viewer = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (viewer is not null)
                viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        };
        // Keep the header's right edge aligned with the row content area when the list shows a
        // vertical scrollbar (the header sits outside the list's scroll viewer).
        _list.LayoutUpdated += (_, _) =>
        {
            ScrollViewer? inner = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (inner is not null && inner.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                inner.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ScrollBar? bar = _list.GetVisualDescendants().OfType<ScrollBar>()
                .FirstOrDefault(scrollBar => scrollBar.Orientation == Orientation.Vertical);
            double allowance = bar is { IsVisible: true } ? bar.Bounds.Width : 0;
            if (Math.Abs(allowance - _scrollbarAllowance) < 0.5) return;
            _scrollbarAllowance = allowance;
            _header.Margin = new Thickness(0, 0, allowance, 0);
        };
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(Layoutable.MinHeightProperty, 0d)
            }
        });
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler<TableRowEventArgs>? RowDoubleClicked;
    public event EventHandler<TableColumnEventArgs>? ColumnHeaderClicked;
    public event EventHandler<TableRowEventArgs>? RowContextRequested;
    public event EventHandler? ScrollChanged;

    public IReadOnlyList<TableColumn> Columns => _columns;
    public IReadOnlyList<TableRow> Rows => _rows;
    public bool IsGrouped { get; private set; }
    public double RowHeight { get; set; } = 22;
    public bool ShowGridLines { get; set; } = true;
    public bool ShowHeader { get; set; } = true;
    public string GroupLabel { get; set; } = "Group";
    public string EmptyText { get; set; } = string.Empty;

    /// <summary>Columns sorted in display order, with hidden ones removed.</summary>
    public IEnumerable<TableColumn> VisibleColumns =>
        _columns.Where(column => column.Visible).OrderBy(column => _columns.IndexOf(column));

    public TableRow? ContextRow => _contextRow;

    public void SetColumns(IEnumerable<TableColumn> columns)
    {
        _columns.Clear();
        _columns.AddRange(columns);
        _header.InvalidateVisual();
        UpdateContentWidth();
        RebindRows();
    }

    public TableColumn? Column(string name) =>
        _columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.Ordinal));

    /// <summary>Replaces the rows with a flat list.</summary>
    public void SetRows(IEnumerable<TableRow> rows)
    {
        IsGrouped = false;
        _rows.Clear();
        _rows.AddRange(rows);
        RebindRows();
    }

    /// <summary>
    /// Builds collapsible group rows followed by the items of each group. The keys are taken in the
    /// supplied order, so callers control the ordering (the library sorts its groups by the active
    /// column before calling this).
    /// </summary>
    public void SetGroups<T>(IEnumerable<T> items, Func<T, string> keySelector, Func<T, TableRow> rowFactory)
    {
        IsGrouped = true;
        _rows.Clear();
        var groups = new List<(string Key, List<T> Items)>();
        var index = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (T item in items)
        {
            string key = keySelector(item);
            if (!index.TryGetValue(key, out List<T>? list))
            {
                list = [];
                index[key] = list;
                groups.Add((key, list));
            }
            list.Add(item);
        }

        foreach ((string key, List<T> groupItems) in groups)
        {
            string display = string.IsNullOrWhiteSpace(key) ? "(none)" : key;
            _rows.Add(new TableRow
            {
                Cells = [$"{GroupLabel}: {display}  ({groupItems.Count})"],
                IsGroup = true,
                GroupKey = key,
                GroupCount = groupItems.Count
            });
            if (_collapsed.Contains(key)) continue;
            foreach (T item in groupItems) _rows.Add(rowFactory(item));
        }
        RebindRows();
    }

    public void Clear()
    {
        IsGrouped = false;
        _rows.Clear();
        RebindRows();
    }

    public bool ToggleGroup(string key)
    {
        bool collapsed;
        if (_collapsed.Remove(key)) collapsed = false;
        else { _collapsed.Add(key); collapsed = true; }
        return collapsed;
    }

    public bool IsGroupCollapsed(string key) => _collapsed.Contains(key);

    /// <summary>Raised with the group key when the user clicks a group header.</summary>
    public event EventHandler<string>? GroupToggled;

    /// <summary>
    /// Repaints the existing rows after their cell values changed. The item source is left alone on
    /// purpose: resetting it dropped the user's selection (and the scroll position) every time a
    /// running task reported progress.
    /// </summary>
    public void RefreshRows()
    {
        UpdateContentWidth();
        _header.InvalidateVisual();
        InvalidateRowVisuals();
    }

    /// <summary>Re-applies the item source after the row collection itself changed.</summary>
    private void RebindRows()
    {
        IReadOnlyList<TableRow> selected = SelectedRows;
        TableRow? current = _list.SelectedItem as TableRow;
        _suppressSelection = true;
        try
        {
            _list.ItemsSource = null;
            _list.ItemsSource = _rows;
            if (_list.SelectedItems is { } items)
            {
                if (current is not null && _rows.Contains(current)) items.Add(current);
                foreach (TableRow row in selected)
                    if (!items.Contains(row) && _rows.Contains(row)) items.Add(row);
            }
        }
        finally
        {
            _suppressSelection = false;
        }
        RefreshRows();
    }

    public IReadOnlyList<TableRow> SelectedRows =>
        _list.SelectedItems?.OfType<TableRow>().Where(row => !row.IsGroup).ToList() ?? [];

    public TableRow? CurrentRow =>
        _list.SelectedItem as TableRow is { IsGroup: false } row ? row : SelectedRows.FirstOrDefault();

    public void ClearSelection()
    {
        _suppressSelection = true;
        try { _list.SelectedItems?.Clear(); }
        finally
        {
            _suppressSelection = false;
            InvalidateRowVisuals();
        }
    }

    public void SelectRows(IEnumerable<TableRow> rows)
    {
        _suppressSelection = true;
        try
        {
            _list.SelectedItems?.Clear();
            foreach (TableRow row in rows)
                _list.SelectedItems?.Add(row);
        }
        finally
        {
            _suppressSelection = false;
            InvalidateRowVisuals();
        }
    }

    public void SelectGameRowsByTag(Func<object?, bool> predicate)
    {
        _suppressSelection = true;
        try
        {
            _list.SelectedItems?.Clear();
            foreach (TableRow row in _rows)
                if (!row.IsGroup && predicate(row.Tag)) _list.SelectedItems?.Add(row);
        }
        finally
        {
            _suppressSelection = false;
            InvalidateRowVisuals();
        }
    }

    public void ScrollToRow(TableRow row) => _list.ScrollIntoView(row);

    public void ScrollToIndex(int index)
    {
        if (index >= 0 && index < _rows.Count) _list.ScrollIntoView(_rows[index]);
    }

    public int IndexOfRow(TableRow row) => _rows.IndexOf(row);

    public int FirstDisplayedIndex
    {
        get
        {
            ScrollViewer? viewer = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (viewer is null) return 0;
            int index = (int)(viewer.Offset.Y / Math.Max(1, RowHeight));
            return Math.Clamp(index, 0, Math.Max(0, _rows.Count - 1));
        }
    }

    public void ScrollToOffset(int index)
    {
        ScrollViewer? viewer = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        viewer?.SetCurrentValue(ScrollViewer.OffsetProperty,
            new Vector(viewer.Offset.X, Math.Max(0, index * RowHeight)));
    }

    public void FocusGrid() => _list.Focus();

    internal double MeasureColumnWidth(TableColumn column, double available)
    {
        if (!column.Visible) return 0;
        double width = MeasureBaseColumnWidth(column, available);

        // The last visible column absorbs any leftover viewport width, so the table always reaches
        // the right edge instead of leaving dead space. When the columns already overflow the
        // viewport it keeps its own width and the table scrolls.
        TableColumn? last = _columns.LastOrDefault(candidate => candidate.Visible);
        if (!ReferenceEquals(column, last) || column.FixedWidth > 0) return width;
        double layoutWidth = (_viewportWidth > 0 ? _viewportWidth : available) - _scrollbarAllowance;
        if (layoutWidth <= 0) return width;
        double others = _columns
            .Where(candidate => candidate.Visible && !ReferenceEquals(candidate, column))
            .Sum(candidate => MeasureBaseColumnWidth(candidate, available));
        return Math.Max(width, layoutWidth - others);
    }

    private double MeasureBaseColumnWidth(TableColumn column, double available)
    {
        if (column.FixedWidth > 0) return column.FixedWidth;
        if (column.Width > 0) return Math.Max(column.MinWidth, column.Width);
        // Auto-sized columns are always measured against the viewport, so the header and every row
        // agree even though the rows lose a few pixels to the vertical scrollbar.
        double layoutWidth = _viewportWidth > 0 ? _viewportWidth : available;
        double fixedTotal = _columns.Where(c => c.Visible && c.FixedWidth > 0).Sum(c => c.FixedWidth);
        double weightTotal = _columns.Where(c => c.Visible && c.FixedWidth <= 0).Sum(c => Math.Max(0.1, c.Weight));
        double free = Math.Max(0, layoutWidth - fixedTotal);
        if (weightTotal <= 0) return 0;
        return Math.Max(column.MinWidth, free * (Math.Max(0.1, column.Weight) / weightTotal));
    }

    /// <summary>Total width of the visible columns at the current layout.</summary>
    private double TotalColumnWidth()
    {
        double available = _viewportWidth > 0 ? _viewportWidth : Math.Max(0, Bounds.Width);
        return _columns.Where(column => column.Visible).Sum(column => MeasureColumnWidth(column, available));
    }

    /// <summary>Sizes the shared header/row surface, growing it past the viewport for horizontal scrolling.</summary>
    private void UpdateContentWidth()
    {
        double viewport = _viewportWidth > 0 ? _viewportWidth : Math.Max(0, Bounds.Width);
        if (viewport <= 0) return;
        double width = Math.Max(viewport, TotalColumnWidth());
        if (!double.IsFinite(width) || width <= 0) return;
        if (Math.Abs(_content.Width - width) < 0.5) return;
        _content.Width = width;
        _header.InvalidateVisual();
        InvalidateRowVisuals();
    }

    private void InvalidateRowVisuals()
    {
        foreach (TableRowView row in _list.GetVisualDescendants().OfType<TableRowView>())
            row.InvalidateVisual();
    }

    internal double RowPixelHeight => Math.Max(14, RowHeight);

    internal bool IsSelected(TableRow row) =>
        _list.SelectedItems?.Contains(row) == true;

    private void OnSelectionChanged()
    {
        // Rows draw their own selection highlight, so they must be repainted when the selection
        // moves; otherwise the previously selected row keeps its blue background.
        InvalidateRowVisuals();
        if (_suppressSelection) return;
        // Group rows are never part of the selection: drop them if the list added one.
        var groups = _list.SelectedItems?.OfType<TableRow>().Where(row => row.IsGroup).ToList();
        if (groups is { Count: > 0 })
        {
            _suppressSelection = true;
            try
            {
                foreach (TableRow row in groups) _list.SelectedItems?.Remove(row);
            }
            finally { _suppressSelection = false; }
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_list).Properties.IsRightButtonPressed)
        {
            _contextRow = RowAt(e);
            if (_contextRow is { IsGroup: false } && _list.SelectedItems?.Contains(_contextRow) != true)
            {
                _suppressSelection = true;
                try
                {
                    _list.SelectedItems?.Clear();
                    _list.SelectedItems?.Add(_contextRow);
                }
                finally { _suppressSelection = false; }
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        TableRow? row = RowAt(e);
        if (row is { IsGroup: true })
        {
            e.Handled = true;
            ToggleGroup(row.GroupKey);
            GroupToggled?.Invoke(this, row.GroupKey);
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!e.GetCurrentPoint(_list).Properties.IsRightButtonPressed) return;
        if (_contextRow is null) return;
        e.Handled = true;
        RowContextRequested?.Invoke(this, new TableRowEventArgs(_contextRow));
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        TableRow? row = RowAt(e);
        if (row is null) return;
        if (row.IsGroup)
        {
            ToggleGroup(row.GroupKey);
            GroupToggled?.Invoke(this, row.GroupKey);
            return;
        }
        RowDoubleClicked?.Invoke(this, new TableRowEventArgs(row));
    }

    private TableRow? RowAt(RoutedEventArgs e)
    {
        if (e.Source is not Visual visual) return null;
        ListBoxItem? item = visual.FindAncestorOfType<ListBoxItem>();
        return item?.DataContext as TableRow;
    }

    internal void RaiseHeaderClicked(TableColumn column, bool shift) =>
        ColumnHeaderClicked?.Invoke(this, new TableColumnEventArgs(column, shift));

    /// <summary>
    /// Sets one column's pixel width. The first manual resize freezes every auto-sized column so the
    /// width change is local; the table then scrolls horizontally instead of shrinking its neighbours.
    /// </summary>
    internal void SetColumnWidth(TableColumn column, double newWidth)
    {
        if (column.FixedWidth > 0) return;
        FreezeAutoWidths();
        column.Width = Math.Clamp(newWidth, column.MinWidth, 4000);
        UpdateContentWidth();
        RefreshColumnLayout();
    }

    private void FreezeAutoWidths()
    {
        double available = _viewportWidth > 0 ? _viewportWidth : Math.Max(0, Bounds.Width);
        if (available <= 0) return;
        foreach (TableColumn column in _columns)
            if (column.Visible && column.FixedWidth <= 0 && column.Width <= 0)
                column.Width = MeasureColumnWidth(column, available);
    }

    internal void RaiseColumnResized(TableColumn column) =>
        ColumnResized?.Invoke(this, new TableColumnEventArgs(column, false));

    /// <summary>Repaints the header and every realized row after a column width/visibility change.</summary>
    internal void RefreshColumnLayout()
    {
        _header.InvalidateVisual();
        InvalidateVisual();
        _content.InvalidateMeasure();
        InvalidateRowVisuals();
    }

    public event EventHandler<TableColumnEventArgs>? ColumnResized;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
        _header.InvalidateVisual();
        _list.InvalidateVisual();
    }
}

/// <summary>Draws the column captions, sort arrows and the resize grips.</summary>
internal sealed class TableHeaderView : Control
{
    private readonly TableView _owner;
    private TableColumn? _resizing;
    private double _resizeStartX;
    private double _resizeStartWidth;

    public TableHeaderView(TableView owner)
    {
        _owner = owner;
        Height = 24;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public override void Render(DrawingContext context)
    {
        ThemePalette theme = ThemeManager.Active;
        var background = new SolidColorBrush(theme.MediumBackground);
        var border = new SolidColorBrush(theme.DarkBorder);
        var textBrush = new SolidColorBrush(theme.LightText);
        context.FillRectangle(background, new Rect(Bounds.Size));
        context.DrawLine(new Pen(border, 1), new Point(0, Bounds.Height - 0.5),
            new Point(Bounds.Width, Bounds.Height - 0.5));

        double x = 0;
        foreach (TableColumn column in _owner.VisibleColumns)
        {
            double width = _owner.MeasureColumnWidth(column, Bounds.Width);
            context.DrawLine(new Pen(border, 1), new Point(x + width - 0.5, 0),
                new Point(x + width - 0.5, Bounds.Height));
            var formatted = new FormattedText(column.Header, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, textBrush)
            {
                MaxTextWidth = Math.Max(0, width - 12),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            context.DrawText(formatted, new Point(x + 6, (Bounds.Height - formatted.Height) / 2));
            x += width;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        double x = 0;
        Point point = e.GetPosition(this);
        foreach (TableColumn column in _owner.VisibleColumns)
        {
            double width = _owner.MeasureColumnWidth(column, Bounds.Width);
            if (Math.Abs(point.X - (x + width)) <= 5)
            {
                // Fixed-width columns (the icon) have no weight to resize.
                if (column.FixedWidth > 0) return;
                _resizing = column;
                _resizeStartX = point.X;
                _resizeStartWidth = width;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            x += width;
        }

        x = 0;
        foreach (TableColumn column in _owner.VisibleColumns)
        {
            double width = _owner.MeasureColumnWidth(column, Bounds.Width);
            if (point.X >= x && point.X < x + width)
            {
                bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                _owner.RaiseHeaderClicked(column, shift);
                e.Handled = true;
                return;
            }
            x += width;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_resizing is null) return;
        double delta = e.GetPosition(this).X - _resizeStartX;
        if (Math.Abs(delta) < 1) return;
        _owner.SetColumnWidth(_resizing, _resizeStartWidth + delta);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_resizing is not null)
        {
            _owner.RaiseColumnResized(_resizing);
            _resizing = null;
            e.Pointer.Capture(null);
            _owner.RefreshColumnLayout();
        }
    }
}

/// <summary>Draws one row, including the group header, icon, progress and missing-source colour.</summary>
internal sealed class TableRowView : Control
{
    private readonly TableView _owner;
    private readonly TableRow _row;

    public TableRowView(TableView owner, TableRow row)
    {
        _owner = owner;
        _row = row;
        Height = owner.RowPixelHeight;
        MinHeight = owner.RowPixelHeight;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        ThemePalette theme = ThemeManager.Active;
        bool selected = !_row.IsGroup && _owner.IsSelected(_row);
        var lightText = new SolidColorBrush(theme.LightText);
        var selectionText = new SolidColorBrush(theme.SelectionText);
        var border = new Pen(new SolidColorBrush(theme.DarkBorder), 1);

        if (_row.IsGroup)
        {
            context.FillRectangle(new SolidColorBrush(theme.MediumBackground), new Rect(Bounds.Size));
            bool collapsed = false;
            var formatted = new FormattedText(_row.Cells.FirstOrDefault() ?? string.Empty,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 12, lightText)
            {
                MaxTextWidth = Math.Max(0, Bounds.Width - 24),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            _ = collapsed;
            context.DrawText(formatted, new Point(20, (Bounds.Height - formatted.Height) / 2));
            context.DrawLine(border, new Point(0, Bounds.Height - 0.5), new Point(Bounds.Width, Bounds.Height - 0.5));
            return;
        }

        if (selected)
            context.FillRectangle(new SolidColorBrush(theme.BlueSelection), new Rect(Bounds.Size));

        IBrush textBrush = selected ? selectionText : _row.Error ? new SolidColorBrush(Color.FromRgb(208, 128, 128)) : lightText;
        double x = 0;
        // Cells are authored for every declared column, in declaration order. Hidden columns keep
        // their slot here, otherwise their removal would shift every following cell under the
        // wrong header.
        for (int cellIndex = 0; cellIndex < _owner.Columns.Count; cellIndex++)
        {
            TableColumn column = _owner.Columns[cellIndex];
            if (!column.Visible) continue;
            double width = _owner.MeasureColumnWidth(column, Bounds.Width);
            if (column.IsIcon)
            {
                if (_row.Icon is Bitmap icon)
                {
                    double size = Math.Min(Bounds.Height - 4, width - 8);
                    context.DrawImage(icon, new Rect(x + (width - size) / 2, (Bounds.Height - size) / 2, size, size));
                }
            }
            else
            {
                string text = cellIndex < _row.Cells.Length ? _row.Cells[cellIndex] : string.Empty;
                if (_row.Progress is double progress && string.Equals(column.Name, "Progress", StringComparison.Ordinal))
                {
                    DrawProgress(context, theme, x, width, progress, selected);
                }
                else if (text.Length > 0)
                {
                    var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, Typeface.Default, 12, textBrush)
                    {
                        MaxTextWidth = Math.Max(0, width - 10),
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = column.RightAligned ? TextAlignment.Right : TextAlignment.Left
                    };
                    double textX = column.RightAligned ? x + 5 : x + 5;
                    context.DrawText(formatted, new Point(textX, (Bounds.Height - formatted.Height) / 2));
                }
            }
            if (_owner.ShowGridLines)
                context.DrawLine(border, new Point(x + width - 0.5, 0), new Point(x + width - 0.5, Bounds.Height));
            x += width;
        }

        if (_owner.ShowGridLines)
            context.DrawLine(border, new Point(0, Bounds.Height - 0.5), new Point(Bounds.Width, Bounds.Height - 0.5));
    }

    private static void DrawProgress(DrawingContext context, ThemePalette theme, double x, double width,
        double progress, bool selected)
    {
        var track = new Rect(x + 4, 4, Math.Max(0, width - 8), Math.Max(4, 14));
        context.FillRectangle(new SolidColorBrush(theme.DarkBackground), track);
        double clamped = Math.Clamp(progress, 0, 1);
        context.FillRectangle(new SolidColorBrush(selected ? theme.LightestBackground : theme.BlueSelection),
            new Rect(track.X, track.Y, track.Width * clamped, track.Height));
    }
}
