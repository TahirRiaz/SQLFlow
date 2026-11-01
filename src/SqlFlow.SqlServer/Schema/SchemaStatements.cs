namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// The one statement that makes sure a schema exists. A guard alone is a race: flows running side by side on a fresh database
/// all find the schema missing, and each one but the first fails with "There is already an object named ...". So the create is
/// tried, and a failure stands only when the schema is still not there. CREATE SCHEMA must begin its own batch, so it is
/// wrapped in EXEC, which also lets it run inside a shared DDL transaction.
/// </summary>
public static class SchemaStatements
{
    /// <summary>The statement for <paramref name="schema"/>, safe to run from any number of connections at once.</summary>
    public static string Ensure(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var literal = schema.Replace("'", "''", StringComparison.Ordinal);
        var quoted = schema.Replace("]", "]]", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
        return $"IF SCHEMA_ID(N'{literal}') IS NULL BEGIN TRY EXEC(N'CREATE SCHEMA [{quoted}]'); END TRY BEGIN CATCH IF SCHEMA_ID(N'{literal}') IS NULL THROW; END CATCH;";
    }
}
