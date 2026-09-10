using SqlFlow.Core.Acquire;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Export;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer;

namespace SqlFlow.Translate;

/// <summary>
/// The without-database composition root for translation flows: builds a fully wired
/// <see cref="TranslateFlowRunner"/> from a flow document's own connection declarations, with no control
/// database anywhere. The same resolver composition as full mode runs behind an in-memory store; the source is
/// SQL Server by the document loader's guarantee, so the built-in SQL Server provider set is all the resolver
/// needs. The caller supplies the write destinations (the cloud destination lives in an Azure assembly this one
/// does not reference, so it arrives as the Core <see cref="IExportDestination"/> abstraction, exactly as the
/// export flow's does); when none are given the runner defaults to local only. The secret resolver is required:
/// the invoke step's auth and header references resolve through it.
/// </summary>
public static class WithoutDatabaseTranslate
{
    public static TranslateFlowRunner BuildRunner(
        IEnumerable<DataSource> connections,
        ISecretResolver secrets,
        IReadOnlyList<IExportDestination>? destinations = null,
        Func<AcquireReliability, HttpClient>? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(secrets);

        var registry = SqlServerSourceProvider.CreateRegistry();
        return new TranslateFlowRunner(
            WithoutDatabaseResolver.Build(connections, secrets, registry),
            secrets,
            destinations,
            httpClientFactory);
    }
}
