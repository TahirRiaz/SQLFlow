using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Schema;

/// <summary>The desired target schema plus the source-to-target column-name map for the bulk copy.</summary>
public sealed record DesiredSchema
{
    public required IReadOnlyList<SqlColumn> Columns { get; init; }

    /// <summary>Maps each bulk-copied source column name to its target name (after cleanup). Virtual,
    /// system, hash, and identity columns are computed, not bulk-copied, so they are not in this map.</summary>
    public required IReadOnlyDictionary<string, string> SourceToTargetNames { get; init; }
}

/// <summary>
/// Builds the desired target schema for an ingestion flow from the detected (shaped) source columns: applies
/// column-name cleanup and unicode conversion, marks virtual columns, injects the system, hash key, and
/// (target only) identity columns, and orders columns the legacy way (PK-prefixed first, PK-suffixed next,
/// ordinary source columns, then the _DW columns last). Pure; one builder serves both the staging and target
/// passes (forStaging toggles only the identity injection).
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

        var virtualByName = flow.VirtualColumns
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .GroupBy(v => v.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<SqlColumn>(sourceColumns.Count);

        for (var i = 0; i < sourceColumns.Count; i++)
        {
            var source = sourceColumns[i];
            var targetName = cleanedNames[i];
            var type = flow.SchemaSync.ConvertUnicodeToNonUnicode ? UnicodeConverter.ToNonUnicode(source.DataType) : source.DataType;

            if (virtualByName.TryGetValue(source.Name, out var virtualColumn))
            {
                columns.Add(source with
                {
                    Name = targetName,
                    DataType = type,
                    Role = ColumnRole.Virtual,
                    Origin = ColumnOrigin.Computed,
                    SelectExpression = virtualColumn.SelectExpression,
                });
            }
            else
            {
                map[source.Name] = targetName;
                columns.Add(source with
                {
                    Name = targetName,
                    DataType = type,
                    Role = ColumnRole.Source,
                    Origin = ColumnOrigin.BulkCopied,
                });
            }
        }

        AddSystemColumns(columns, flow.SystemColumns);
        AddScd2Columns(columns, flow.Versioning.Scd2);
        AddHashKey(columns, flow.Change);
        if (!forStaging)
        {
            AddIdentity(columns, flow.Target.IdentityColumn);
        }

        return new DesiredSchema { Columns = Order(columns), SourceToTargetNames = map };
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

    private static bool Exists(List<SqlColumn> columns, string name)
        => columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

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
