using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.State;
using SqlFlow.Azure;
using SqlFlow.DuckDb;
using SqlFlow.Providers;
using SqlFlow.Sources;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Schema;
using SqlFlow.Yaml;

namespace SqlFlow.Execution;

/// <summary>
/// The single registration of the SqlFlow engine into a dependency-injection container: source readers, the SQL
/// Server type/schema/ddl/bulk/index/incremental/state providers, the secret chain (Azure credential factory, the
/// env + Key Vault secret providers, the resolver), the cloud-storage credential provider, the connection registry
/// (provider canonicalizers, the kind-dispatching connection/catalog factories, the catalog service), the inference
/// stack, the full set of YAML loaders, the flow runner, and the shared <see cref="DocumentExecutor"/>. Every host
/// that runs flows (the CLI, the control plane, worker nodes) composes the engine through this one extension, so
/// there is exactly one engine wiring to maintain and a flow runs identically wherever it is hosted.
/// </summary>
public static class SqlFlowEngineServices
{
    /// <summary>
    /// Registers the complete SqlFlow engine. The caller still adds its own logging (and may override the
    /// <see cref="DocumentExecutor"/> registration afterwards to attach a host-specific warning sink: a later
    /// registration of the same service wins). The <see cref="DocumentExecutor"/> registered here has no warning
    /// sink, so engine-internal hygiene/history warnings are silent unless the host opts in.
    /// </summary>
    public static IServiceCollection AddSqlFlowEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IFileLifecycle, LocalFileLifecycle>();
        services.AddSingleton<IFileStore, LocalFileStore>();
        // Cloud object store: reads abfss/wasbs/https lake paths through the shared Azure credential (az login /
        // managed identity / service principal). Selected by CanHandle for Azure URIs; local paths stay on LocalFileStore.
        services.AddSingleton<IFileStore, AzureBlobFileStore>();
        services.AddSingleton<ISourceReader, CsvSourceReader>();
        services.AddSingleton<ISourceReader, XlsSourceReader>();
        services.AddSingleton<ISourceReader, JsonSourceReader>();
        services.AddSingleton<ISourceReader, XmlSourceReader>();
        services.AddSingleton<ISourceReader, ParquetSourceReader>();
        services.AddSingleton<ISourceReader, DuckDbSourceReader>();
        // Unified file discovery: one entry point that detects a source's format and generates its ingestion YAML
        // (flatten for JSON/XML, columnar for CSV/Excel/Parquet), over the readers and stores registered above.
        services.AddSingleton<SourceDiscoveryService>();
        services.AddSingleton<ISqlTypeMapper, SqlServerTypeMapper>();
        services.AddSingleton<ISchemaProvider, SqlServerSchemaProvider>();
        services.AddSingleton<IColumnTypeReconciler, SqlServerColumnTypeReconciler>();
        services.AddSingleton<IDdlGenerator, SqlServerDdlGenerator>();
        services.AddSingleton<IBulkLoader, SqlBulkLoader>();
        services.AddSingleton<IIndexManager, SqlServerIndexManager>();
        services.AddSingleton<IDesiredIndexManager, SqlServerDesiredIndexManager>();
        services.AddSingleton<IIncrementalProbe, SqlServerIncrementalProbe>();
        services.AddSingleton<SqlFlow.Core.Acquire.IAcquireWatermarkProbe, SqlServerAcquireWatermarkProbe>();
        services.AddSingleton<IStateStore, NullStateStore>();
        services.AddSingleton<IFlowEventSink, ConsoleFlowEventSink>();

        // Secret resolution + Azure auth (Service Principal / Managed Identity / Azure CLI / Default).
        services.AddSingleton<IAzureCredentialFactory, AzureCredentialFactory>();
        services.AddSingleton<ISecretProvider, EnvSecretProvider>();
        services.AddSingleton<ISecretProvider, AzureKeyVaultSecretProvider>();
        services.AddSingleton<ISecretResolver, SecretResolver>();

