using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Applies the indexes a flow declares it wants on its target table (the original SQLFlow
/// <c>trgDesiredIndex</c> design). The engine invokes this only when the target table was created
/// during the run: on a fresh table there are no existing indexes, so the declared set is simply
/// built. On an existing table the load-time disable/rebuild path (<see cref="IIndexManager"/>)
/// preserves whatever indexes are already there, so the two never duplicate work.
/// </summary>
public interface IDesiredIndexManager
{
    /// <summary>
    /// Parses the desired-index T-SQL script (one or more <c>CREATE INDEX</c> statements), reporting
    /// syntax errors with location, then creates each declared index on the target and returns the
    /// per-index outcome.
    /// </summary>
    Task<IReadOnlyList<IndexAction>> ApplyAsync(string connectionString, string desiredIndexScript, CancellationToken ct = default);
}
