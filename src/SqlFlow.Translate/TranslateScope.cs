namespace SqlFlow.Translate;

/// <summary>
/// The column scope a template node renders against: a chain of row frames, innermost first. A <c>$forEach</c>
/// pushes each dataset row onto the enclosing scope, so an item template sees its own row's columns first and
/// the enclosing rows' columns behind them (a child row shadows a same-named parent column). Lookup is
/// case-insensitive, matching SQL Server column-name semantics.
/// </summary>
public sealed class TranslateScope
{
    /// <summary>The empty root scope (a result-set-grain document has no row at its root).</summary>
    public static readonly TranslateScope Empty = new(null, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

    private readonly TranslateScope? _parent;
    private readonly IReadOnlyDictionary<string, object?> _row;

    private TranslateScope(TranslateScope? parent, IReadOnlyDictionary<string, object?> row)
    {
        _parent = parent;
        _row = row;
    }

    /// <summary>A new scope with <paramref name="row"/> as the innermost frame.</summary>
    public TranslateScope Push(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new TranslateScope(this, row);
    }

    /// <summary>Resolves a column, innermost frame first. DBNull is normalized to null at row materialization,
    /// so a found-but-NULL column returns true with a null value.</summary>
    public bool TryGet(string column, out object? value)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            if (scope._row.TryGetValue(column, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Every column name visible from this scope, innermost first, for error messages.</summary>
    public IReadOnlyList<string> AvailableColumns()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            foreach (var name in scope._row.Keys)
            {
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }
}
