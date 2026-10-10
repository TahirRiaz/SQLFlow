using SqlFlow.Core.Connections;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.SchemaRegistration;
using SqlFlow.SqlServer.Catalog;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// The connected pass for schema registration flows (flowType: sch): reads each registered database's tables and
/// views and hands them to the graph builder as catalog objects (with columns and, for a view, its definition), a
/// derived script per table, the primary keys, and the registered-object list the builder attributes to the flow
/// and resolves registered-source subscribers against. An unreachable database degrades to a warning and marks its
/// server degraded, so what an earlier pass registered for it is kept.
/// </summary>
public sealed class SchemaRegistrationCollector
{
    private readonly IConnectionResolver _resolver;
    private readonly Func<string, CancellationToken, Task>? _progress;

    public SchemaRegistrationCollector(IConnectionResolver resolver, Func<string, CancellationToken, Task>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _progress = progress;
    }

    public async Task<CollectionResult> CollectAsync(
        IReadOnlyList<CollectedSchemaRegistration> registrations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var merged = new CollectionResult();
        foreach (var registration in registrations.OrderBy(r => r.Flow, StringComparer.OrdinalIgnoreCase))
        {
            if (registration.Kind is not (DataSourceKind.MSSQL or DataSourceKind.AZDB))
            {
                merged.Warnings.Add(
                    $"schema registration '{registration.Flow}': server '{registration.ServerRef}' is {registration.Kind}; "
                    + "registration reads SQL Server only.");
                continue;
            }

            try
            {
                await ReportAsync($"schema registration '{registration.Flow}': reading server '{registration.ServerRef}'.", ct)
                    .ConfigureAwait(false);
                var resolved = await _resolver.ResolveAsync(registration.RawReference, ConnectionRole.Source, ct: ct)
                    .ConfigureAwait(false);
                var read = await SchemaRegistrationReader.ReadAsync(resolved.CanonicalString, registration.Scope, ct)
                    .ConfigureAwait(false);
                Add(merged, registration.Flow, registration.ServerRef, read.Database, read.Objects);
                merged.Warnings.AddRange(read.Warnings.Select(w => $"schema registration '{registration.Flow}': {w}"));
                await ReportAsync(
                    $"schema registration '{registration.Flow}' (db '{read.Database}'): {read.Objects.Count} table(s) and view(s) registered.",
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reason = Core.Secrets.SecretHygiene.RedactedMessage(ex);
                merged.Warnings.Add(
                    $"schema registration '{registration.Flow}': server '{registration.ServerRef}' could not be read "
                    + $"({reason}); what it registered before is kept.");
                merged.DegradedServers.Add(registration.ServerRef);
                await ReportAsync($"schema registration '{registration.Flow}' FAILED ({reason}).", ct).ConfigureAwait(false);
            }
        }

        return merged;
    }

    /// <summary>Folds one registration's objects into <paramref name="result"/> in the shapes the builder consumes.</summary>
    public static void Add(
        CollectionResult result, string flow, string serverRef, string database, IReadOnlyList<RegisteredObject> objects)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(objects);

        foreach (var item in objects)
        {
            result.CatalogObjects.Add(new CatalogObject
            {
                ServerRef = serverRef,
                Database = database,
                Schema = item.Schema,
                Name = item.Name,
                Kind = item.Kind,
                Columns = item.Columns,
                Definition = item.Kind == LineageNodeKind.View ? item.Script : null,
            });

            if (item.Kind == LineageNodeKind.Table && item.Script is not null)
            {
                result.ObjectArtifacts.Add(new CollectedObjectArtifact
                {
                    ServerRef = serverRef,
                    Database = database,
                    Schema = item.Schema,
                    Name = item.Name,
                    Kind = LineageNodeKind.Table,
                    Script = item.Script,
                    Tier = LineageTier.Derived,
                });
            }

            if (item.KeyColumns.Count > 0)
            {
                result.KeyHints.Add(new CollectedKeyHint
                {
                    Table = new ModelObjectRef
                    {
                        ServerRef = serverRef,
                        Database = database,
                        Schema = item.Schema,
                        Name = item.Name,
                    },
                    Columns = item.KeyColumns,
                    Origin = LineageModelOrigin.Constraint,
                    Tier = LineageTier.Derived,
                });
            }

            result.RegisteredObjects.Add(new CollectedRegisteredObject
            {
                Flow = flow,
                ServerRef = serverRef,
                Database = database,
                Schema = item.Schema,
                Name = item.Name,
            });
        }
    }

    private Task ReportAsync(string message, CancellationToken ct)
        => _progress is null ? Task.CompletedTask : _progress(message, ct);
}
