using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IntercomEventing.Benchmark.Examples.DependencyInjection;

/// <summary>
/// One entry written through <see cref="ILogger"/>.
/// </summary>
public sealed record RecordedLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Stands in for the console or another logging provider: it prints each entry on one line and keeps it, so the example
/// can check that an event failure reached <see cref="ILogger"/>.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<RecordedLogEntry> _entries = new();

    public IReadOnlyCollection<RecordedLogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName)
    {
        return new RecordingLogger(categoryName, _entries);
    }

    public void Dispose()
    {
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<RecordedLogEntry> _entries;

        public RecordingLogger(string category, ConcurrentQueue<RecordedLogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _entries.Enqueue(new RecordedLogEntry(_category, logLevel, message, exception));
            Console.WriteLine($"ILogger {logLevel} [{_category}]: {message}: {exception?.Message}");
        }
    }
}
