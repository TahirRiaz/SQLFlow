namespace SqlFlow.Core.Acquire;

/// <summary>
/// One row of a watermark probe: a resume point, optionally scoped to the entity it belongs to.
/// </summary>
/// <param name="Key">
/// The entity the watermark applies to (the value of the flow's fan-out variable, e.g. a quest or vehicle id), or
/// null when the query returned a single column and the watermark is global to the flow.
/// </param>
/// <param name="Value">The resume point as text, already formatted the way the query chose to return it.</param>
public readonly record struct AcquireWatermarkRow(string? Key, string? Value);

/// <summary>
/// Reads an acquisition's resume point out of a database, so a feed can resume from the state of what was actually
/// LOADED rather than from a run record or the shape of the raw zone.
/// </summary>
/// <remarks>
/// This exists because the other two watermark sources each have a blind spot. A run-record value is node-local and
/// does not survive a redeploy or an edit to the flow file, and a lake-derived one only works when the landed file
/// NAMES encode the resume value, which they do not when a feed is partitioned by entity and page rather than by
/// time. The loaded table has neither problem: it is the thing the pipeline is ultimately trying to keep current.
///
/// The engine never composes the statement. The flow supplies the connection and the query verbatim, so nothing
/// about a particular source's tables or columns is known here; the probe runs it and hands the rows back as text.
///
/// A one-column result is a single watermark for the whole flow. A two-column result is one watermark PER ENTITY,
/// keyed by the first column, which is what a fan-out feed needs: each entity resumes from its own high-water mark
/// instead of every entity being dragged back to the oldest one.
/// </remarks>
public interface IAcquireWatermarkProbe
{
    /// <summary>
    /// Runs the flow's query and returns its rows. An empty result (or a NULL value) is not an error: it is a first
    /// run against an empty target, and the flow stays on its declared seed.
    /// </summary>
    /// <param name="connection">A connection reference, resolved the same way any flow connection is.</param>
    /// <param name="query">The scalar or two-column query, taken verbatim from the flow.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<IReadOnlyList<AcquireWatermarkRow>> ReadAsync(string connection, string query, CancellationToken ct = default);
}
