using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;

namespace PS5PKGTool.Views;

/// <summary>Small read-only artwork/text preview dialog used by the details panes.</summary>
internal sealed class FilePreviewWindow : Window
{
    private FilePreviewWindow(string title, Control content)
    {
        Title = title;
        Width = 900;
        Height = 640;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = content;
    }

    public static async Task ShowImageAsync(Window owner, string title, byte[] imageBytes)
    {
        Bitmap? bitmap = null;
        try
        {
            using var stream = new MemoryStream(imageBytes);
            bitmap = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            await AppDialog.ShowErrorAsync(ex.Message, title);
            return;
        }

        var image = new Image { Source = bitmap, Stretch = Avalonia.Media.Stretch.Uniform };
        var window = new FilePreviewWindow(title, new Border { Padding = new Avalonia.Thickness(8), Child = image });
        await window.ShowDialog(owner);
        bitmap.Dispose();
    }

    public static async Task ShowTextAsync(Window owner, string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontFamily = new Avalonia.Media.FontFamily("monospace"),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var window = new FilePreviewWindow(title, new Border { Padding = new Avalonia.Thickness(8), Child = box });
        await window.ShowDialog(owner);
    }
}
