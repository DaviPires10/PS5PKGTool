using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using PS5PKGTool.Infrastructure;

namespace PS5PKGTool.Views;

/// <summary>One rendered log line, bound by the Log tab's template.</summary>
public sealed class LogLine
{
    public required string Text { get; init; }
    public required IBrush Brush { get; init; }
    public LogEntry Entry { get; init; }
}

public partial class MainWindow
{
    private readonly ConcurrentQueue<LogEntry> _pendingLog = new();
    private readonly List<LogEntry> _logEntries = [];
    private readonly ObservableCollection<LogLine> _logLines = [];
    private DispatcherTimer? _logRefreshTimer;
    private int _logFilterLevel = -1;
    private bool _logAutoScroll = true;
    private const int MaxLogEntries = 5000;

    private void InitializeLog()
    {
        Logger.Logged -= OnLogged;
        Logger.Logged += OnLogged;
        txtLogView.ItemsSource = _logLines;
        cboLogLevel.ItemsSource = new[] { "All", "Info", "Warn", "Error" };
        cboLogLevel.SelectedIndex = 0;
        cboLogLevel.SelectionChanged += (_, _) =>
        {
            _logFilterLevel = cboLogLevel.SelectedIndex - 1;
            RebuildLogView();
        };
        chkLogAutoScroll.IsCheckedChanged += (_, _) =>
        {
            _logAutoScroll = chkLogAutoScroll.IsChecked == true;
            _settings.LogAutoScroll = _logAutoScroll;
            SaveSettingsQuietly();
        };
        btnLogClear.Click += (_, _) =>
        {
            _logEntries.Clear();
            _logLines.Clear();
        };
        btnLogOpenFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Logger.LogDirectory);
                PlatformService.OpenPath(Logger.LogDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Could not open the log folder: {ex.Message}");
            }
        };

        _logRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _logRefreshTimer.Tick += (_, _) => FlushLog();
        _logRefreshTimer.Start();
    }

    private void ShutdownLog()
    {
        _logRefreshTimer?.Stop();
        Logger.Logged -= OnLogged;
    }

    private void OnLogged(LogEntry entry) => _pendingLog.Enqueue(entry);

    private void FlushLog()
    {
        if (_pendingLog.IsEmpty) return;
        bool appended = false;
        while (_pendingLog.TryDequeue(out LogEntry entry))
        {
            _logEntries.Add(entry);
            if (MatchesLogFilter(entry))
            {
                AppendLogLine(entry);
                appended = true;
            }
        }
        TrimLogBuffer();
        if (appended && _logAutoScroll) ScrollLogToEnd();
    }

    private void AppendLogLine(LogEntry entry) =>
        _logLines.Add(new LogLine
        {
            Text = $"{entry.Time:HH:mm:ss.fff} [{LogLevelText(entry.Level)}] {entry.Message}",
            Brush = LogLevelBrush(entry.Level),
            Entry = entry
        });

    private void ScrollLogToEnd()
    {
        if (_logLines.Count == 0) return;
        txtLogView.ScrollIntoView(_logLines[^1]);
    }

    private void RebuildLogView()
    {
        _logLines.Clear();
        foreach (LogEntry entry in _logEntries)
            if (MatchesLogFilter(entry))
                AppendLogLine(entry);
        if (_logAutoScroll) ScrollLogToEnd();
    }

    private void TrimLogBuffer()
    {
        if (_logEntries.Count <= MaxLogEntries + 1000) return;
        _logEntries.RemoveRange(0, _logEntries.Count - MaxLogEntries);
        RebuildLogView();
    }

    private bool MatchesLogFilter(LogEntry entry) =>
        _logFilterLevel < 0 || (int)entry.Level == _logFilterLevel;

    private static IBrush LogLevelBrush(LogLevel level) => level switch
    {
        LogLevel.Warn => new SolidColorBrush(Color.FromRgb(230, 180, 80)),
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(240, 110, 110)),
        _ => new SolidColorBrush(ThemeManager.Active.LightText)
    };

    private static string LogLevelText(LogLevel level) => level switch
    {
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    };
}
