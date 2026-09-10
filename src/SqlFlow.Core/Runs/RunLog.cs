using System.Globalization;
using System.Text;

namespace SqlFlow.Core.Runs;

/// <summary>How much detail the canonical run log records. Levels are cumulative.</summary>
public enum RunLogLevel
{
    /// <summary>The authoritative step-by-step account: what ran, in what order, with counts, durations,
    /// and the outcome. Always enough to know exactly what took place.</summary>
    Info = 0,

    /// <summary>Adds the engine's decisions: resolved windows, column mappings, checksum exclusions,
    /// apply-mode choices.</summary>
    Debug = 1,

    /// <summary>Adds every generated SQL statement inline at its point in the timeline.</summary>
    Trace = 2,
}

/// <summary>One timestamped event in a run's canonical log.</summary>
public sealed record RunLogEntry
{
    public required DateTime TimestampUtc { get; init; }

    public required RunLogLevel Level { get; init; }

    /// <summary>The run step the event belongs to (for example "stage.copy", "target.evolve", "upsert.insert").</summary>
    public required string Step { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// Receives run events as they happen. The runner emits through this seam; the default
/// <see cref="NullRunEventSink"/> records nothing (a library caller that wants no log pays nothing), while
/// <see cref="RunLogger"/> collects the canonical per-run log written to the .sqlflow run folder.
/// </summary>
public interface IRunEventSink
{
    /// <summary>The enabled level: events above it are dropped at the source.</summary>
    RunLogLevel Level { get; }

    void Log(RunLogLevel level, string stepName, string message);
}

/// <summary>The no-op default sink.</summary>
public sealed class NullRunEventSink : IRunEventSink
{
    public static readonly NullRunEventSink Instance = new();

    private NullRunEventSink()
    {
    }

    public RunLogLevel Level => RunLogLevel.Info;

    public void Log(RunLogLevel level, string stepName, string message)
    {
    }
}

/// <summary>
/// The canonical run log: collects timestamped events in order (thread-safe, because init-load segments and
/// parallel writers emit concurrently), optionally echoes each formatted line live (the CLI streams it to the
/// console), and renders the whole log as the <c>run.log</c> artifact of the .sqlflow run folder. Unlike the
/// legacy engine, nothing is gated away by default and the log survives failure: what was recorded up to the
/// failure point is exactly what gets written.
/// </summary>
public sealed class RunLogger : IRunEventSink
{
    private readonly List<RunLogEntry> _entries = [];
    private readonly Action<string>? _echo;
    private readonly Lock _gate = new();

    public RunLogger(RunLogLevel level = RunLogLevel.Info, Action<string>? echo = null)
    {
        Level = level;
        _echo = echo;
    }

    public RunLogLevel Level { get; }

    public IReadOnlyList<RunLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public void Log(RunLogLevel level, string stepName, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(message);

        if (level > Level)
        {
            return;
        }

        var entry = new RunLogEntry { TimestampUtc = DateTime.UtcNow, Level = level, Step = stepName, Message = message };
        lock (_gate)
        {
            _entries.Add(entry);
        }

        _echo?.Invoke(Format(entry));
    }

    /// <summary>Renders the collected log as the run.log text.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var entry in Entries)
        {
            sb.AppendLine(Format(entry));
        }

        return sb.ToString();
    }

    /// <summary>One formatted line per event; a multi-line message (SQL at trace level) continues indented
    /// under its event line, so the file stays both readable and greppable.</summary>
    public static string Format(RunLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var head =
            $"{entry.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z " +
            $"{LevelName(entry.Level),-5} {entry.Step,-22} ";

        var lines = entry.Message.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 1)
        {
            return head + lines[0];
        }

        var sb = new StringBuilder(head + lines[0]);
        foreach (var line in lines.Skip(1))
        {
            sb.AppendLine().Append("    | ").Append(line);
        }

        return sb.ToString();
    }

    private static string LevelName(RunLogLevel level) => level switch
    {
        RunLogLevel.Debug => "DEBUG",
        RunLogLevel.Trace => "TRACE",
        _ => "INFO",
    };
}
