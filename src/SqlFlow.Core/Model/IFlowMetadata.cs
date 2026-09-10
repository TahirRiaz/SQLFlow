namespace SqlFlow.Core.Model;

/// <summary>
/// Common contract across all flow-type metadata records. SQLFlow partitions metadata by
/// <see cref="FlowType"/> - csv, xml, json, parquet, xls, ado, … - where each type is a rich record
/// whose properties map directly onto that source's parser/reader library. <see cref="FlowId"/> is
/// the universal key that ties a flow together across logging, lineage, and execution.
/// </summary>
public interface IFlowMetadata
{
    /// <summary>
    /// Stable flow identity (pinned GUID or one computed deterministically from the flow name),
    /// identical on every execution. Distinct from the per-run RunId.
    /// </summary>
    Guid FlowId { get; }

    string FlowType { get; }
}
