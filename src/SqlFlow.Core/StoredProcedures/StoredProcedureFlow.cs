using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.StoredProcedures;

/// <summary>
/// A stored-procedure flow (legacy flw.StoredProcedure, FlowType 'sp'): a DAG node that executes one existing
/// stored procedure on a resolved target server. It carries no secret and does no schema work; it is the V3
/// model of one flw.StoredProcedure row. The procedure is addressed by a three-part name and the server by a
/// connection-registry alias, exactly like an ingestion flow's endpoints. Immutable.
/// </summary>
public sealed record StoredProcedureFlow
{
    /// <summary>The legacy numeric identity (flw.StoredProcedure.FlowID).</summary>
    public int FlowId { get; init; }

    public string? Batch { get; init; }                  // Batch

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>, production by default): a development
    /// flow runs exactly like a production one but never generates notification events.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;

    public required string SysAlias { get; init; }       // SysAlias (mandatory in legacy)

    /// <summary>The flw.SysDataSource alias the procedure runs on (legacy trgServer).</summary>
    public required string Server { get; init; }

    /// <summary>The three-part procedure to execute (legacy trgDBSchSP).</summary>
    public required RelationalObject Procedure { get; init; }

    /// <summary>The input parameters bound to the procedure (legacy flw.Parameter), in declaration order.
    /// Empty for a procedure that takes none, which is the common case.</summary>
    public IReadOnlyList<StoredProcedureParameter> Parameters { get; init; } = [];

    public bool OnErrorResume { get; init; } = true;     // OnErrorResume (default true)

    public string? PostInvokeAlias { get; init; }        // PostInvokeAlias

    public string? Description { get; init; }            // Description

    public string FlowType { get; init; } = "sp";        // FlowType

    public bool DeactivateFromBatch { get; init; }       // DeactivateFromBatch

    public int? FromObjectMK { get; init; }              // FromObjectMK (lineage)

    public int? ToObjectMK { get; init; }                // ToObjectMK (lineage)

    public string? CreatedBy { get; init; }              // CreatedBy

    public DateTime? CreatedDate { get; init; }          // CreatedDate

    /// <summary>The connection reference understood by the connection resolver.</summary>
    public string ConnectionReference => "@" + Server;
}
