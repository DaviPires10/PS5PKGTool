using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PS5PKGTool.Infrastructure;
using PS5PKGTool.Views;

namespace PS5PKGTool;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A single instance keeps the manifest and the thumbnail cache consistent, mirroring the
            // WinForms build. The mutex is released when the process exits.
            if (!Program.TryAcquireSingleInstance(out Mutex mutex))
            {
                mutex.Dispose();
                AppDialog.ShowWarning("PS5 PKG Tool is already running.", "PS5 PKG Tool");
                desktop.Shutdown();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            desktop.Exit += (_, _) => mutex.Dispose();
            desktop.MainWindow = new MainWindow(ExternalPath(desktop.Args));
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? ExternalPath(string[]? args) =>
        args is { Length: >= 2 } && args[0] == "--open" ? args[1] : null;

    /// <summary>Runs <paramref name="action"/> on the UI thread, ignoring shutdown races.</summary>
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
