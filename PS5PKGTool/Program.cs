using Avalonia;
using PS5PKGTool.Infrastructure;
using PS5PKGTool.Views;

namespace PS5PKGTool;

internal static class Program
{
    private const string SingleInstanceName = "PS5PKGTool.SingleInstance.v1";

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleFatal("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Exception("Unobserved task", e.Exception);
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Entry point used by the Avalonia designer and the visual tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    internal static void HandleFatal(string context, Exception? exception)
    {
        Logger.Exception(context, exception ?? new Exception("Unknown error"));
        try
        {
            AppDialog.ShowError(
                (exception?.Message ?? "An unknown error occurred.") + $"\n\nA log was written to:\n{Logger.LogPath}",
                "PS5 PKG Tool error");
        }
        catch (Exception)
        {
            // Nothing more we can do while handling a fatal error.
        }
    }

    /// <summary>Mutex-based single instance guard, shared by every platform.</summary>
    internal static bool TryAcquireSingleInstance(out Mutex mutex)
    {
        mutex = new Mutex(true, SingleInstanceName, out bool createdNew);
        return createdNew;
    }
}
