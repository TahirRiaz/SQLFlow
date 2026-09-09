namespace SqlFlow.Core.SourceControl;

/// <summary>
/// The canonical set of database object categories the source-control scripter captures, in dependency-friendly
/// order, each the folder name a scripted object lands under (the legacy <c>SmoHelper.ScriptFolders</c> layout:
/// one folder per type, one file per object). This list is the single source of truth shared by the YAML loader
/// (which validates an <c>include</c>/<c>exclude</c> filter against it) and the SMO scripter (which maps each
/// name to its catalog collection), so the two can never drift. Names are compared case-insensitively.
/// </summary>
public static class SourceControlObjectTypes
{
    /// <summary>The folder name used for a table's scripted data rows (legacy <c>Data</c> folder), kept distinct
    /// from the schema-only <c>Table</c> folder so a data snapshot never collides with the table definition.</summary>
    public const string DataFolder = "Data";

    /// <summary>Every captured object category, in the order the scripter walks them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "Schema",
        "UserDefinedDataType",
        "UserDefinedType",
        "XmlSchemaCollection",
        "Sequence",
        "PartitionFunction",
        "PartitionScheme",
        "Table",
        "View",
        "StoredProcedure",
        "UserDefinedFunction",
        "UserDefinedAggregate",
        "UserDefinedTableType",
        "Synonym",
        "Rule",
        "Default",
        "DatabaseDdlTrigger",
        "FullTextCatalog",
        "SecurityPolicy",
    ];

    /// <summary>The schemas a snapshot skips unless the flow says otherwise: the engine's staging schema
    /// (<see cref="Ingestion.StagingConventions.SchemaName"/>), whose per-flow work tables are rebuilt and
    /// dropped by every run and so belong to no database's tracked definition.</summary>
    public static IReadOnlyList<string> DefaultExcludedSchemas { get; } = [Ingestion.StagingConventions.SchemaName];

    private static readonly HashSet<string> Known = new(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="name"/> is one of the captured categories (case-insensitive).</summary>
    public static bool IsKnown(string name) => Known.Contains(name);
}
