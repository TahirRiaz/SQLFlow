using System.Data.Common;

namespace SqlFlow.Core.Connections;

/// <summary>
/// The single way to obtain an open database connection. Opens a fresh, pooled connection per call and
/// never caches or returns a shared connection object, so the engine stays stateless and reentrant; the
/// physical reuse comes from the ADO.NET pool, keyed by <see cref="ResolvedConnection.CanonicalString"/>.
/// Returns a <see cref="DbConnection"/> so the contract is provider-neutral (a SQL Server target yields a
/// SqlConnection, a MySQL source a MySqlConnection); SQL Server providers obtain the concrete connection
/// through a single guarded downcast. Implemented in the provider assemblies, not in Core.
/// </summary>
public interface IConnectionFactory
{
    Task<DbConnection> OpenAsync(ResolvedConnection connection, CancellationToken ct = default);
}
