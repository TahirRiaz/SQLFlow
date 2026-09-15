using SqlFlow.Core.Model;

namespace SqlFlow.Core;

/// <summary>Base type for all SQLFlow errors.</summary>
public class SqlFlowException : Exception
{
    public SqlFlowException(string message)
        : base(message)
    {
    }

    public SqlFlowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a request came back with a non-2xx status the retry policy will not retry. Carries the status code so a
/// caller can decide per request whether the failure is fatal, a tolerated skip or one to retry later (a per-id rejection,
/// such as a route id the endpoint no longer accepts, must not poison the other ids in the same sweep), and the wait the
/// service asked for when it named one.
/// </summary>
public sealed class HttpStatusException : SqlFlowException
{
    public HttpStatusException(int statusCode, string message)
        : this(statusCode, message, null)
    {
    }

    /// <param name="statusCode">The HTTP status code the endpoint answered with.</param>
    /// <param name="message">The failure, secret-free.</param>
    /// <param name="retryAfter">The wait the service asked for (its <c>Retry-After</c>), or null when it named none. A negative wait (a retry date already past) is kept as zero.</param>
    public HttpStatusException(int statusCode, string message, TimeSpan? retryAfter)
        : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter is { } wait && wait < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
    }

    /// <summary>The HTTP status code the endpoint answered with.</summary>
    public int StatusCode { get; }

    /// <summary>How long the service asked the caller to wait before trying again, or null when it named no wait. A later attempt must not come sooner.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Thrown when a pipeline definition is invalid.</summary>
public sealed class FlowValidationException : SqlFlowException
{
    public FlowValidationException(string message)
        : base(message)
    {
    }

    public FlowValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Why a source read selected no files. Each is a materially different outcome to report.</summary>
public enum NoSourceFilesReason
{
    /// <summary>
    /// Nothing under the location matches the file pattern at all: an empty landing folder, or a wrong path or
    /// pattern. No selection filter can be blamed, because none was ever reached.
    /// </summary>
    NoCandidates,

    /// <summary>
    /// Candidate files exist, but every one of them is at or older than the incremental watermark. This is the
    /// steady state of an incremental flow with nothing new to load, and says nothing is wrong.
    /// </summary>
    NoneAfterWatermark,

    /// <summary>
    /// Candidate files exist, but none pass the configured selection (the init date window, the path mask, or a
    /// date window combined with a watermark, where no single bound can be singled out as the cause).
    /// </summary>
    NoneSelected,
}

/// <summary>
/// Thrown when a source read selected no files. <see cref="Reason"/> separates a location that holds nothing from
/// one whose files are simply all older than the watermark: the second is the normal resting state of an
/// incremental flow and the first usually means a misconfigured path, so the two must never be reported alike.
/// The engine treats this as a clean no-op for an incremental run and as a failure otherwise.
/// </summary>
public sealed class NoSourceFilesException : SqlFlowException
{
    public NoSourceFilesException(NoSourceFilesReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Which of the distinct empty-selection outcomes this is.</summary>
    public NoSourceFilesReason Reason { get; }
}

/// <summary>Thrown when the target schema diverges from the desired schema under a 'strict' policy.</summary>
public sealed class SchemaDriftException : SqlFlowException
{
    public SchemaDriftException(TableSchema desired, IReadOnlyList<ColumnDefinition> missing)
        : base($"Schema drift on [{desired.Schema}].[{desired.Table}]: target is missing {missing.Count} column(s) " +
               $"({string.Join(", ", missing.Select(c => c.Name))}) and the evolve policy is 'strict'.")
    {
    }
}
