using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

/// <summary>Avalonia replacement for the WinForms <c>UpdateAvailableForm</c>.</summary>
internal sealed class UpdateAvailableWindow : Window
{
    private UpdateAvailableWindow(UpdateCheck check, string current)
    {
        Title = "Update available";
        Width = 680;
        Height = 520;
        MinWidth = 420;
        MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(ThemeManager.Active.GreyBackground);

        var heading = new TextBlock
        {
            Text = $"PS5 PKG Tool {check.Latest} is available",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(ThemeManager.Active.LightText)
        };
        var currentLine = new TextBlock
        {
            Text = $"You are running {current}.",
            Foreground = new SolidColorBrush(ThemeManager.Active.DisabledText)
        };

        var notes = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(check.Notes) ? "(No release notes were published.)" : check.Notes,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        ScrollViewer.SetVerticalScrollBarVisibility(notes, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var open = new Button { Content = "Open release page", Width = 150, IsDefault = true };
        var later = new Button { Content = "Later", Width = 96, IsCancel = true };
        open.Click += (_, _) => Close(true);
        later.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { open, later }
        };

        var panel = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(heading);
        header.Children.Add(currentLine);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        header.Margin = new Thickness(0, 0, 0, 10);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(header);
        panel.Children.Add(buttons);
        panel.Children.Add(notes);
        Content = panel;
    }

    /// <summary>Returns true when the user wants the release page opened.</summary>
    public static Task<bool> ShowAsync(Window owner, UpdateCheck check, string current)
    {
        var dialog = new UpdateAvailableWindow(check, current);
        return dialog.ShowDialog<bool>(owner);
    }
}
