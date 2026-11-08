using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PS5PKGTool.Views;

/// <summary>
/// Avalonia replacement for the WinForms <c>TextPromptForm</c>. Returns the trimmed text, or null
/// when the user cancels, so callers can keep the original "cancel means no change" behaviour.
/// </summary>
internal sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    private TextPromptWindow(string title, string prompt, string initial, bool multiline)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Infrastructure.ThemeManager.Active.GreyBackground);

        _input = new TextBox
        {
            Text = initial,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 120 : 28,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };

        var ok = new Button { Content = "OK", Width = 88, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 88, IsCancel = true };
        ok.Click += (_, _) => Close(_input.Text?.Trim() ?? string.Empty);
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { ok, cancel }
        };

        var body = new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Infrastructure.ThemeManager.Active.LightText)
        };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(body);
        panel.Children.Add(_input);
        panel.Children.Add(buttons);
        Content = panel;
        Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    public static Task<string?> AskAsync(Window owner, string title, string prompt, string initial,
        bool multiline = false)
    {
        var dialog = new TextPromptWindow(title, prompt, initial, multiline);
        return dialog.ShowDialog<string?>(owner);
    }
}
