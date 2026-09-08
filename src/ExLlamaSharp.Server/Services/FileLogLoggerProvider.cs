using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

/// <summary>Daily rolling file sink under %ProgramData%\ExLlamaSharp\logs.</summary>
public sealed class FileLogLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _directory;
    private StreamWriter? _writer;
    private string _currentDate = "";
    private bool _disposed;

    public FileLogLoggerProvider()
    {
        _directory = ProductionRuntime.LogsDirectory;
        Directory.CreateDirectory(_directory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            EnsureWriter();
            var line = $"{DateTime.UtcNow:o} [{level}] {category}: {message}";
            _writer!.WriteLine(line);
            if (exception is not null)
            {
                _writer.WriteLine(exception);
            }

            _writer.Flush();
        }
    }

    private void EnsureWriter()
    {
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (_writer is not null && _currentDate == day)
        {
            return;
        }

        _writer?.Dispose();
        _currentDate = day;
        var path = Path.Combine(_directory, $"server-{day}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(string category, FileLogLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
