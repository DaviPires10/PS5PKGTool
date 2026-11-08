using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PS5PKGTool.Controls;

namespace PS5PKGTool.Views;

public enum AppDialogResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No
}

public enum AppDialogButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
    Close
}

public enum AppDialogIcon
{
    None,
    Information,
    Warning,
    Error,
    Question
}

/// <summary>
/// The Avalonia replacement for the custom WinForms message box. Dialog results keep the same meaning
/// as the WinForms <c>DialogResult</c> values used by the original call sites.
/// </summary>
public static class AppDialog
{
    public static async Task<AppDialogResult> ShowAsync(string message, string title = "PS5 PKG Tool",
        AppDialogButtons buttons = AppDialogButtons.Ok, AppDialogIcon icon = AppDialogIcon.None)
    {
        var dialog = new AppDialogWindow(message, title, buttons, icon);
        Window? owner = Owner;
        if (owner is null)
        {
            dialog.Show();
            return await dialog.Completion;
        }
        return await dialog.ShowDialog<AppDialogResult>(owner);
    }

    public static Task<AppDialogResult> ShowInfoAsync(string message, string title = "PS5 PKG Tool") =>
        ShowAsync(message, title, AppDialogButtons.Ok, AppDialogIcon.Information);

    public static Task<AppDialogResult> ShowWarningAsync(string message, string title = "PS5 PKG Tool") =>
        ShowAsync(message, title, AppDialogButtons.Ok, AppDialogIcon.Warning);

    public static Task<AppDialogResult> ShowErrorAsync(string message, string title = "PS5 PKG Tool error") =>
        ShowAsync(message, title, AppDialogButtons.Ok, AppDialogIcon.Error);

    public static Task<AppDialogResult> ConfirmAsync(string message, string title = "PS5 PKG Tool") =>
        ShowAsync(message, title, AppDialogButtons.YesNo, AppDialogIcon.Question);

    /// <summary>Fire-and-forget variant for synchronous handlers and last-chance error reporting.</summary>
    public static void Show(string message, string title = "PS5 PKG Tool",
        AppDialogButtons buttons = AppDialogButtons.Ok, AppDialogIcon icon = AppDialogIcon.None)
    {
        Dispatcher.UIThread.Post(() => _ = ShowAsync(message, title, buttons, icon));
    }

    public static void ShowInfo(string message, string title = "PS5 PKG Tool") =>
        Show(message, title, AppDialogButtons.Ok, AppDialogIcon.Information);

    public static void ShowWarning(string message, string title = "PS5 PKG Tool") =>
        Show(message, title, AppDialogButtons.Ok, AppDialogIcon.Warning);

    public static void ShowError(string message, string title = "PS5 PKG Tool error") =>
        Show(message, title, AppDialogButtons.Ok, AppDialogIcon.Error);

    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

internal sealed class AppDialogWindow : Window
{
    private readonly TaskCompletionSource<AppDialogResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AppDialogWindow(string message, string title, AppDialogButtons buttons, AppDialogIcon icon)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 320;

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        AddButton(buttonPanel, "OK", AppDialogResult.Ok, buttons is AppDialogButtons.Ok or AppDialogButtons.OkCancel, true);
        AddButton(buttonPanel, "Yes", AppDialogResult.Yes,
            buttons is AppDialogButtons.YesNo or AppDialogButtons.YesNoCancel, buttons is AppDialogButtons.YesNo);
        AddButton(buttonPanel, "No", AppDialogResult.No,
            buttons is AppDialogButtons.YesNo or AppDialogButtons.YesNoCancel, false);
        AddButton(buttonPanel, "Cancel", AppDialogResult.Cancel,
            buttons is AppDialogButtons.OkCancel or AppDialogButtons.YesNoCancel, false);
        AddButton(buttonPanel, "Close", AppDialogResult.Cancel, buttons is AppDialogButtons.Close, true);

        var content = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        content.Children.Add(buttonPanel);

        var text = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            Foreground = new SolidColorBrush(Infrastructure.ThemeManager.Active.LightText)
        };

        if (icon != AppDialogIcon.None)
        {
            var glyph = new TextBlock
            {
                Text = icon switch
                {
                    AppDialogIcon.Warning => "⚠",
                    AppDialogIcon.Error => "⛔",
                    AppDialogIcon.Question => "?",
                    _ => "ℹ"
                },
                FontSize = 24,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(icon switch
                {
                    AppDialogIcon.Warning => Color.FromRgb(225, 180, 90),
                    AppDialogIcon.Error => Color.FromRgb(220, 100, 110),
                    AppDialogIcon.Question => Color.FromRgb(100, 160, 220),
                    _ => Color.FromRgb(100, 160, 220)
                })
            };
            DockPanel.SetDock(glyph, Dock.Left);
            content.Children.Add(glyph);
        }

        content.Children.Add(text);
        Content = content;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Finish(AppDialogResult.Cancel);
        };
        Closed += (_, _) => _completion.TrySetResult(AppDialogResult.None);
    }

    public Task<AppDialogResult> Completion => _completion.Task;

    private void AddButton(Panel panel, string caption, AppDialogResult result, bool visible, bool primary)
    {
        if (!visible) return;
        var button = new Button
        {
            Content = caption,
            MinWidth = 82,
            IsDefault = primary,
            IsCancel = result == AppDialogResult.Cancel
        };
        if (primary) button.Classes.Add("accent");
        button.Click += (_, _) => Finish(result);
        panel.Children.Add(button);
    }

    private void Finish(AppDialogResult result)
    {
        _completion.TrySetResult(result);
        base.Close(result);
    }
}
