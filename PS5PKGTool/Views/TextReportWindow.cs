using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace PS5PKGTool.Views;

/// <summary>Avalonia replacement for the WinForms <c>TextReportForm</c>.</summary>
internal sealed class TextReportWindow : Window
{
    private TextReportWindow(string title, string body)
    {
        Title = title;
        Width = 940;
        Height = 640;
        MinWidth = 480;
        MinHeight = 320;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Infrastructure.ThemeManager.Active.GreyBackground);

        var text = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var copy = new Button { Content = "Copy", Width = 96 };
        var close = new Button { Content = "Close", Width = 96, IsDefault = true, IsCancel = true };
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(body);
        };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { copy, close }
        };

        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(buttons);
        panel.Children.Add(text);
        Content = panel;
    }

    public static Task ShowAsync(Window owner, string title, string body)
    {
        var dialog = new TextReportWindow(title, body);
        return dialog.ShowDialog(owner);
    }
}
