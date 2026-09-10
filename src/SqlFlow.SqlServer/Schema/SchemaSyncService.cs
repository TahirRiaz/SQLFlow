using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>The result of applying schema evolution: the plan plus the exact DDL that ran (empty when the
/// live schema already matched).</summary>
public sealed record EvolutionOutcome
{
    public required EvolutionPlan Plan { get; init; }

    public required IReadOnlyList<DdlStatement> AppliedStatements { get; init; }
}

/// <summary>The result of applying a temporal plan: what the planner decided plus the DDL that ran.</summary>
public sealed record TemporalOutcome
{
    public required TemporalPlan Plan { get; init; }

    public required IReadOnlyList<DdlStatement> AppliedStatements { get; init; }
}

/// <summary>
/// Evolves a live SQL Server target to a desired schema end to end: introspect the current shape through the
/// shared <see cref="ICatalogReader"/>, plan the diff (monotonic widening, footprint classification, the
/// key/hash critical-mismatch gate), generate idempotent DDL, and apply it under the non-blocking lock
/// model. <see cref="PlanAsync"/> is the dry-run that stops before applying; <see cref="EvolveAsync"/> applies.
/// </summary>
public sealed class SchemaSyncService
{
    private readonly ICatalogReader _catalog;

    public SchemaSyncService(ICatalogReader catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>Introspects the live target over the supplied connection and returns the evolution plan,
    /// applying nothing. The target's database is taken from the connection's current context.</summary>
    public async Task<EvolutionPlan> PlanAsync(
        DbConnection connection,
        RelationalObject target,
        IReadOnlyList<SqlColumn> desired,
        IReadOnlySet<string> keyColumns,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(target);

        var live = await _catalog.IntrospectObjectAsync(connection, ToName(target), ct).ConfigureAwait(false);
        var actual = live is null ? null : CatalogSchemaAdapter.ToEvolvableColumns(live);
        return SchemaEvolutionPlanner.Plan(desired, actual, keyColumns);
    }

    /// <summary>Introspects, plans, generates, and applies in one operation. Returns the plan that was
    /// applied plus the exact DDL statements that ran (for reporting and the run's SQL trace). Throws if the
    /// plan is blocked or requires an unauthorized table rewrite, before any DDL runs.</summary>
    public async Task<EvolutionOutcome> EvolveAsync(
        string connectionString,
        RelationalObject target,
        IReadOnlyList<SqlColumn> desired,
        IReadOnlySet<string> keyColumns,
        bool allowTableRewrite,
        SchemaApplyOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // One connection serves both the introspection and the DDL apply, so an evolve never pays a second
        // open (and its sp_reset_connection round trip) on the hot ingestion path.
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var plan = await PlanAsync(connection, target, desired, keyColumns, ct).ConfigureAwait(false);

        // Generate validates the plan (throws on a blocked plan or an unauthorized rewrite). A plan with no
        // changes and no block produces an empty batch, which ApplyDdlAsync treats as a no-op.
        var batch = EvolutionDdlGenerator.Generate(target, plan, allowTableRewrite);
        await SqlServerSchemaProvider.ApplyDdlAsync(connection, batch, options, ct).ConfigureAwait(false);
        return new EvolutionOutcome { Plan = plan, AppliedStatements = batch.Statements };
    }

    /// <summary>
    /// Brings the target to its declared system-versioning state: introspect, plan, apply. Introspection and
    /// the DDL share one connection, and the apply goes through the same object-scoped lock protocol as
    /// schema evolution, so a temporal transition can never interleave with another run's DDL on the same
    /// table. Returns what was decided and what ran; an unsatisfiable state throws before any DDL.
    /// </summary>
    public async Task<TemporalOutcome> ApplyTemporalAsync(
        string connectionString,
        RelationalObject target,
        TemporalPolicy policy,
        IReadOnlyList<SqlColumn> desired,
        SchemaApplyOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var live = await _catalog.IntrospectObjectAsync(connection, ToName(target), ct).ConfigureAwait(false)
            ?? throw new SqlFlowException(
                $"versioning.temporal is enabled but the target '{target.QualifiedName}' does not exist. Enable 'schema.sync' " +
                "so the engine creates it, or create the table before running the flow.");

        var plan = TemporalTablePlanner.Plan(target, policy, live, desired);
        // Ordered: SYSTEM_VERSIONING cannot be turned on before the SYSTEM_TIME period exists, and the
        // period cannot be added before its schema does.
        var batch = new DdlBatch
        {
            Schema = target.Schema,
            Table = target.Name,
            Statements = plan.Statements,
            PreserveOrder = true,
        };
        await SqlServerSchemaProvider.ApplyDdlAsync(connection, batch, options, ct).ConfigureAwait(false);
        return new TemporalOutcome { Plan = plan, AppliedStatements = plan.Statements };
    }

    // The connection is already on the target database, so the introspection uses the current-database
    // OBJECT_ID path (Database left null) rather than switching context.
    private static ThreePartName ToName(RelationalObject target)
        => new() { Database = null, Schema = target.Schema, Name = target.Name };
}
