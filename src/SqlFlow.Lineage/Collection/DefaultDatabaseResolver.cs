using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// Completes the server inventory with each SQL Server connection's default database WITHOUT opening a
/// database connection: the reference is resolved (secret expansion plus canonicalization) and the Initial
/// Catalog read off the canonical string. Only servers that actually carry a database-less fact are
/// resolved, so an estate of fully three-part documents never pays for, or warns about, references its
/// identities do not need. Connected ground truth (DB_NAME(), recorded by <see cref="CatalogCollector"/>)
/// always wins; a reference that cannot be resolved, or that declares no catalog, degrades to a warning and
/// the builder's single-candidate unification remains the fallback. Only the database NAME leaves this
/// class: the canonical string can carry credentials and never does.
/// </summary>
public static class DefaultDatabaseResolver
{
    public static async Task ResolveAsync(CollectionResult collected, IConnectionResolver resolver, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collected);
        ArgumentNullException.ThrowIfNull(resolver);

        var needsDatabase = collected.Facts
            .Where(f => f.Database is null)
            .Select(f => f.ServerRef)
            .ToHashSet(StringComparer.Ordinal);

        var pending = collected.Servers
            .Where(s => s.Value.Kind is DataSourceKind.MSSQL or DataSourceKind.AZDB)
            .Where(s => needsDatabase.Contains(s.Key) && !collected.ServerDefaultDatabases.ContainsKey(s.Key))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var (serverRef, value) in pending)
        {
            ct.ThrowIfCancellationRequested();
            string database;
            try
            {
                var resolved = await resolver.ResolveAsync(value.RawReference, ConnectionRole.Source, value.Kind, ct).ConfigureAwait(false);
                database = new SqlConnectionStringBuilder(resolved.CanonicalString).InitialCatalog;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                collected.Warnings.Add(
                    $"server '{serverRef}': default database unknown ({SecretHygiene.RedactedMessage(ex)}); its two-part object identities keep no database.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(database))
            {
                collected.Warnings.Add(
                    $"server '{serverRef}': the connection declares no Initial Catalog, so its two-part object identities keep no database; set the database on the connection or use three-part names.");
                continue;
            }

            collected.ServerDefaultDatabases[serverRef] = database;
        }
    }
}
