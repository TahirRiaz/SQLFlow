#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SQLFlowCore.Logger
{
    public class RealTimeLogger : ILogger
    {
        private readonly string _name;
        private readonly Action<string> _writeToOutput;
        private readonly ConcurrentDictionary<string, OperationMetrics> _activeOperations;
        private readonly ConcurrentDictionary<string, ProcessingMetrics> _processingMetrics;

        private readonly LogLevel _minLogLevel;
        private readonly int _debugLevel; // e.g., 1, 2, 3...

        // Each call context gets its own stack of "operations" for indentation
        private static readonly AsyncLocal<Stack<string>> s_operationStack = new();

        internal Stack<string> OperationStack => s_operationStack.Value ??= new Stack<string>();

        public RealTimeLogger(
            string name,
            Action<string> writeToOutput,
            LogLevel minLogLevel = LogLevel.Trace,
            int debugLevel = 1)
        {
            _name = name;
            _writeToOutput = writeToOutput;
            _minLogLevel = minLogLevel;
            _debugLevel = debugLevel;

            _activeOperations = new ConcurrentDictionary<string, OperationMetrics>();
            _processingMetrics = new ConcurrentDictionary<string, ProcessingMetrics>();
        }

        // We don't use scopes, so no-op here
        IDisposable ILogger.BeginScope<TState>(TState state) => new NoopDisposable();

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLogLevel;

        // Public Log method (no call-site info)
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Provide empty values for caller info
            LogInternal(logLevel, eventId, state, exception, formatter, "", "", 0);
        }

        // Public Log method with call-site info
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
        {
            LogInternal(logLevel, eventId, state, exception, formatter, memberName, sourceFilePath, sourceLineNumber);
        }

        /// <summary>
        /// Core logging logic shared by both Log() overloads.
        /// </summary>
        private void LogInternal<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter,
            string memberName,
            string sourceFilePath,
            int sourceLineNumber)
        {
            // Skip if not enabled or no formatter
            if (!IsEnabled(logLevel) || formatter == null) return;

            // If Debug-level but debugLevel < 2, skip
            if (logLevel == LogLevel.Debug && _debugLevel < 2) return;

            var message = formatter(state, exception);

            // Special handling for operation start/end messages
            bool isOperationMessage = message.Contains(" 🔄") || message.Contains(" ✅");

            // For regular messages, calculate indentation differently than for operation messages
            int indentLevel = OperationStack.Count;

            // The key change: first level operations should have no indentation
            // But for regular messages inside operations, we still add the extra indent
            if (!isOperationMessage && indentLevel > 0)
            {
                // Regular messages inside operations still get an extra indent level
                indentLevel += 1;
            }
            else if (isOperationMessage)
            {
                // For operation messages, subtract 1 (so first level is 0 indentation)
                // but ensure we don't go below 0
                indentLevel = Math.Max(0, indentLevel - 1);
            }

            var finalMessage = BuildLogMessage(logLevel, message, exception, memberName, sourceFilePath, sourceLineNumber, indentLevel);

            _writeToOutput(finalMessage + Environment.NewLine);
        }

        /// <summary>
        /// Builds a fully formatted log message (timestamp, level, optional caller info, exceptions),
        /// then applies indentation based on the current operation stack depth.
        /// </summary>
        private string BuildLogMessage(
            LogLevel logLevel,
            string message,
            Exception? exception,
            string memberName,
            string sourceFilePath,
            int sourceLineNumber,
            int indentLevel)
        {
            // Basic prefix: "2025-02-25 12:34:56.789 [INFO] "
            var sb = new StringBuilder();
            sb.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{GetShortLogLevel(logLevel)}] {message}");

            // For Debug-level, append call site info if present
            if (logLevel == LogLevel.Debug && !string.IsNullOrEmpty(memberName))
            {
                sb.Append($" at {memberName} in {Path.GetFileName(sourceFilePath)}:line {sourceLineNumber}");
            }

            // If there's an exception, append details
            if (exception != null)
            {
                sb.AppendLine();
                sb.AppendLine($"Exception: {exception.GetType().Name}: {exception.Message}");
                sb.Append($"StackTrace: {exception.StackTrace}");
            }

            // Indent the result based on the provided indent level
            return IndentMessage(sb.ToString(), indentLevel);
        }

        /// <summary>
        /// Indents just the message portion of "[Timestamp] [Level] " by <paramref name="indentLevel"/>.
        /// The timestamp + level remain at the left margin. The rest is padded with spaces.
        /// </summary>
        private string IndentMessage(string message, int indentLevel)
        {
            if (indentLevel <= 0) return message;

            var lines = message.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var indentSpaces = new string(' ', indentLevel * 2);

            // Pattern: "yyyy-MM-dd HH:mm:ss.fff [LEVEL] "
            var logHeaderPattern = @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[[A-Z]+\] ";

            for (int i = 0; i < lines.Length; i++)
            {
                // Check if this line starts with our timestamp + level pattern
                var match = System.Text.RegularExpressions.Regex.Match(lines[i], logHeaderPattern);

                if (match.Success)
                {
                    // Line starts with timestamp + level, so insert indent after it
                    var headerLength = match.Length;
                    var prefix = lines[i].Substring(0, headerLength);
                    var rest = lines[i].Substring(headerLength);
                    lines[i] = prefix + indentSpaces + rest;
                }
                else
                {
                    // Normal line or continuation, indent everything
                    lines[i] = indentSpaces + lines[i];
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Returns a short name for the log level: INFO, DBG, etc.
        /// </summary>
        private string GetShortLogLevel(LogLevel logLevel) =>
            logLevel switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRIT",
                _ => logLevel.ToString()
            };

        /// <summary>
        /// Logs a code block, but only if the logger is enabled at the specified level
        /// AND debug level >= 2.
        /// </summary>
        public void LogCodeBlock(string header, string code, LogLevel logLevel = LogLevel.Information)
        {
            if (!IsEnabled(logLevel)) return;
            if (_debugLevel < 2) return; // skip if debug level < 2

            var codeBlockText = FormatCodeBlock(header, code);

            // Get indentation level - use stack depth for code blocks
            int indentLevel = OperationStack.Count;

            var finalMessage = BuildLogMessage(
                logLevel,
                codeBlockText,
                exception: null,
                memberName: "",
                sourceFilePath: "",
                sourceLineNumber: 0,
                indentLevel: indentLevel);

            _writeToOutput(finalMessage + Environment.NewLine);
        }

        private static string FormatCodeBlock(string header, string code)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {header}");
            sb.AppendLine("-- BEGIN CODE --");
            sb.AppendLine(code);
            sb.Append("-- END CODE --");
            return sb.ToString();
        }

        public void LogMetric(string metricName, double value, Dictionary<string, string>? tags = null)
        {
            if (!IsEnabled(LogLevel.Information)) return;

            var sb = new StringBuilder();
            sb.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [Metric] {metricName}: {value}");

            if (tags != null && tags.Count > 0)
            {
                sb.Append(" | tags: ");
                sb.Append(string.Join(", ", tags.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            }

            // Use the same indentation rules as regular log messages
            int indentLevel = OperationStack.Count;
            var finalLog = IndentMessage(sb.ToString(), indentLevel);

            _writeToOutput(finalLog + Environment.NewLine);
        }

        public IDisposable TimeOperation(string operationName, Dictionary<string, string>? tags = null)
        {
            var metrics = new OperationMetrics(operationName, this, tags);
            _activeOperations.TryAdd($"{operationName}_{Guid.NewGuid()}", metrics);
            return metrics;
        }

        public ProcessingMetrics GetOrCreateProcessingMetrics(string name)
        {
            return _processingMetrics.GetOrAdd(name, _ => new ProcessingMetrics());
        }

        private class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Extension for code block logging on ILogger.
    /// </summary>
    public static class RealTimeLoggerExtensions
    {
        public static void LogCodeBlock(this ILogger logger, string header, string code, LogLevel logLevel = LogLevel.Information)
        {
            if (logger is RealTimeLogger rtLogger)
            {
                rtLogger.LogCodeBlock(header, code, logLevel);
            }
        }
    }

    public class OperationMetrics : IDisposable
    {
        private readonly string _operationName;
        private readonly ILogger _logger;
        private readonly Stopwatch _stopwatch;
        private readonly Dictionary<string, string> _tags;
        private long _rowsProcessed;
        private bool _disposed;
        private readonly object _disposeLock = new();
        private readonly ConcurrentDictionary<string, double> _customMetrics;

        public OperationMetrics(string operationName, ILogger logger, Dictionary<string, string>? tags = null)
        {
            _operationName = operationName;
            _logger = logger;
            _tags = tags ?? new Dictionary<string, string>();
            _stopwatch = Stopwatch.StartNew();
            _customMetrics = new ConcurrentDictionary<string, double>();

            // Push to the stack in RealTimeLogger (if applicable)
            if (_logger is RealTimeLogger realLogger)
            {
                realLogger.OperationStack.Push(operationName);
            }

            // Start message
            _logger.LogInformation($"{operationName} \uD83D\uDD04");
        }

        public void IncrementRowsProcessed(long count = 1)
        {
            Interlocked.Add(ref _rowsProcessed, count);
        }

        public void AddCustomMetric(string name, double value)
        {
            _customMetrics.AddOrUpdate(name, value, (_, current) => current + value);
        }

        public void SetCustomMetric(string name, double value)
        {
            _customMetrics.AddOrUpdate(name, value, (_, _) => value);
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed) return;
                _disposed = true;

                _stopwatch.Stop();
                var duration = _stopwatch.Elapsed;
                var rowsPerSecond = duration.TotalSeconds > 0
                    ? _rowsProcessed / duration.TotalSeconds
                    : 0;

                var sb = new StringBuilder();
                sb.Append($"{_operationName} \u2705 | duration: {duration.TotalSeconds:F2}s");
                if (_rowsProcessed > 0)
                {
                    sb.Append($", rows: {_rowsProcessed:N0}");
                    sb.Append($", throughput: {rowsPerSecond:F2} rows/s");
                }

                // Append any custom metrics
                foreach (var metric in _customMetrics)
                {
                    sb.Append($", {metric.Key}: {metric.Value}");
                }

                // Append tags
                if (_tags.Count > 0)
                {
                    sb.Append(" | tags: ");
                    sb.Append(string.Join(", ", _tags.Select(t => $"{t.Key}={t.Value}")));
                }

                // End message
                _logger.LogInformation(sb.ToString());

                // Pop from operation stack
                if (_logger is RealTimeLogger realLogger && realLogger.OperationStack.Count > 0)
                {
                    realLogger.OperationStack.Pop();
                }
            }
        }
    }

    public class ProcessingMetrics
    {
        private readonly ConcurrentDictionary<string, long> _counters = new();
        private readonly ConcurrentDictionary<string, TimeSpan> _operationTimes = new();
        private readonly ConcurrentQueue<LogEntry> _errors = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _histograms = new();

        public void IncrementCounter(string name, long value = 1)
        {
            _counters.AddOrUpdate(name, value, (_, current) => current + value);
        }

        public void AddOperationTime(string operation, TimeSpan duration)
        {
            _operationTimes.AddOrUpdate(operation, duration, (_, current) => current + duration);
        }

        public void LogError(string error, Exception? ex = null, Dictionary<string, string>? metadata = null)
        {
            _errors.Enqueue(new LogEntry(error, ex, metadata));
        }

        public void AddHistogramValue(string name, double value)
        {
            var histogram = _histograms.GetOrAdd(name, _ => new ConcurrentQueue<double>());
            histogram.Enqueue(value);
        }

        public HistogramStats? GetHistogramStats(string name)
        {
            if (!_histograms.TryGetValue(name, out var histogram)) return null;
            var values = histogram.ToArray();
            if (values.Length == 0) return null;

            Array.Sort(values);
            return new HistogramStats
            {
                Count = values.Length,
                Min = values[0],
                Max = values[values.Length - 1],
                Mean = values.Average(),
                Median = values[values.Length / 2],
                P95 = values[(int)(values.Length * 0.95)],
                P99 = values[(int)(values.Length * 0.99)]
            };
        }

        public IReadOnlyDictionary<string, long> GetCounters() => _counters;
        public IReadOnlyDictionary<string, TimeSpan> GetOperationTimes() => _operationTimes;
        public IEnumerable<LogEntry> GetErrors() => _errors.ToArray();
    }

    public class LogEntry
    {
        public string Message { get; }
        public Exception? Exception { get; }
        public Dictionary<string, string> Metadata { get; }
        public DateTime Timestamp { get; }

        public LogEntry(string message, Exception? ex = null, Dictionary<string, string>? metadata = null)
        {
            Message = message;
            Exception = ex;
            Metadata = metadata ?? new Dictionary<string, string>();
            Timestamp = DateTime.UtcNow;
        }
    }

    public class HistogramStats
    {
        public int Count { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }
        public double Median { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }
    }

    public static class LoggerExtensions
    {
        /// <summary>
        /// Helper extension for starting an OperationMetrics scope with indentation.
        /// </summary>
        public static IDisposable TrackOperation(this ILogger logger, string operationName, Dictionary<string, string>? tags = null)
        {
            if (logger is RealTimeLogger rtLogger)
            {
                return rtLogger.TimeOperation(operationName, tags);
            }
            return new NoopOperation();
        }

        public static void LogMetric(this ILogger logger, string metricName, double value, Dictionary<string, string>? tags = null)
        {
            if (logger is RealTimeLogger rtLogger)
            {
                rtLogger.LogMetric(metricName, value, tags);
            }
        }

        private class NoopOperation : IDisposable
        {
            public void Dispose() { }
        }
    }

    public static class RealTimeLoggerInformationExtensions
    {
        /// <summary>
        /// Logs an information message
        /// </summary>
        public static void LogInformation(this RealTimeLogger logger, string message)
        {
            logger.Log(LogLevel.Information, message);
        }

        /// <summary>
        /// Logs an information message with formatting
        /// </summary>
        public static void LogInformation(this RealTimeLogger logger, string format, params object[] args)
        {
            logger.Log(LogLevel.Information, string.Format(format, args));
        }

        /// <summary>
        /// Logs an error message
        /// </summary>
        public static void LogError(this RealTimeLogger logger, string message)
        {
            logger.Log(LogLevel.Error, message);
        }

        /// <summary>
        /// Logs an error message with exception
        /// </summary>
        public static void LogError(this RealTimeLogger logger, string message, Exception exception)
        {
            logger.Log(LogLevel.Error, 0, message, exception, (m, e) => m);
        }
    }
}
