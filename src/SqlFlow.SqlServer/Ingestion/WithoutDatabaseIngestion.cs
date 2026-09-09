using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer.Invoke;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>
/// The without-database composition root for relational ingestion: builds a fully wired
/// <see cref="IngestionFlowRunner"/> from a flow document's own connection, assertion, and invoke declarations,
/// with no control database anywhere. The same resolver, assertion runner, surrogate-key executor, and invoke
/// runner as full mode run behind in-memory stores; the run log stays the no-op default. The SQL Server
/// provider set is built in; MySQL and PostgreSQL sources plug in by passing the SqlFlow.Providers registry,
/// and ADF/Automation executors by passing the SqlFlow.Azure executor list. With no declared invokes the hooks
/// stay unavailable (a set alias is a clear error, the documented without-database default). Both the CLI and
/// the tests construct through here, so YAML execution has exactly one code path.
/// </summary>
public static class WithoutDatabaseIngestion
{
    public static IngestionFlowRunner BuildRunner(
        IEnumerable<DataSource> connections,
        IEnumerable<AssertionDefinition> assertionDefinitions,
        ISecretResolver? secrets = null,
        SourceProviderRegistry? providers = null,
        IEnumerable<InvokeDefinition>? invokes = null,
        IReadOnlyList<IInvokeExecutor>? invokeExecutors = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(assertionDefinitions);

        var registry = SqlServerSourceProvider.CreateRegistry();
        if (providers is not null)
        {
            registry = registry.Concat(providers);
        }

        var resolver = WithoutDatabaseResolver.Build(connections, secrets, registry);
        var factory = new CompositeConnectionFactory(registry.ConnectionFactories);
        var catalogs = new CompositeCatalogReaderFactory(registry.CatalogReaders);

        // The pre-ingestion transform's type inference profiles the (SQL Server) target the runner just loaded;
        // the runner hands it an already-resolved connection string, so the secret resolver is a pass-through
        // for it (kept consistent with the rest of the without-database composition).
        static Core.Abstractions.IInferenceService BuildInferenceService(ISecretResolver? secrets)
            => new Core.Engine.InferenceService(
                new SqlServerSchemaProvider(),
                new SqlServerColumnProfiler(),
                new Core.Engine.TypeInferencer(),
                new SqlServerInferenceValidator(),
                new SqlServerLocaleProvider(),
                secrets ?? new SecretResolver([new EnvSecretProvider()]));

        // The surrogate-key executor introspects the TARGET (always SQL Server), so it keeps a direct reader.
        var targetCatalog = new Catalog.SqlServerCatalogReader();

        return new IngestionFlowRunner(
            resolver,
            factory,
            catalogs,
            assertions: new AssertionRunner(new InMemoryAssertionDefinitionStore(assertionDefinitions)),
            surrogateKeys: new SurrogateKeyExecutor(resolver, targetCatalog),
            invoke: WithoutDatabaseInvokeRunner.Build(invokes, invokeExecutors),
            sourceDialects: registry.Dialects,
            sourceTypeMappers: registry.TypeMappers,
            inference: BuildInferenceService(secrets));
    }
}
