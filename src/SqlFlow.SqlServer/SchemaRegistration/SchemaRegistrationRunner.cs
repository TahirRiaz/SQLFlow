using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Core.SchemaRegistration;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer.Catalog;

namespace SqlFlow.SqlServer.SchemaRegistration;

/// <summary>
/// Executes a schema registration flow (flowType: sch): reads the registered database's tables and views through
/// <see cref="SchemaRegistrationReader"/> and returns them as the run product the catalog records. It writes nothing
/// to the source database and never throws for a source failure: the failure comes back on the result.
/// </summary>
public sealed class SchemaRegistrationRunner
{
    private readonly IConnectionResolver _resolver;

    public SchemaRegistrationRunner(IConnectionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    public async Task<SchemaRegistrationResult> RunAsync(
        SchemaRegistrationFlow flow, IngestionRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;
        var events = options.Events ?? NullRunEventSink.Instance;

        try
        {
            var scope = flow.Scope;
            events.Log(RunLogLevel.Info, "run.start",
                $"schema registration '{flow.SysAlias}' (flow {flow.FlowId}): database "
                + $"{scope.Database ?? "(connection default)"} on '{flow.Server}'"
                + (scope.IncludeSchemas.Count > 0 ? $", schemas {string.Join(", ", scope.IncludeSchemas)}" : string.Empty)
                + (scope.ExcludeSchemas.Count > 0 ? $", excluding {string.Join(", ", scope.ExcludeSchemas)}" : string.Empty));

            var resolved = await _resolver.ResolveAsync(flow.ConnectionReference, ConnectionRole.Source, ct: ct)
                .ConfigureAwait(false);
            var read = await SchemaRegistrationReader.ReadAsync(resolved.CanonicalString, scope, ct).ConfigureAwait(false);

            var tables = read.Objects.Count(o => o.Kind == LineageNodeKind.Table);
            var views = read.Objects.Count(o => o.Kind == LineageNodeKind.View);
            var columns = read.Objects.Sum(o => o.Columns.Count);
            foreach (var warning in read.Warnings)
            {
                events.Log(RunLogLevel.Info, "schema.read", "WARN " + warning);
            }

            events.Log(RunLogLevel.Info, "schema.read",
                $"database '{read.Database}': {tables} table(s), {views} view(s), {columns} column(s) registered");

            var endUtc = DateTime.UtcNow;
            events.Log(RunLogLevel.Info, "run.end",
                $"SUCCESS in {Math.Round((endUtc - startUtc).TotalSeconds, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)}s");
            return new SchemaRegistrationResult
            {
                RunId = runId,
                FlowName = flow.SysAlias,
                Success = true,
                Database = read.Database,
                Objects = read.Objects,
                Tables = tables,
                Views = views,
                Columns = columns,
                Warnings = read.Warnings,
                StartedUtc = startUtc,
                EndedUtc = endUtc,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = SecretHygiene.RedactedMessage(ex);
            var endUtc = DateTime.UtcNow;
            events.Log(RunLogLevel.Info, "run.end", $"FAILED: {error}");
            return new SchemaRegistrationResult
            {
                RunId = runId,
                FlowName = flow.SysAlias,
                Success = false,
                Error = error,
                StartedUtc = startUtc,
                EndedUtc = endUtc,
            };
        }
    }
}

/// <summary>The without-database composition of <see cref="SchemaRegistrationRunner"/>: the document's own
/// connections behind the shared resolver.</summary>
public static class WithoutDatabaseSchemaRegistration
{
    public static SchemaRegistrationRunner BuildRunner(IEnumerable<DataSource> connections, ISecretResolver? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return new SchemaRegistrationRunner(
            WithoutDatabaseResolver.Build(connections, secrets, SqlServerSourceProvider.CreateRegistry()));
    }
}
