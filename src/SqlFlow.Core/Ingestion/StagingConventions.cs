namespace SqlFlow.Core.Ingestion;

/// <summary>
/// The naming conventions of the engine's own work objects, shared by the runners that create them and by the
/// features that must recognize them as engine-owned rather than user-authored. Keeping the schema name here
/// (rather than only on the runner that creates it) is what lets the source-control scripter skip staging
/// without taking a dependency on the SQL Server runner or restating the literal.
/// </summary>
public static class StagingConventions
{
    /// <summary>The schema hosting every flow's canonical staging and match-key work tables
    /// (<c>[raw].[&lt;targetSchema&gt;_&lt;targetTable&gt;_&lt;flowId&gt;]</c>). Its contents are rebuilt by each run and
    /// dropped on success, so they are transient by design and never part of a database's tracked definition.</summary>
    public const string SchemaName = "raw";
}
