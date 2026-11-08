using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace PS5PKGTool.Controls;

/// <summary>
/// Wrapping strip of removable filter chips, the Avalonia counterpart of DarkUI's DarkChipsPanel.
/// </summary>
public sealed class ChipsPanel : UserControl
{
    private readonly WrapPanel _panel = new()
    {
        Orientation = Orientation.Horizontal
    };

    public ChipsPanel()
    {
        Padding = new Thickness(2);
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _panel
        };
    }

    public IReadOnlyList<Control> Chips => _panel.Children;

    public void AddChip(string text, EventHandler? clicked)
    {
        var button = new Button
        {
            Content = text + "  ×",
            Classes = { "chip" }
        };
        button.Click += (sender, args) =>
        {
            clicked?.Invoke(button, EventArgs.Empty);
            // Callers rebuild the strip in their handler; if they do not, remove it here.
            if (_panel.Children.Contains(button)) _panel.Children.Remove(button);
        };
        _panel.Children.Add(button);
    }

    public void ClearChips() => _panel.Children.Clear();
}
