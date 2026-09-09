using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>What a discovered path points at, in format-agnostic terms.</summary>
public enum SchemaPathKind
{
    /// <summary>A scalar leaf (JSON value, XML element text, or attribute) that becomes a column.</summary>
    Value,

    /// <summary>An object / element container; a target for root/keep-as-string/exclude rules.</summary>
    Container,

    /// <summary>A JSON array or repeating XML element; becomes a column, or a target for explode.</summary>
    Repeating,
}

/// <summary>One discovered path with what it points at, the column it would become, and its record count.</summary>
public sealed record SchemaPath(string Path, SchemaPathKind Kind, int RecordCount, string Column);

/// <summary>Every addressable path found across a sample, format-agnostic.</summary>
public sealed record SchemaInventory(int FilesScanned, int RecordsScanned, IReadOnlyList<SchemaPath> Paths);

/// <summary>One output column of a flatten formula and the source path it comes from.</summary>
public sealed record SchemaColumn(string Name, string SourcePath, bool IsLargeText);

/// <summary>The resolved output columns and the collision mappings that keep the flatten lossless.</summary>
public sealed record SchemaFormula(IReadOnlyList<SchemaColumn> Columns, IReadOnlyDictionary<string, string> CollisionMappings);

/// <summary>The full introspection of a flattenable source: its structure, the formula, and the options to reproduce it.</summary>
public sealed record FlattenIntrospection(
    string SourceType,
    SchemaInventory Inventory,
    SchemaFormula Formula,
    IReadOnlyList<KeyValuePair<string, string>> Options)
{
    /// <summary>
    /// The record grain (JSON rootPath or XML rowXPath) that statistics-driven detection chose because the
    /// caller gave no explicit one, or null when the caller pinned the grain or nothing was detected. Present so
    /// the CLI can tell the user its rows came from an auto-detected anchor rather than the document root.
    /// </summary>
    public string? AutoDetectedGrain { get; init; }
}

/// <summary>
/// Implemented by the path-based flatten readers (JSON, XML) so the <c>paths</c>, <c>flatten</c>, and
/// <c>discover</c> commands can explore any of them through one code path: scan the source, report its path
/// structure, and derive the flatten formula (columns + collision-resolved mappings + the options to run it).
/// </summary>
public interface IFlattenIntrospector
{
    /// <summary>True if this introspector handles the given source type (e.g. "json", "xml").</summary>
    bool CanHandle(string sourceType);

    /// <summary>Scans the source and returns its structure, derived formula, and reproducing options.</summary>
    Task<FlattenIntrospection> IntrospectAsync(SourceSpec source, int maxFiles, int maxRecords, int maxDepth, CancellationToken ct = default);
}