        // The same Azure auth intent that drives Key Vault and invoke also authenticates DuckDB cloud reads
        // (ADLS/Blob), so the DuckDB reader (registered above) auto-creates its storage secret with no per-flow
        // credential. One intent, every Azure path.
        services.AddSingleton<ICloudCredentialProvider, AzureStorageCredentialProvider>();

        // Connection registry + source discovery (catalog). Lightweight default: NullDataSourceStore, so an
        // @alias requires full mode; inline and ${...} references resolve directly. The provider registry
        // (SQL Server built-in + MySQL/PostgreSQL from SqlFlow.Providers) feeds the canonicalizers, the
        // kind-dispatching connection factory, and the catalog reader factory.
        var sourceProviders = SqlServerSourceProvider.CreateRegistry().Concat(SqlFlowSourceProviders.CreateRegistry());
        services.AddSingleton(sourceProviders);
        services.AddSingleton<IDataSourceStore, NullDataSourceStore>();
        foreach (var canonicalizer in sourceProviders.Canonicalizers)
        {
            services.AddSingleton<IConnectionStringCanonicalizer>(canonicalizer);
        }

        services.AddSingleton<IConnectionResolver, ConnectionResolver>();
        services.AddSingleton<IConnectionFactory>(new CompositeConnectionFactory(sourceProviders.ConnectionFactories));
        services.AddSingleton<ICatalogReaderFactory>(new CompositeCatalogReaderFactory(sourceProviders.CatalogReaders));
        services.AddSingleton<CatalogService>();
        // Ad-hoc datasource compute (list tables, introspect, test connection, unique-key detection): the
        // executor behind queued compute tasks, resolving connections through the exact same registry as flows.
        services.AddSingleton<ComputeTaskExecutor>();
        services.AddSingleton<IServerLocaleProvider, SqlServerLocaleProvider>();
        services.AddSingleton<IColumnProfiler, SqlServerColumnProfiler>();
        services.AddSingleton<TypeInferencer>();
        services.AddSingleton<IInferenceValidator, SqlServerInferenceValidator>();
        services.AddSingleton<IInferenceService, InferenceService>();
        services.AddSingleton<YamlFlowLoader>();
        services.AddSingleton<YamlIngestionFlowLoader>();
        services.AddSingleton<YamlExportFlowLoader>();
        services.AddSingleton<YamlStoredProcedureFlowLoader>();
        services.AddSingleton<YamlInvokeFlowLoader>();
        services.AddSingleton<YamlHealthCheckFlowLoader>();
        services.AddSingleton<YamlSourceControlFlowLoader>();
        services.AddSingleton<YamlBatchFlowLoader>();
        services.AddSingleton<YamlAcquireFlowLoader>();
        services.AddSingleton<YamlCopyFlowLoader>();
        services.AddSingleton<YamlSftpFlowLoader>();
        services.AddSingleton<YamlCalendarFlowLoader>();
        services.AddSingleton<YamlTranslateFlowLoader>();
        services.AddSingleton<YamlDocumentLoader>();
        services.AddSingleton<InferSpecLoader>();
        services.AddSingleton<FlowRunner>();

        // Generic acquisition engine (flowType: acq): raw landing stores, the HTTP/SFTP/S3/Azure Table
        // transports, the auth resolver, and the acquisition runner. Composed here so every host that runs flows
        // gets it, exactly like the source readers above.
        SqlFlow.Acquire.AcquireServices.AddSqlFlowAcquire(services);

        // File-copy engine (flowType: cpy): the local / Azure storage endpoints, the engine, and the copy runner.
        SqlFlow.Copy.CopyServices.AddSqlFlowCopy(services);

        // SFTP transfer engine (flowType: sftp): the engine and the sftp runner.
        SqlFlow.Sftp.SftpServices.AddSqlFlowSftp(services);

        services.AddSingleton(sp => new DocumentExecutor(sp));

        return services;
    }
}
