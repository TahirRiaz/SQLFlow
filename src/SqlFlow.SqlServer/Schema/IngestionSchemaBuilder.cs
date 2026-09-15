using SqlFlow.Core;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Schema;

/// <summary>The desired target schema plus the source read that fills staging.</summary>
public sealed record DesiredSchema
{
    public required IReadOnlyList<SqlColumn> Columns { get; init; }

    /// <summary>The source read in select-list order: every data column the bulk copy lands in staging. A source
    /// column reads as itself and a virtual column as its expression; system, hash, and identity columns are
    /// computed on the target, so they are not projections.</summary>
    public required IReadOnlyList<SourceProjection> Projections { get; init; }

    /// <summary>Maps each projection's source-side name to its target name (after cleanup), for resolving the
    /// column names a flow declares (keys, dates, datasets) onto the target.</summary>
    public required IReadOnlyDictionary<string, string> SourceToTargetNames { get; init; }
}

/// <summary>
/// Builds the desired target schema for an ingestion flow from the detected (shaped) source columns: applies
/// column-name cleanup and unicode conversion, projects the virtual columns into the source read, injects the
/// system, hash key, and (target only) identity columns, and orders columns the legacy way (PK-prefixed first,
/// PK-suffixed next, ordinary columns, then the _DW columns last). Pure; one builder serves both the staging and
/// target passes (forStaging toggles only the identity injection).
/// </summary>
public sealed class IngestionSchemaBuilder
{
    private readonly IColumnNameCleaner _cleaner;

    public IngestionSchemaBuilder(IColumnNameCleaner cleaner)
    {
        ArgumentNullException.ThrowIfNull(cleaner);
        _cleaner = cleaner;
    }

    public DesiredSchema Build(IReadOnlyList<SqlColumn> sourceColumns, IngestionFlow flow, bool forStaging)
    {
        ArgumentNullException.ThrowIfNull(sourceColumns);
        ArgumentNullException.ThrowIfNull(flow);

        var rawNames = sourceColumns.Select(c => c.Name).ToList();
        var cleanedNames = _cleaner.Clean(rawNames, flow.SchemaSync);
        var virtuals = NamedVirtualColumns(flow);
        var virtualByName = virtuals.ToDictionary(v => v.Name, v => v.Column, StringComparer.OrdinalIgnoreCase);

        var columns = new List<SqlColumn>(sourceColumns.Count + virtuals.Count);
        var projections = new List<SourceProjection>(sourceColumns.Count + virtuals.Count);
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sourceColumns.Count; i++)
        {
            var source = sourceColumns[i];
            var targetName = cleanedNames[i];
            var type = flow.SchemaSync.ConvertUnicodeToNonUnicode ? UnicodeConverter.ToNonUnicode(source.DataType) : source.DataType;

            if (virtualByName.TryGetValue(source.Name, out var virtualColumn))
            {
                // The declaration replaces this source column's value: the read projects the expression under the
                // column's own name, so it lands in the same staging and target column. A declared type wins;
                // without one the column keeps the type the source introspected.
                replaced.Add(source.Name);
                columns.Add(source with
                {
                    Name = targetName,
                    DataType = DeclaredType(source.Name, virtualColumn) ?? type,
                    Role = ColumnRole.Virtual,
                    Origin = ColumnOrigin.BulkCopied,
                    SelectExpression = virtualColumn.SelectExpression,
                });
                projections.Add(new SourceProjection { SourceName = source.Name, TargetName = targetName, Expression = virtualColumn.SelectExpression });
            }
            else
            {
                columns.Add(source with
                {
                    Name = targetName,
                    DataType = type,
                    Role = ColumnRole.Source,
                    Origin = ColumnOrigin.BulkCopied,
                });
                projections.Add(new SourceProjection { SourceName = source.Name, TargetName = targetName });
            }
        }

