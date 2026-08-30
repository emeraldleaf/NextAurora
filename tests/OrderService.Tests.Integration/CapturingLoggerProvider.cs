using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OrderService.Tests.Integration;

/// <summary>
/// Captures every log entry the test host emits so a test can assert on infrastructure behavior
/// that leaves no other trace: Wolverine's durable inbox rejects a redelivered envelope *before*
/// any handler runs, so the only witness is the log line it writes on the way out — the same line
/// the first VPS deploy produced (see docs/performance-and-data-correctness.md, container memory
/// section). Register with <c>AddFilter&lt;CapturingLoggerProvider&gt;(null, LogLevel.Trace)</c> so the
/// host's configured minimum level doesn't drop the entry before it reaches us.
/// </summary>
/// <summary>One captured log entry: category, level, the formatted message, and the exception if the call carried one.</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
        // Nothing to release — the queue is garbage-collected with the provider.
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}
