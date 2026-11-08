using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace PS5PKGTool.Controls;

/// <summary>
/// Drop-down with check boxes, the Avalonia counterpart of DarkUI's DarkCheckedComboBox. The button
/// shows a summary of the checked items; the flyout hosts the list.
/// </summary>
public sealed class CheckedComboBox : Button
{
    private readonly List<string> _items = [];
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _caption = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly StackPanel _itemPanel = new() { Spacing = 2 };
    private readonly Border _flyoutBorder;
    private readonly Flyout _flyout;
    private bool _suppress;
    private string _placeholder = "Any";

    public CheckedComboBox()
    {
        Content = _caption;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(7, 3, 18, 3);
        MinHeight = 24;
        _flyoutBorder = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Child = new ScrollViewer
            {
                MaxHeight = 320,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _itemPanel
            }
        };
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = _flyoutBorder
        };
        ApplyTheme();
        Infrastructure.ThemeManager.ThemeChanged += (_, _) => ApplyTheme();
        Click += (_, _) => _flyout.ShowAt(this);
    }

    private void ApplyTheme()
    {
        _flyoutBorder.Background = new SolidColorBrush(Infrastructure.ThemeManager.Active.MediumBackground);
        _flyoutBorder.BorderBrush = new SolidColorBrush(Infrastructure.ThemeManager.Active.DarkBorder);
        UpdateCaption();
    }

    /// <summary>Raised whenever the checked set changes.</summary>
    public event EventHandler? CheckedItemsChanged;

    /// <summary>The selectable values, in display order.</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>The values that are currently checked.</summary>
    public IReadOnlyList<string> CheckedItems
    {
        get
        {
            var result = new List<string>();
            foreach (string item in _items)
                if (_checked.Contains(item)) result.Add(item);
            return result;
        }
    }

    /// <summary>Caption used while nothing is checked.</summary>
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            _placeholder = value;
            UpdateCaption();
        }
    }

    public void AddItems(params string[] items)
    {
        foreach (string item in items)
            if (!_items.Contains(item, StringComparer.OrdinalIgnoreCase))
                _items.Add(item);
        RebuildItems();
        UpdateCaption();
    }

    public void ClearItems()
    {
        _items.Clear();
        _checked.Clear();
        RebuildItems();
        UpdateCaption();
    }

    public void SetAllItemsChecked(bool value)
    {
        _suppress = true;
        try
        {
            _checked.Clear();
            if (value) foreach (string item in _items) _checked.Add(item);
            foreach (Control child in _itemPanel.Children)
                if (child is CheckBox box) box.IsChecked = value;
        }
        finally
        {
            _suppress = false;
        }
        UpdateCaption();
        CheckedItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetItemChecked(int index, bool value)
    {
        if (index < 0 || index >= _items.Count) return;
        string item = _items[index];
        bool changed = value ? _checked.Add(item) : _checked.Remove(item);
        if (changed && index < _itemPanel.Children.Count && _itemPanel.Children[index] is CheckBox box)
            box.IsChecked = value;
        UpdateCaption();
        CheckedItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetItemChecked(string value, bool checkedState)
    {
        int index = _items.FindIndex(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) SetItemChecked(index, checkedState);
    }

    private void RebuildItems()
    {
        _itemPanel.Children.Clear();
        for (int index = 0; index < _items.Count; index++)
        {
            string item = _items[index];
            var box = new CheckBox
            {
                Content = item,
                IsChecked = _checked.Contains(item),
                MinWidth = 150,
                Margin = new Thickness(2, 1)
            };
            box.IsCheckedChanged += (_, _) =>
            {
                if (_suppress) return;
                if (box.IsChecked == true) _checked.Add(item);
                else _checked.Remove(item);
                UpdateCaption();
                CheckedItemsChanged?.Invoke(this, EventArgs.Empty);
            };
            _itemPanel.Children.Add(box);
        }
    }

    private void UpdateCaption()
    {
        IReadOnlyList<string> checkedItems = CheckedItems;
        if (checkedItems.Count == 0)
        {
            _caption.Text = _placeholder;
            _caption.Foreground = new SolidColorBrush(Infrastructure.ThemeManager.Active.DisabledText);
        }
        else
        {
            _caption.Text = string.Join(", ", checkedItems);
            _caption.Foreground = new SolidColorBrush(Infrastructure.ThemeManager.Active.LightText);
        }
    }
}