        foreach (var (name, virtualColumn) in virtuals)
        {
            if (replaced.Contains(name))
            {
                continue;
            }

            // Not a column of the source: it exists only because of the declaration, so it takes the declared
            // type, the way legacy flw.IngestionVirtual added it to the target as its DataTypeExp.
            var declared = DeclaredType(name, virtualColumn) ?? throw new SqlFlowException(
                $"Virtual column '{name}' is not a column of source {flow.Source.Table.QualifiedName}, so it needs a " +
                "dataTypeExpression (or dataType), for example 'int' or 'varchar(100)', to create it in staging and the target.");
            if (columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlFlowException(
                    $"Virtual column '{name}' has the cleaned name of a source column of {flow.Source.Table.QualifiedName}. " +
                    "Name it after the raw source column to replace that column's value, or give it a distinct name.");
            }

            columns.Add(new SqlColumn
            {
                Name = name,
                DataType = declared,
                IsNullable = true,
                Role = ColumnRole.Virtual,
                Origin = ColumnOrigin.BulkCopied,
                SelectExpression = virtualColumn.SelectExpression,
            });
            projections.Add(new SourceProjection { SourceName = name, TargetName = name, Expression = virtualColumn.SelectExpression });
        }

        AddSystemColumns(columns, flow.SystemColumns);
        AddScd2Columns(columns, flow.Versioning.Scd2);
        AddHashKey(columns, flow.Change);
        if (!forStaging)
        {
            AddIdentity(columns, flow.Target.IdentityColumn);
        }

        // Assigned, not added: two source columns whose names differ only by case (Dup, dup) share one
        // case-insensitive entry, the later one winning, exactly as before virtual columns were projected.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projection in projections)
        {
            map[projection.SourceName] = projection.TargetName;
        }

