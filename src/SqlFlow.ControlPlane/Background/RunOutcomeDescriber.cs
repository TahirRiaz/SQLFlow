using System.Globalization;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Says what one run did, in a few words, for a reader outside SQLFlow: a trigger request's trace and the summary a GitHub
/// job shows. A module registers one for the runs of its own flow kinds (what a registered kind did is in its
/// <see cref="CatalogRun.ResultJson"/>, which only the module can read); SQLFlow describes every run by its row counts when
/// no describer has anything to say.
/// </summary>
public interface IRunOutcomeDescriber
{
    /// <summary>What <paramref name="run"/> did, or null when this describer has nothing to say about it (a run of a kind it
    /// does not own, or a result it cannot read).</summary>
    string? Describe(CatalogRun run);
}

/// <summary>The one rule a run is described by: the first registered describer with something to say, otherwise its row counts.</summary>
public static class RunOutcomeDescriptions
{
    /// <summary>What <paramref name="run"/> did, or null when nothing is known (no describer spoke and no row was counted).
    /// A describer that throws is logged and passed over: a description is never worth failing a request's trace for.</summary>
    public static string? Describe(CatalogRun run, IEnumerable<IRunOutcomeDescriber> describers, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(describers);
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var describer in describers)
        {
            string? text;
            try
            {
                text = describer.Describe(run);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(
                    ex, "{Describer} could not describe run {RunId} of flow '{Flow}' ({Kind}); its row counts describe it instead.",
                    describer.GetType().Name, run.RunId, run.FlowName, run.FlowKind);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return Rows(run);
    }

    /// <summary>A run's row counts: <c>1,234 rows loaded (1,000 inserted, 234 updated)</c>, naming only the counts it has.</summary>
    public static string? Rows(CatalogRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var changes = new List<string>();
        if (run.RowsInserted is > 0)
        {
            changes.Add(N(run.RowsInserted.Value) + " inserted");
        }

        if (run.RowsUpdated is > 0)
        {
            changes.Add(N(run.RowsUpdated.Value) + " updated");
        }

        if (run.RowsDeleted is > 0)
        {
            changes.Add(N(run.RowsDeleted.Value) + " deleted");
        }

        if (run.RowsLoaded is { } loaded)
        {
            return N(loaded) + (loaded == 1 ? " row loaded" : " rows loaded") + (changes.Count == 0 ? string.Empty : $" ({string.Join(", ", changes)})");
        }

        return changes.Count == 0 ? null : string.Join(", ", changes);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
