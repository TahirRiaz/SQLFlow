using Microsoft.Data.SqlClient;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>
/// Creates a flow's declared indexes on its target (the <c>trgDesiredIndex</c> design) by executing
/// the authored <c>CREATE INDEX</c> statements. The engine calls this only for a freshly created
/// table, so there is nothing to diff: each declared index is created and its outcome recorded. A
/// failure on one index is captured as a <see cref="IndexActionKind.Failed"/> result (with the SQL
/// error) and does not stop the remaining indexes, so the run trace shows exactly which indexes
/// succeeded and which did not.
/// </summary>
public sealed class SqlServerDesiredIndexManager : IDesiredIndexManager
{
    public async Task<IReadOnlyList<IndexAction>> ApplyAsync(string connectionString, string desiredIndexScript, CancellationToken ct = default)
    {
        var indexes = DesiredIndexParser.Parse(desiredIndexScript);
        if (indexes.Count == 0)
        {
            return [];
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var actions = new List<IndexAction>(indexes.Count);
        foreach (var index in indexes)
        {
            try
            {
                // Building an index on a freshly loaded table is unbounded work; the server decides when it ends.
                await using var command = new SqlCommand(index.StatementText, connection) { CommandTimeout = 0 };
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                actions.Add(new IndexAction { IndexName = index.Name, Table = index.Table, Kind = IndexActionKind.Created, Sql = index.StatementText });
            }
            catch (SqlException ex)
            {
                actions.Add(new IndexAction
                {
                    IndexName = index.Name,
                    Table = index.Table,
                    Kind = IndexActionKind.Failed,
                    Detail = ex.Message,
                    Sql = index.StatementText,
                });
            }
        }

        return actions;
    }
}
