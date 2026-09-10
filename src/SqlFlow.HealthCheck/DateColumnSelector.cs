namespace SqlFlow.HealthCheck;

/// <summary>The auto-detection outcome: the chosen column (null when none qualify), every candidate, and the
/// reasoning line for the log.</summary>
public sealed record DateColumnChoice
{
    public string? Column { get; init; }

    public required IReadOnlyList<string> Candidates { get; init; }

    public required string Reasoning { get; init; }
}

/// <summary>
/// Picks the date column for an ad-hoc health check from a table's column metadata, so 'sqlflow healthcheck
/// --source ... --object ...' works with zero configuration. Only date-typed columns qualify; among them,
/// business-named date columns (OrderDate, TransactionDate) outrank generic event stamps (CreatedAt,
/// ModifiedDate), which outrank the warehouse system columns (InsertedDate_DW), because the question a health
/// check asks is about the DATA's calendar, not the load's. Deterministic: tier, then shortest name, then
/// ordinal order. The choice and the alternatives are always reported so an operator can pin --date-column
/// when the heuristic picks wrong.
/// </summary>
public static class DateColumnSelector
{
    private static readonly HashSet<string> DateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset",
    };

    public static DateColumnChoice Choose(IReadOnlyList<(string Name, string NativeType)> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var candidates = columns
            .Where(c => DateTypes.Contains(BaseType(c.NativeType)))
            .Select(c => c.Name)
            .ToList();

        if (candidates.Count == 0)
        {
            return new DateColumnChoice
            {
                Candidates = candidates,
                Reasoning = "no date-typed columns (date/datetime/datetime2/smalldatetime/datetimeoffset) exist on the object",
            };
        }

        var chosen = candidates
            .OrderBy(Tier)
            .ThenBy(c => c.Length)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .First();

        var alternatives = candidates.Where(c => !string.Equals(c, chosen, StringComparison.OrdinalIgnoreCase)).ToList();
        return new DateColumnChoice
        {
            Column = chosen,
            Candidates = candidates,
            Reasoning = $"chose '{chosen}' (tier {Tier(chosen)})" +
                (alternatives.Count > 0 ? $"; alternatives: {string.Join(", ", alternatives)}. Pin --date-column to override." : string.Empty),
        };
    }

    /// <summary>Lower is better. 1: business date names; 2: generic date names; 3: event-stamp names;
    /// 4: any other date-typed column; 5: warehouse system columns.</summary>
    public static int Tier(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith("_dw", StringComparison.Ordinal))
        {
            return 5;
        }

        if (lower == "date" || lower.EndsWith("date", StringComparison.Ordinal))
        {
            return 1;
        }

        if (lower.Contains("date", StringComparison.Ordinal))
        {
            return 2;
        }

        if (lower.Contains("created", StringComparison.Ordinal) || lower.Contains("modified", StringComparison.Ordinal)
            || lower.Contains("updated", StringComparison.Ordinal) || lower.Contains("inserted", StringComparison.Ordinal)
            || lower.Contains("timestamp", StringComparison.Ordinal) || lower.EndsWith("at", StringComparison.Ordinal)
            || lower.EndsWith("time", StringComparison.Ordinal))
        {
            return 3;
        }

        return 4;
    }

    private static string BaseType(string nativeType)
    {
        var paren = nativeType.IndexOf('(', StringComparison.Ordinal);
        return (paren > 0 ? nativeType[..paren] : nativeType).Trim();
    }
}
