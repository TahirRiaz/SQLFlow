namespace SqlFlow.SourceControl;

/// <summary>
/// One scripted database object: its category folder, its schema and name, the repository-relative path its
/// <c>.sql</c> file occupies, and the generated DDL (or data) text. The relative path is forward-slashed and
/// rooted at the database name, exactly the on-disk layout that gets committed, so the snapshot writer and the
/// git layer never re-derive it.
/// </summary>
public sealed record ScriptedObject
{
    /// <summary>The object category and folder name (for example <c>Table</c>, <c>View</c>, <c>Data</c>).</summary>
    public required string Folder { get; init; }

    /// <summary>The object's schema, or null for schema-less objects (a schema, a database DDL trigger).</summary>
    public string? Schema { get; init; }

    public required string Name { get; init; }

    /// <summary>The repository-relative, forward-slashed path: <c>&lt;database&gt;/&lt;folder&gt;/&lt;file&gt;.sql</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The generated script, line endings normalized to LF with a single trailing newline so an
    /// unchanged object produces byte-identical text every run (no spurious git diffs).</summary>
    public required string Sql { get; init; }
}

/// <summary>
/// The full scripted snapshot of one database: every captured object plus any warnings (an encrypted module, a
/// category that could not be enumerated). The database name roots the on-disk folder, matching the legacy
/// <c>ScriptToPath/DBName/...</c> layout.
/// </summary>
public sealed record ScriptedDatabase
{
    public required string DatabaseName { get; init; }

    public required IReadOnlyList<ScriptedObject> Objects { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
