namespace SqlFlow.Core.Query;

/// <summary>One column of a query result: its name and the provider's type name for it.</summary>
public sealed record QueryColumn(string Name, string DataType);

/// <summary>
/// The rows a query returned, rendered as text so one shape carries every provider type through the queue and
/// out to a client. <see cref="Truncated"/> is the field a caller must read before describing the answer:
/// a truncated result is a page, and summing a truncated page gives a confidently wrong total.
/// </summary>
public sealed record QueryResult
{
    public required IReadOnlyList<QueryColumn> Columns { get; init; }

    public required IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; }

    public required int RowCount { get; init; }

    /// <summary>True when the row cap stopped the read before the query ran out of rows.</summary>
    public required bool Truncated { get; init; }

    /// <summary>The database the query actually ran in, as the connection reported it.</summary>
    public string? Database { get; init; }

    /// <summary>The statement that ran, verbatim, so a result is self-describing in the task history.</summary>
    public required string Sql { get; init; }
}

/// <summary>
/// The ask to RUN an already-approved query. It carries the statement rather than a reference to one, because
/// by the time this exists the approval has already happened: the control plane resolved a one-time token into
/// the exact SQL a person saw and agreed to.
/// </summary>
public sealed record QueryRunRequest
{
    public required string Sql { get; init; }

    public string? Database { get; init; }

    /// <summary>The most rows returned. Beyond this the result is marked truncated and the read stops.</summary>
    public int MaxRows { get; init; } = DefaultMaxRows;

    /// <summary>The command timeout. A business question that cannot be answered inside this is a question
    /// that needs narrowing, not more time on a shared warehouse.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    public const int DefaultMaxRows = 200;

    public const int MaxMaxRows = 5000;

    public const int DefaultTimeoutSeconds = 120;

    public const int MaxTimeoutSeconds = 600;

    /// <summary>Validates the bounds. The SQL itself is validated by the read-only guard, which lives with the
    /// provider because it parses T-SQL.</summary>
    public QueryRunRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(Sql))
        {
            throw new SqlFlowException("A query run requires the statement to run.");
        }

        if (MaxRows is < 1 or > MaxMaxRows)
        {
            throw new SqlFlowException($"maxRows must be between 1 and {MaxMaxRows}.");
        }

        if (TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            throw new SqlFlowException($"timeoutSeconds must be between 1 and {MaxTimeoutSeconds}.");
        }

        return this;
    }
}
