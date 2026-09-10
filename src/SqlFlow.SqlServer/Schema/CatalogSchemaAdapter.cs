using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Bridges a live <see cref="CatalogObject"/> (from <see cref="ICatalogReader"/>) into the
/// <see cref="SqlColumn"/> list the <see cref="SchemaEvolutionPlanner"/> diffs against. For a SQL Server
/// object the native type strings round-trip through <see cref="SqlDataType.Parse"/>; for a foreign source
/// (MySQL, PostgreSQL) the provider's <see cref="ISourceTypeMapper"/> translates each native type into the
/// SQL Server type that will hold it, and an unmappable type throws rather than degrading into an opaque
/// type the evolution planner cannot reason about. This is the single point where introspection feeds
/// evolution.
/// </summary>
public static class CatalogSchemaAdapter
{
    public static IReadOnlyList<SqlColumn> ToColumns(CatalogObject catalogObject)
    {
        ArgumentNullException.ThrowIfNull(catalogObject);

        return catalogObject.Columns
            .OrderBy(c => c.Ordinal)
            .Select(ToSqlColumn)
            .ToList();
    }

    private static SqlColumn ToSqlColumn(CatalogColumn c) => new()
    {
        Name = c.Name,
        DataType = SqlDataType.Parse(c.NativeType),
        IsNullable = c.IsNullable,
        IsIdentity = c.IsIdentity,
        IsPrimaryKey = c.IsPrimaryKeyMember,
    };

    /// <summary>
    /// The live columns that schema evolution may reason about: everything except the SYSTEM_TIME period
    /// columns of a temporal table. Those are GENERATED ALWAYS, so they can never be written, altered by the
    /// evolution planner, or diffed against a desired schema that (correctly) does not contain them. Leaving
    /// them in would make every run report two phantom extra-column drift findings and, worse, would let them
    /// reach the change-detection type map, where a target column with no staging counterpart has no meaning.
    /// </summary>
    public static IReadOnlyList<SqlColumn> ToEvolvableColumns(CatalogObject catalogObject)
    {
        ArgumentNullException.ThrowIfNull(catalogObject);

        return catalogObject.Columns
            .Where(c => c.GeneratedAlways == GeneratedAlwaysKind.None)
            .OrderBy(c => c.Ordinal)
            .Select(ToSqlColumn)
            .ToList();
    }

    /// <summary>Maps each column's native source type to its SQL Server equivalent through the provider's
    /// type mapper before parsing, so staging and target DDL are always valid T-SQL.</summary>
    public static IReadOnlyList<SqlColumn> ToColumns(CatalogObject catalogObject, ISourceTypeMapper typeMapper)
    {
        ArgumentNullException.ThrowIfNull(catalogObject);
        ArgumentNullException.ThrowIfNull(typeMapper);

        return catalogObject.Columns
            .OrderBy(c => c.Ordinal)
            .Select(c => new SqlColumn
            {
                Name = c.Name,
                DataType = SqlDataType.Parse(typeMapper.ToSqlServerType(c)),
                IsNullable = c.IsNullable,
                IsIdentity = c.IsIdentity,
                IsPrimaryKey = c.IsPrimaryKeyMember,
            })
            .ToList();
    }
}
