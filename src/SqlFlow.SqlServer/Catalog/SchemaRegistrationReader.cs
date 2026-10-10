using Microsoft.Data.SqlClient;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.SchemaRegistration;

namespace SqlFlow.SqlServer.Catalog;

/// <summary>What a schema registration read from one database.</summary>
public sealed record SchemaRegistrationRead
{
    public required string Database { get; init; }

    public required IReadOnlyList<RegisteredObject> Objects { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Reads what a schema registration flow (flowType: sch) registers: the tables and views of one database in scope,
/// each with its columns, primary key, and script. The one reader behind both the flow's own run and a connected
/// catalog sync, so a registration is identical whichever of them performed it.
/// </summary>
public static class SchemaRegistrationReader
{
    public static async Task<SchemaRegistrationRead> ReadAsync(
        string connectionString, SchemaRegistrationScope scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scope);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var harvest = await SqlServerObjectHarvester.HarvestAsync(
            connection, new ObjectHarvestRequest { IncludeProgrammability = false, Scope = scope }, ct).ConfigureAwait(false);

        var viewDefinitions = harvest.Modules.ToDictionary(
            m => m.Schema + "|" + m.Name, m => m.Definition, StringComparer.OrdinalIgnoreCase);

        var warnings = new List<string>();
        var objects = new List<RegisteredObject>(harvest.Objects.Count);
        foreach (var item in harvest.Objects)
        {
            string? script;
            if (item.Kind == LineageNodeKind.View)
            {
                viewDefinitions.TryGetValue(item.Schema + "|" + item.Name, out script);
                if (script is null)
                {
                    warnings.Add(
                        $"view '{harvest.Database}.{item.Schema}.{item.Name}' is encrypted (WITH ENCRYPTION); it is "
                        + "registered with its columns but without its definition.");
                }
            }
            else
            {
                script = item.TableScript;
            }

            objects.Add(new RegisteredObject
            {
                Schema = item.Schema,
                Name = item.Name,
                Kind = item.Kind,
                Columns = item.Columns,
                KeyColumns = item.PrimaryKeyColumns,
                Script = script,
            });
        }

        return new SchemaRegistrationRead { Database = harvest.Database, Objects = objects, Warnings = warnings };
    }
}
