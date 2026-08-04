using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AlbionCompanion.App;

// Temporary diagnostic added 2026-08-04: a Blazor WebView render exception (navigating to
// Broadcast) surfaced as the generic "An unhandled error has occurred" banner with no trace in
// Windows Event Log or debug_maui_startup_failures.log. This mirrors what a debugger's Output
// window would show (Blazor's internal renderer logs exceptions via ILogger before the WebView
// shows its generic error UI), but written to disk so it's visible without attaching one. Safe to
// remove once the crash is diagnosed.
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string path) => _path = path;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, _path));

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _path;

        public FileLogger(string categoryName, string path)
        {
            _categoryName = categoryName;
            _path = path;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {logLevel} {_categoryName}: {formatter(state, exception)}{exception}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
            catch
            {
                // Logging must never itself throw.
            }
        }
    }
}
