using System.Globalization;
using System.Text;

namespace SqlFlow.Lineage.PowerBi;

/// <summary>
/// One read of a source table. Identity is the instance: a query that reads the same table twice (a table and a
/// query merged into it that load from the same object) holds two instances, and each gets its own alias when
/// rendered.
/// </summary>
internal sealed class SourceTable(string database, string schema, string name)
{
    public string Database { get; } = database;

    public string Schema { get; } = schema;

    public string Name { get; } = name;

    public string Label => $"{Database}.{Schema}.{Name}";
}

/// <summary>A scalar T-SQL expression over source columns, before aliases are assigned.</summary>
internal abstract record SqlScalar;

/// <summary>A source column.</summary>
internal sealed record SqlColumn(SourceTable Table, string Column) : SqlScalar;

/// <summary>A text literal.</summary>
internal sealed record SqlText(string Value) : SqlScalar;

/// <summary>A number literal, already in T-SQL form.</summary>
internal sealed record SqlNumber(string Value) : SqlScalar;

/// <summary>NULL.</summary>
internal sealed record SqlNull : SqlScalar;

/// <summary>Text concatenation that yields NULL when any part is NULL, as Power Query's <c>&amp;</c> does.</summary>
internal sealed record SqlConcat(IReadOnlyList<SqlScalar> Parts) : SqlScalar;

/// <summary>A binary arithmetic operation.</summary>
internal sealed record SqlArithmetic(string Operator, SqlScalar Left, SqlScalar Right) : SqlScalar;

/// <summary>A unary minus.</summary>
internal sealed record SqlNegate(SqlScalar Operand) : SqlScalar;

/// <summary>A conversion to a T-SQL type.</summary>
internal sealed record SqlCast(SqlScalar Operand, string SqlType) : SqlScalar;

/// <summary>A column of a relation: its expression, or why it has none.</summary>
internal readonly record struct ColumnValue(SqlScalar? Expression, string? Problem)
{
    public static ColumnValue Of(SqlScalar expression) => new(expression, null);

    public static ColumnValue Refused(string problem) => new(null, problem);
}

/// <summary>A join a relation adds to its base.</summary>
internal sealed record RelationJoin(bool Inner, Relation Right, IReadOnlyList<(SqlScalar Left, SqlScalar Right)> On);

/// <summary>
/// The rows a Power Query table query produces, as T-SQL: a base source table, the joins merged into it, and the
/// expression behind each output column. A relation whose base still exposes every source column under its own
/// name (a navigation with no column selection yet) is <see cref="Open"/>. A relation that could not be expressed at
/// all carries a <see cref="Problem"/>, since its rows are not the source's rows.
/// </summary>
internal sealed record Relation
{
    public SourceTable? Base { get; init; }

    public IReadOnlyList<RelationJoin> Joins { get; init; } = [];

    /// <summary>The output columns defined by the query's steps, in order.</summary>
    public IReadOnlyList<KeyValuePair<string, ColumnValue>> Columns { get; init; } = [];

    /// <summary>True while every source column is still exposed under its own name.</summary>
    public bool Open { get; init; }

    /// <summary>Source column names an open relation no longer exposes.</summary>
    public IReadOnlySet<string> Removed { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public string? Problem { get; init; }

    public static Relation Refused(string problem) => new() { Problem = problem };

    /// <summary>The value of output column <paramref name="name"/>.</summary>
    public ColumnValue Column(string name)
    {
        for (var i = Columns.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Columns[i].Key, name, StringComparison.Ordinal))
            {
                return Columns[i].Value;
            }
        }

        if (Open && Base is not null && !Removed.Contains(name))
        {
            return ColumnValue.Of(new SqlColumn(Base, name));
        }

        return ColumnValue.Refused($"the query produces no column '{name}'");
    }

    public bool HasColumn(string name)
        => Columns.Any(c => string.Equals(c.Key, name, StringComparison.Ordinal))
           || (Open && !Removed.Contains(name));

    /// <summary>Every source table this relation reads, base first.</summary>
    public IEnumerable<SourceTable> Tables()
    {
        if (Base is not null)
        {
            yield return Base;
        }

        foreach (var join in Joins)
        {
            foreach (var table in join.Right.Tables())
            {
                yield return table;
            }
        }
    }
}

/// <summary>Renders relational pieces as T-SQL, with one alias per <see cref="SourceTable"/> instance.</summary>
internal sealed class SqlRenderer
{
    private readonly Dictionary<SourceTable, string> _aliases = new(ReferenceEqualityComparer.Instance);
    private readonly string _aliasPrefix;

    public SqlRenderer(string aliasPrefix = "t") => _aliasPrefix = aliasPrefix;

    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string TextLiteral(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public string Alias(SourceTable table)
    {
        if (!_aliases.TryGetValue(table, out var alias))
        {
            alias = _aliasPrefix + _aliases.Count.ToString(CultureInfo.InvariantCulture);
            _aliases[table] = alias;
        }

        return alias;
    }

    public string Scalar(SqlScalar scalar) => scalar switch
    {
        SqlColumn column => Quote(Alias(column.Table)) + "." + Quote(column.Column),
        SqlText text => TextLiteral(text.Value),
        SqlNumber number => number.Value,
        SqlNull => "NULL",
        SqlConcat concat => "(" + string.Join(" + ", concat.Parts.Select(ConcatPart)) + ")",
        SqlArithmetic { Operator: "/" } division =>
            $"(CAST({Scalar(division.Left)} AS float) / NULLIF({Scalar(division.Right)}, 0))",
        SqlArithmetic arithmetic => $"({Scalar(arithmetic.Left)} {arithmetic.Operator} {Scalar(arithmetic.Right)})",
        SqlNegate negate => $"(-{Scalar(negate.Operand)})",
        SqlCast cast => $"CAST({Scalar(cast.Operand)} AS {cast.SqlType})",
        _ => throw new InvalidOperationException($"no rendering for {scalar.GetType().Name}"),
    };

    // A literal part is already text; any other part is converted, since Power Query only concatenates text.
    private string ConcatPart(SqlScalar part)
        => part is SqlText ? Scalar(part) : $"CAST({Scalar(part)} AS nvarchar(4000))";

    /// <summary>The FROM-clause form of a relation: its base, and its joins parenthesized with it when it has any.</summary>
    public string Relation(Relation relation)
    {
        if (relation.Base is null)
        {
            throw new InvalidOperationException("a refused relation has no rendering");
        }

        var builder = new StringBuilder();
        builder.Append(Quote(relation.Base.Database)).Append('.').Append(Quote(relation.Base.Schema)).Append('.')
            .Append(Quote(relation.Base.Name)).Append(" AS ").Append(Quote(Alias(relation.Base)));
        if (relation.Joins.Count == 0)
        {
            return builder.ToString();
        }

        foreach (var join in relation.Joins)
        {
            builder.Append(join.Inner ? " INNER JOIN " : " LEFT JOIN ")
                .Append(Relation(join.Right))
                .Append(" ON ")
                .Append(Condition(join.On));
        }

        return "(" + builder + ")";
    }

    public string Condition(IReadOnlyList<(SqlScalar Left, SqlScalar Right)> pairs)
        => string.Join(" AND ", pairs.Select(p => $"{Scalar(p.Left)} = {Scalar(p.Right)}"));
}
