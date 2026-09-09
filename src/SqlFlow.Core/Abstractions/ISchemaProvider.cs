using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>Introspects and mutates the target's schema.</summary>
public interface ISchemaProvider
{
    /// <summary>Returns the live schema of the table, or <c>null</c> if it does not exist.</summary>
    Task<TableSchema?> GetTableSchemaAsync(string connectionString, string schema, string table, CancellationToken ct = default);

    /// <summary>Executes DDL statements in a single transaction.</summary>
    Task ExecuteDdlAsync(string connectionString, IReadOnlyList<string> statements, CancellationToken ct = default);
}
