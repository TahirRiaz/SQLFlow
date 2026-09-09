using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer;

namespace SqlFlow.SourceControl;

/// <summary>
/// The without-database composition root for source-control flows: builds a fully wired
/// <see cref="SourceControlService"/> from a document's own connection declarations, with no control database
/// anywhere. The same resolver composition as every other flow runs behind an in-memory store; the database to
/// script is always SQL Server (the loader guarantees it), so the built-in SQL Server provider set is all the
/// resolver needs. The SMO scripter and the LibGit2Sharp workspace are the production engines; a test can swap
/// the git workspace through the optional <c>gitFactory</c>. The CLI and the tests construct through here, so
/// execution has exactly one code path.
/// </summary>
public static class WithoutDatabaseSourceControl
{
    public static SourceControlService BuildService(
        IEnumerable<DataSource> connections,
        ISecretResolver? secrets = null,
        Func<GitWorkspaceConfig, IGitWorkspace>? gitFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connections);

        var registry = SqlServerSourceProvider.CreateRegistry();
        var secretResolver = secrets ?? new SecretResolver([new EnvSecretProvider()]);
        var resolver = WithoutDatabaseResolver.Build(connections, secretResolver, registry);

        return new SourceControlService(resolver, secretResolver, new SmoDatabaseScripter(), gitFactory);
    }
}
