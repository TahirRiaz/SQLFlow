using System.Text.RegularExpressions;
using SqlFlow.Core;

namespace SqlFlow.SqlServer.Query;

/// <summary>
/// A read-only query a caller consumes row by row (<see cref="SqlServerQueryRunner.ReadAsync"/>): the statement, the named
/// parameters it binds, and how long it may run. Each parameter is an <c>@name</c> in the statement, sent as an
/// <c>nvarchar</c> value (null as SQL NULL), so a value is never written into the statement's text.
/// </summary>
public sealed partial record ReadOnlyRead
{
    /// <summary>The most parameters one statement binds.</summary>
    public const int MaxParameters = 100;

    /// <summary>The longest a statement may run, in seconds.</summary>
    public const int MaxTimeoutSeconds = 3600;

    public required string Sql { get; init; }

    /// <summary>The values the statement's <c>@name</c> parameters take, by name without the <c>@</c>.</summary>
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>The command timeout, 1 to <see cref="MaxTimeoutSeconds"/> seconds.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Refuses a parameter that is not a T-SQL variable name, more than <see cref="MaxParameters"/>, or a timeout out of bounds.</summary>
    /// <exception cref="SqlFlowException">A bound is broken; the message names it.</exception>
    public ReadOnlyRead Validate()
    {
        if (TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            throw new SqlFlowException($"A read-only query runs for 1 to {MaxTimeoutSeconds} seconds, not {TimeoutSeconds}.");
        }

        if (Parameters.Count > MaxParameters)
        {
            throw new SqlFlowException($"A read-only query binds at most {MaxParameters} parameters, not {Parameters.Count}.");
        }

        foreach (var name in Parameters.Keys)
        {
            if (!ParameterName().IsMatch(name))
            {
                throw new SqlFlowException(
                    $"'{name}' is not a parameter name: a letter or '_', then letters, digits and '_', at most 128 characters, written without the '@'.");
            }
        }

        return this;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();
}
