using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Translate;

namespace SqlFlow.Translate;

/// <summary>
/// The run's dataset rows, indexed for <c>$forEach</c>/<c>$row</c> resolution: each declared dataset's rows are
/// grouped by its bind-column values, and a lookup takes the same-named columns from the enclosing scope to
/// select the matching group. A bind-less (single-instance) dataset groups under one empty key, so it resolves
/// to all of its rows at any scope with no lookup. The reserved name <c>rows</c> resolves to the primary
/// query's rows (result-set grain). Key values compare by an invariant, type-normalized string representation
/// (so an <c>int</c> key on one side matches a <c>bigint</c> on the other) with case-insensitive strings,
/// matching SQL Server join semantics under the default collation.
/// </summary>
public sealed class TranslateDatasetIndex
{
    private const string PrimaryRowsName = "rows";

    private sealed record Group(
        IReadOnlyList<string> Bind,
        Dictionary<string, List<IReadOnlyDictionary<string, object?>>> RowsByKey);

    private readonly Dictionary<string, Group> _datasets = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _primaryRows = [];

    /// <summary>Indexes one dataset's rows. <paramref name="columns"/> is the dataset query's full column list
    /// (known even for an empty result), so a bind column the query does not return fails loudly here rather
    /// than resolving every group to empty.</summary>
    public void AddDataset(TranslateDataset dataset, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var columnSet = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        var missing = dataset.Bind.Where(b => !columnSet.Contains(b)).ToList();
        if (missing.Count > 0)
        {
            throw new SqlFlowException(
                $"Dataset '{dataset.Name}' binds by [{string.Join(", ", dataset.Bind)}], but its query does not return " +
                $"[{string.Join(", ", missing)}]. Returned columns: {string.Join(", ", columns)}.");
        }

        var byKey = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = KeyOf(dataset.Bind, name => row[name]);
            if (!byKey.TryGetValue(key, out var group))
            {
                group = [];
                byKey[key] = group;
            }

            group.Add(row);
        }

        _datasets[dataset.Name] = new Group(dataset.Bind, byKey);
    }

    /// <summary>Registers the primary query's rows for the reserved <c>rows</c> dataset (result-set grain).</summary>
    public void SetPrimaryRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _primaryRows = rows;
    }

    /// <summary>The rows a <c>$forEach</c> iterates at the given scope: all primary rows for <c>rows</c>, else the
    /// dataset group whose bind values match the scope (empty when no child rows match). A bind column absent from
    /// the scope is an authoring error and throws with the visible columns.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Resolve(string forEach, TranslateScope scope, string path)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.Equals(forEach, PrimaryRowsName, StringComparison.OrdinalIgnoreCase))
        {
            return _primaryRows;
        }

        // The loader proved the name against the declared datasets, so a miss here is a programming error.
        var dataset = _datasets[forEach];
        var key = KeyOf(dataset.Bind, name =>
        {
            if (!scope.TryGet(name, out var value))
            {
                var available = scope.AvailableColumns();
                throw new SqlFlowException(
                    $"At {path}: dataset '{forEach}' binds by column '{name}', which is not in scope. " +
                    (available.Count == 0
                        ? "No columns are in scope here (a result-set-grain root has none; bind inside a $forEach)."
                        : $"Columns in scope: {string.Join(", ", available)}."));
            }

            return value;
        });

        return dataset.RowsByKey.TryGetValue(key, out var rows) ? rows : [];
    }

    private static string KeyOf(IReadOnlyList<string> bind, Func<string, object?> valueOf)
    {
        var parts = new string[bind.Count];
        for (var i = 0; i < bind.Count; i++)
        {
            parts[i] = KeyValueOf(valueOf(bind[i]));
        }

        // The unit separator cannot appear in the normalized forms, so composite keys never collide by joining.
        return string.Join('\u001f', parts);
    }

    /// <summary>The type-normalized invariant form of one key value, so equal values compare equal across the
    /// numeric widths and temporal types SQL Server may hand back for the two sides of a join.</summary>
    private static string KeyValueOf(object? value)
        => value switch
        {
            null or DBNull => "\u0000null",
            string s => s.ToUpperInvariant(),
            bool b => b ? "1" : "0",
            byte or sbyte or short or ushort or int or uint or long
                => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            ulong ul => ul.ToString(CultureInfo.InvariantCulture),
            float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString("G29", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("o", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("o", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            byte[] bytes => Convert.ToBase64String(bytes),
            char c => char.ToUpperInvariant(c).ToString(),
            _ => throw new SqlFlowException($"A dataset bind column holds an unsupported key type '{value.GetType().Name}'."),
        };
}
