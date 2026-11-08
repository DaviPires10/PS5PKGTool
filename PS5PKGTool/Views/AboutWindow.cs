using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

/// <summary>Avalonia replacement for the WinForms <c>AboutForm</c>.</summary>
internal sealed class AboutWindow : Window
{
    private AboutWindow(string version)
    {
        Title = "About PS5 PKG Tool";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Infrastructure.ThemeManager.Active.GreyBackground);

        var title = new TextBlock
        {
            Text = "PS5 PKG Tool",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Infrastructure.ThemeManager.Active.LightText)
        };
        var body = new TextBlock
        {
            Text = $"Version {version}\n\n" +
                   "Inspect PS5 dumps, packages and filesystem images; build FPKG, exFAT, FFPKG and " +
                   "FFPFSC images; explore trophies, activities, SELF/ELF metadata and PlayGo chunks.\n\n" +
                   "Avalonia port of the original WinForms tool by pearlxcore.\n\n" +
                   "PS5 PKG Tool is not affiliated with Sony Interactive Entertainment.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Infrastructure.ThemeManager.Active.LightText)
        };

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        var github = new Button { Content = "GitHub" };
        var kofi = new Button { Content = "Ko-fi" };
        github.Click += (_, _) => Infrastructure.PlatformService.OpenUrl(UpdateChecker.RepositoryUrl);
        kofi.Click += (_, _) => Infrastructure.PlatformService.OpenUrl("https://ko-fi.com/R6R524N7X");
        links.Children.Add(github);
        links.Children.Add(kofi);

        var close = new Button
        {
            Content = "Close",
            Width = 96,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        panel.Children.Add(title);
        panel.Children.Add(body);
        panel.Children.Add(links);
        panel.Children.Add(close);
        Content = panel;
    }

    public static Task ShowAsync(Window owner, string version)
    {
        var dialog = new AboutWindow(version);
        return dialog.ShowDialog(owner);
    }
}
