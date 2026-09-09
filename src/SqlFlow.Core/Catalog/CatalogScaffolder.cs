using System.Text;

namespace SqlFlow.Core.Catalog;

/// <summary>Options for scaffolding a runnable ingestion-flow document from a discovered object.</summary>
public sealed record ScaffoldOptions
{
    /// <summary>The source connection reference embedded in the document. Only a <c>${...}</c> reference is
    /// ever embedded (a literal connection string could carry a secret); the caller substitutes a placeholder
    /// when the operator supplied a literal.</summary>
    public required string SourceConnection { get; init; }

    /// <summary>The YAML provider key of the source (mysql | postgres | azdb); null for SQL Server, which
    /// uses the plain-string connection form.</summary>
    public string? SourceProvider { get; init; }

    /// <summary>The target connection reference (same embedding rule as the source).</summary>
    public required string TargetConnection { get; init; }

    /// <summary>The target object, for example <c>raw.Customer</c> (schema.table on the target connection's
    /// database).</summary>
    public required string TargetObject { get; init; }

    /// <summary>The flow name; defaults to <c>schema-table</c>.</summary>
    public string? FlowName { get; init; }

    /// <summary>Explicit key columns. Empty means use the source's introspected primary key.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
}

/// <summary>
/// Turns a discovered <see cref="CatalogObject"/> into a RUNNABLE ingestion-flow document: a complete
/// <c>flowType: ing</c> YAML with the connections block, the source and target objects, and the key columns
/// taken from the introspected primary key, so <c>sqlflow run</c> accepts the file as written. Replication
/// helpers a human still reviews: candidate incremental date columns are suggested as comments, and the full
/// detected column inventory is appended for reference. No secret is ever written into the file.
/// </summary>
public static class CatalogScaffolder
{
    public static string ToIngestionYaml(CatalogObject source, ScaffoldOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var keys = options.KeyColumns.Count > 0
            ? options.KeyColumns
            : source.Columns.Where(c => c.IsPrimaryKeyMember).OrderBy(c => c.Ordinal).Select(c => c.Name).ToList();
        var flowName = options.FlowName ?? $"{source.Name.Schema}-{source.Name.Name}".ToLowerInvariant();
        var dateColumns = source.Columns
            .Where(c => IsDateLike(c.NativeType))
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Name)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("# Scaffolded by 'sqlflow catalog scaffold' from ").Append(Display(source.Name)).AppendLine(".");
        sb.AppendLine("# Review, then run:  sqlflow run <thisfile>");
        sb.Append("flowType: ing").AppendLine();
        sb.Append("name: ").AppendLine(flowName);
        sb.AppendLine("connections:");
        if (options.SourceProvider is null)
        {
            sb.Append("  src: ").AppendLine(options.SourceConnection);
        }
        else
        {
            sb.AppendLine("  src:");
            sb.Append("    provider: ").AppendLine(options.SourceProvider);
            sb.Append("    connection: ").AppendLine(options.SourceConnection);
        }

        sb.Append("  dwh: ").AppendLine(options.TargetConnection);
        sb.AppendLine("source:");
        sb.AppendLine("  server: src");
        sb.Append("  object: ").Append(source.Name.Schema).Append('.').AppendLine(source.Name.Name);
        sb.AppendLine("target:");
        sb.AppendLine("  server: dwh");
        sb.Append("  object: ").AppendLine(options.TargetObject);
        sb.AppendLine("load:");
        sb.Append("  keyColumns: [").Append(string.Join(", ", keys)).AppendLine("]");
        if (keys.Count == 0)
        {
            sb.AppendLine("  # no primary key detected: without keyColumns every run appends all rows.");
        }

        if (dateColumns.Count > 0)
        {
            sb.AppendLine("# Incremental loading (uncomment and pick the change-tracking column):");
            sb.AppendLine("# incremental:");
            sb.Append("#   columns: [").Append(dateColumns[0]).AppendLine("]");
            sb.AppendLine("#   overlapDays: 7");
            if (dateColumns.Count > 1)
            {
                sb.Append("#   candidates: ").AppendLine(string.Join(", ", dateColumns));
            }
        }

        sb.AppendLine();
        sb.AppendLine("# Detected source columns (name : type : nullability):");
        foreach (var column in source.Columns.OrderBy(c => c.Ordinal))
        {
            sb.Append("#   ").Append(column.Name)
              .Append(" : ").Append(column.NativeType)
              .Append(" : ").Append(column.IsNullable ? "NULL" : "NOT NULL")
              .AppendLine(column.IsPrimaryKeyMember ? "  [PK]" : string.Empty);
        }

        return sb.ToString();
    }

    private static bool IsDateLike(string nativeType)
    {
        var type = nativeType.ToLowerInvariant();
        return type.StartsWith("date", StringComparison.Ordinal)
            || type.StartsWith("smalldatetime", StringComparison.Ordinal)
            || type.StartsWith("timestamp", StringComparison.Ordinal);
    }

    private static string Display(ThreePartName name)
        => string.IsNullOrEmpty(name.Database) ? $"{name.Schema}.{name.Name}" : $"{name.Database}.{name.Schema}.{name.Name}";
}
