using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Extraction;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// The one mapping from extracted script dependencies to lineage facts, shared by every consumer of the
/// extractor (document hooks, run traces, harvested modules) so the relation semantics are identical
/// everywhere. The relations themselves come from <see cref="ScriptDependencies.TypedRelations"/>, the
/// exact DeltaForge lifecycle algorithm; this layer only applies graph hygiene: temp names and identities
/// below the caller's part threshold stay out, the engine's transient staging (created and THEN
/// dropped by the same script) never reaches the graph (a drop-then-create rebuild, by contrast, keeps its
/// full relations), and reads of the system catalogs (a hook consulting sys.indexes, a probe of
/// INFORMATION_SCHEMA) stay out too: they are introspection, not data movement, and would render as
/// producerless input objects in every graph view.
/// </summary>
public static class ScriptFactBuilder
{
    /// <summary>Whether the identity is a system-catalog object (the <c>sys</c> or
    /// <c>INFORMATION_SCHEMA</c> schema): metadata introspection, never data lineage.</summary>
    private static bool IsSystemCatalog(TableName table)
        => string.Equals(table.Schema, "sys", StringComparison.OrdinalIgnoreCase)
            || string.Equals(table.Schema, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<LineageFact> Facts(
        ScriptDependencies deps,
        string? flow,
        string? viaModuleKey,
        string serverRef,
        LineageTier tier,
        int minimumParts,
        Guid? runId = null,
        DateTime? observedAtUtc = null,
        string? step = null)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRef);

        foreach (var (table, relation) in deps.TypedRelations())
        {
            if (table.IsTemp || table.PartCount < minimumParts || deps.CreatedThenDropped.Contains(table.Key)
                || IsSystemCatalog(table))
            {
                continue;
            }

            yield return new LineageFact
            {
                Flow = flow,
                ViaModuleKey = viaModuleKey,
                Relation = relation,
                ServerRef = serverRef,
                Database = table.Database,
                Schema = table.Schema,
                Name = table.Name,
                Tier = tier,
                RunId = runId,
                ObservedAtUtc = observedAtUtc,
                Step = step,
            };
        }
    }

    /// <summary>
    /// Appends one script's data-model observations (equality joins, key hints, explicit constraints) to the
    /// collection, under the same hygiene the facts use: temp names and transient staging stay out. A side
    /// referencing a table the script itself created-then-dropped is engine plumbing, not model knowledge.
    /// <paramref name="scriptId"/> names the script (a module key, a flow hook label, a trace step): it is
    /// the unit the builder counts occurrences by, so one script repeating a join counts once.
    /// </summary>
    public static void AppendModelObservations(
        CollectionResult result, ScriptDependencies deps, string serverRef, LineageTier tier, string scriptId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);

        bool Excluded(TableName table)
            => table.IsTemp || deps.CreatedThenDropped.Contains(table.Key) || IsSystemCatalog(table);

        ModelObjectRef Ref(TableName table) => new()
        {
            ServerRef = serverRef,
            Database = table.Database,
            Schema = table.Schema,
            Name = table.Name,
        };

        foreach (var join in deps.Joins)
        {
            if (Excluded(join.Left) || Excluded(join.Right))
            {
                continue;
            }

            result.Joins.Add(new CollectedJoin
            {
                Left = Ref(join.Left),
                LeftColumns = join.LeftColumns,
                Right = Ref(join.Right),
                RightColumns = join.RightColumns,
                Operators = join.Operators,
                JoinType = join.JoinType,
                Tier = tier,
                ScriptId = scriptId,
            });
        }

        foreach (var key in deps.Keys)
        {
            if (Excluded(key.Table))
            {
                continue;
            }

            result.KeyHints.Add(new CollectedKeyHint
            {
                Table = Ref(key.Table),
                Columns = key.Columns,
                Origin = key.Origin,
                Tier = tier,
            });
        }

        foreach (var constraint in deps.ForeignKeys)
        {
            if (Excluded(constraint.From) || Excluded(constraint.To))
            {
                continue;
            }

            result.ModelConstraints.Add(new CollectedModelConstraint
            {
                Name = constraint.Name,
                From = Ref(constraint.From),
                FromColumns = constraint.FromColumns,
                To = Ref(constraint.To),
                ToColumns = constraint.ToColumns,
                Tier = tier,
            });
        }
    }

    /// <summary>The created-object artifacts of one script (the generating DDL and, for a table, its columns),
    /// under the same identity threshold the facts use: temp names and transient staging (created then
    /// dropped) stay out, so only durable objects carry a script and column dictionary into the catalog.</summary>
    public static IEnumerable<CollectedObjectArtifact> ObjectArtifacts(
        ScriptDependencies deps, string serverRef, LineageTier tier, int minimumParts)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRef);

        foreach (var created in deps.CreatedObjects.Values)
        {
            var table = created.Table;
            if (table.IsTemp || table.PartCount < minimumParts || deps.CreatedThenDropped.Contains(table.Key))
            {
                continue;
            }

            yield return new CollectedObjectArtifact
            {
                ServerRef = serverRef,
                Database = table.Database,
                Schema = table.Schema,
                Name = table.Name,
                Kind = created.Kind,
                Script = created.Ddl,
                Columns = created.Columns,
                Tier = tier,
            };
        }
    }
}
