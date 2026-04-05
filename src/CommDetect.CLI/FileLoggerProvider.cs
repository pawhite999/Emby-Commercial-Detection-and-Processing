using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace CommDetect.CLI;

/// <summary>
/// Writes all log output to a plain-text file alongside the console output.
/// Thread-safe: all loggers from this provider share a single StreamWriter
/// protected by a lock.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(categoryName, _writer, _lock);

    /// <summary>Writes a plain message directly to the log file (no level prefix).</summary>
    public void WriteLine(string message)
    {
        lock (_lock)
        {
            _writer.WriteLine(message);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly StreamWriter _writer;
    private readonly object _lock;

    public FileLogger(string category, StreamWriter writer, object lockObj)
    {
        _category = category;
        _writer   = writer;
        _lock     = lockObj;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var level = logLevel switch
        {
            LogLevel.Trace       => "trce",
            LogLevel.Debug       => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning     => "warn",
            LogLevel.Error       => "fail",
            LogLevel.Critical    => "crit",
            _                    => "????"
        };

        var message = formatter(state, exception);

        lock (_lock)
        {
            _writer.WriteLine($"{level}: {_category}[{eventId.Id}]");
            _writer.WriteLine($"      {message}");
            if (exception != null)
                _writer.WriteLine($"      {exception}");
        }
    }
}
