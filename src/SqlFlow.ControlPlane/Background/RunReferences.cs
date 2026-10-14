using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// A source of the values the control plane supplies with runs and node tasks for their <c>${env:NAME}</c> references
/// (<see cref="SuppliedReferences"/>): the connections and settings an estate's flows read, held in one place instead of
/// on every node. A host module registers one to give its estate's configuration to every run, of every kind.
/// </summary>
public interface IRunReferenceSource
{
    /// <summary>
    /// The values every run of the repository <paramref name="repoId"/> resolves with; for <c>null</c>, the values for
    /// work that belongs to no repository (an ad-hoc datasource task). Empty when the source holds nothing for it. A read
    /// that fails throws: a run queued without the values it should carry would resolve against whatever the node's
    /// environment happens to hold.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ReferencesAsync(Guid? repoId, CancellationToken ct);
}

/// <summary>
/// The values the control plane supplies with a run or a node task: every registered <see cref="IRunReferenceSource"/>
/// asked in registration order, a later source's value winning over an earlier one's for the same name (and the overlap
/// logged), the whole validated as a run's references are (<see cref="SuppliedReferences.Validate"/>). With no source
/// registered, nothing is supplied and every reference resolves on the node, as it always did.
/// </summary>
public sealed partial class RunReferences
{
    private static readonly IReadOnlyDictionary<string, string> None = ReadOnlyDictionary<string, string>.Empty;

    private readonly IReadOnlyList<IRunReferenceSource> _sources;
    private readonly ILogger<RunReferences> _log;

    public RunReferences(IEnumerable<IRunReferenceSource> sources, ILogger<RunReferences> log)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(log);
        _sources = sources.ToList();
        _log = log;
    }

    /// <summary>Whether any source is registered; with none, nothing is ever supplied.</summary>
    public bool HasSources => _sources.Count > 0;

    /// <summary>
    /// The values a run of <paramref name="repoId"/> (null: work of no repository) is supplied with.
    /// </summary>
    /// <exception cref="SqlFlowException">A source supplies values no run may carry (too many, a malformed name or value,
    /// a written-out credential), named so the configuration can be put right.</exception>
    public async Task<IReadOnlyDictionary<string, string>> ForAsync(Guid? repoId, CancellationToken ct)
    {
        if (_sources.Count == 0)
        {
            return None;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in _sources)
        {
            var supplied = await source.ReferencesAsync(repoId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The run reference source '{source.GetType().FullName}' answered null.");
            foreach (var (name, value) in supplied)
            {
                if (merged.ContainsKey(name))
                {
                    LogOverlap(_log, name, source.GetType().Name);
                }

                merged[name] = value;
            }
        }

        SuppliedReferences.Validate(merged, "the configuration the control plane supplies with runs");
        return merged;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Reference {Name} is supplied by more than one source; the value of {Source}, registered later, is the one runs carry.")]
    private static partial void LogOverlap(ILogger logger, string name, string source);
}