        return new DesiredSchema
        {
            Columns = Order(columns),
            Projections = projections,
            SourceToTargetNames = map,
        };
    }

    // Every virtual column is read under its name, so the name is mandatory and unique. The YAML loader rejects
    // both mistakes at parse time; the control-database path reaches here unchecked, so the run guards them too.
    // Legacy rows spell the name bracketed ([VehicleNo_DW]); the brackets are not part of the name.
    private static List<(string Name, VirtualColumn Column)> NamedVirtualColumns(IngestionFlow flow)
    {
        var named = new List<(string Name, VirtualColumn Column)>(flow.VirtualColumns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < flow.VirtualColumns.Count; i++)
        {
            var column = flow.VirtualColumns[i];
            var name = string.IsNullOrWhiteSpace(column.Name) ? string.Empty : IngestionText.Unbracket(column.Name);
            if (name.Length == 0)
            {
                throw new SqlFlowException(
                    $"Virtual column {i + 1} of flow {flow.FlowId} has no name. A virtual column is read from the source as " +
                    "its expression aliased to its name, so the name is required.");
            }

            if (!seen.Add(name))
            {
                throw new SqlFlowException($"Virtual column '{name}' is declared more than once in flow {flow.FlowId}.");
            }

            named.Add((name, column));
        }

        return named;
    }

    // The declared type: DataTypeExpression first, because the legacy row keeps the full type there ("time(0)",
    // "varchar(150)") and only the bare family in DataType ("time", "varchar"). Null when neither is set.
    private static SqlDataType? DeclaredType(string name, VirtualColumn column)
    {
        var text = column.DataTypeExpression ?? column.DataType;
        if (text is null)
        {
            return null;
        }

        try
        {
            return SqlDataType.Parse(text);
        }
        catch (SqlFlowException ex)
        {
            var key = column.DataTypeExpression is null ? "dataType" : "dataTypeExpression";
            throw new SqlFlowException(
                $"Virtual column '{name}' declares {key} '{text}', which is not a SQL Server data type: {ex.Message}", ex);
        }
    }

    private static void AddSystemColumns(List<SqlColumn> columns, SystemColumnsPolicy policy)
    {
        // The audit stamp columns are datetime (not datetime2), matching the original SQLFlow arc/ods tables so
        // migrated targets are schema-identical to production.
        AddComputed(columns, policy.InsertedDate, "InsertedDate_DW", "datetime", ColumnRole.System);
        AddComputed(columns, policy.UpdatedDate, "UpdatedDate_DW", "datetime", ColumnRole.System);
        AddComputed(columns, policy.DeletedDate, "DeletedDate_DW", "datetime", ColumnRole.System);
        AddComputed(columns, policy.RowStatus, "RowStatus_DW", "char(1)", ColumnRole.System);
    }

    // The SCD2 period columns ride the same computed-system path as the _DW columns: nullable (so an ALTER ADD
    // onto an existing populated table succeeds), engine-maintained, sorted last. The backfill step stamps the
    // pre-existing rows the first time the flow runs with SCD2 on.
    private static void AddScd2Columns(List<SqlColumn> columns, Scd2Policy scd2)
    {
        if (!scd2.Enabled)
        {
            return;
        }

        AddComputed(columns, true, scd2.ValidFromColumn, "datetime2(3)", ColumnRole.System);
        AddComputed(columns, true, scd2.ValidToColumn, "datetime2(3)", ColumnRole.System);
        AddComputed(columns, true, scd2.CurrentFlagColumn, "bit", ColumnRole.System);
    }

    private static void AddHashKey(List<SqlColumn> columns, ChangePolicy change)
    {
        if (!change.HasHashKey || Exists(columns, HashKey.ColumnName))
        {
            return;
        }

        columns.Add(new SqlColumn
        {
            Name = HashKey.ColumnName,
            DataType = HashKey.BinaryTypeFor(change.HashType),
            IsNullable = true,
            Role = ColumnRole.HashKey,
            Origin = ColumnOrigin.Computed,
        });
    }

    private static void AddIdentity(List<SqlColumn> columns, string? identityColumn)
    {
        if (string.IsNullOrWhiteSpace(identityColumn) || Exists(columns, identityColumn))
        {
            return;
        }

        columns.Add(new SqlColumn
        {
            Name = identityColumn,
            DataType = SqlDataType.Parse("int"),
            IsNullable = false,
            Role = ColumnRole.Identity,
            Origin = ColumnOrigin.Computed,
            IsIdentity = true,
            IsPrimaryKey = true,
        });
    }

    private static void AddComputed(List<SqlColumn> columns, bool enabled, string name, string type, ColumnRole role)
    {
        if (!enabled || Exists(columns, name))
        {
            return;
        }

        columns.Add(new SqlColumn
        {
            Name = name,
            DataType = SqlDataType.Parse(type),
            IsNullable = true,
            Role = role,
            Origin = ColumnOrigin.Computed,
        });
    }

    // Whether an engine-maintained column is already present. A SOURCE column of that name is kept (a view may
    // legitimately carry its own UpdatedDate_DW); a VIRTUAL column of that name is refused, because the engine
    // would silently drop either the declaration's value or its own maintenance of the column.
    private static bool Exists(List<SqlColumn> columns, string name)
    {
        var existing = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is { Role: ColumnRole.Virtual })
        {
            throw new SqlFlowException(
                $"Virtual column '{existing.Name}' has the name of the engine-maintained column '{name}' this flow enables; " +
                "rename the virtual column or turn the engine column off.");
        }

        return existing is not null;
    }

    private static IReadOnlyList<SqlColumn> Order(List<SqlColumn> columns)
    {
        static int Rank(SqlColumn column)
        {
            // The _DW system/hash columns always sort last, even if a name would otherwise match a PK rule.
            if (column.Name.EndsWith("_DW", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (column.Name.StartsWith("PK", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return column.Name.EndsWith("PK", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        }

        return columns
            .Select((column, index) => (column, index))
            .OrderBy(x => Rank(x.column))
            .ThenBy(x => x.index)
            .Select(x => x.column)
            .ToList();
    }
}
