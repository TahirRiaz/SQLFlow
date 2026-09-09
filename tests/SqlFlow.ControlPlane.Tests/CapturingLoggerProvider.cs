using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A minimal in-memory <see cref="ILoggerProvider"/> for the integration tests: it appends every log line to a
/// thread-safe queue the test can read. The DB-backed run-trigger test uses it to surface the background worker's
/// own diagnostics (which run on a separate thread and never reach xUnit output) when an assertion fails, so a
/// failure explains itself instead of being a bare timeout.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines;

    public CapturingLoggerProvider(ConcurrentQueue<string> lines) => _lines = lines;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _lines;

        public CapturingLogger(string category, ConcurrentQueue<string> lines)
        {
            _category = category;
            _lines = lines;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            var suffix = exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}";
            _lines.Enqueue($"[{logLevel}] {_category}: {message}{suffix}");
        }
    }
}
