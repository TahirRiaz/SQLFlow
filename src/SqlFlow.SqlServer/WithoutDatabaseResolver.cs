using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SqlServer;

/// <summary>
/// The shared resolver composition of the without-database mode: a flow document's own connection declarations
/// behind an in-memory store, environment-variable secrets unless the caller supplies a resolver, and the
/// provider registry's canonicalizers. Every without-database builder (ingestion, export, stored procedure,
/// health check) goes through here, so connection resolution has exactly one composition. Public because the
/// health-check builder lives in its own assembly (SqlFlow.HealthCheck isolates the ML.NET dependency).
/// </summary>
public static class WithoutDatabaseResolver
{
    public static ConnectionResolver Build(
        IEnumerable<DataSource> connections,
        ISecretResolver? secrets,
        SourceProviderRegistry registry)
        => new(
            new InMemoryDataSourceStore(connections),
            secrets ?? new SecretResolver([new EnvSecretProvider()]),
            registry.Canonicalizers);
}
