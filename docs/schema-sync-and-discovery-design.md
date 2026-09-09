# SQLFlowV3: Schema Sync-Up and Source Discovery - Unified Design

> Pre-implementation design document; the shipped CLI surface diverged from what this document
> specifies. It describes discovery as `sqlflow discover databases|schemas|tables|search|columns|scaffold`
> with a `--no-cache` flag; the shipped commands are `sqlflow catalog <databases|schemas|tables|search|columns|scaffold|scaffold-all>`,
> `sqlflow discover` is instead the JSON/XML source introspection verb, and there is no `--no-cache`
> flag anywhere. For the current, code-verified behavior see
> [docs/reference/cli/catalog.md](reference/cli/catalog.md) and
> [docs/reference/cli/discover.md](reference/cli/discover.md).

Lead-architect synthesis of two deliverables over one shared introspection foundation, building on the implemented connection registry (`SqlFlow.Core.Connections`), ingestion model (`SqlFlow.Core.Ingestion`), and schema-evolution slice 1 (`SqlFlow.SqlServer/Schema`: `SqlDataType`, `SqlTypeResolution`, `HashKey`). Every critic finding is folded into the design, not listed. SQL Server is the target by design; two modes one engine; secrets never leave the canonical string; one code path; production-grade, no stubs.

---

## 1. Overview and the shared introspection foundation

Two features that look unrelated share one hard problem: reading the live shape of a relational object over the firewall-permitted connection. Deliverable B (discovery) reads it to show an operator what exists; deliverable A (sync-up) reads it to evolve a target safely. Legacy built these as separate SMO and INFORMATION_SCHEMA paths, each bolted to a different feature. V3 collapses them into one abstraction.

The non-negotiable principle for both features: **all schema reading goes through one provider-neutral introspection contract, executed over the same `IConnectionResolver` -> `IConnectionFactory` path the engine already uses to ingest.** No second credential path, no extra firewall opening, no SMO.

### 1.1 The one schema-reader: `ICatalogReader`

There are two natural granularities of "read schema": browse the server (databases, schemas, lists of objects, search) and introspect one object in full (columns, types, identity, computed, collation, indexes, constraints). Rather than two interfaces that drift, there is **one** `ICatalogReader` with both granularities, and the sync engine consumes only the object-introspection method. Discovery consumes all of it.

```csharp
// src/SqlFlow.Core/Catalog/ICatalogReader.cs
using System.Data.Common;
using SqlFlow.Core.Connections;

namespace SqlFlow.Core.Catalog;

/// <summary>
/// The single schema-reading abstraction shared by source discovery and the schema-sync engine. Every method
/// runs over a caller-supplied open <see cref="DbConnection"/> obtained from the connection registry, so it
/// inherits the firewall-permitted egress, the resolved credentials, and the secretless gate. Provider-neutral:
/// a SQL Server reader and a MySQL reader implement it; the sync engine only ever calls
/// <see cref="IntrospectObjectAsync"/>, while discovery calls the enumeration methods too.
/// </summary>
public interface ICatalogReader
{
    DataSourceKind Kind { get; }

    Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(DbConnection conn, CatalogQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<SchemaInfo>>   ListSchemasAsync  (DbConnection conn, string database, CatalogQuery query, CancellationToken ct = default);
    Task<CatalogPage<ObjectInfo>>     ListObjectsAsync  (DbConnection conn, ObjectScope scope, CatalogQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<ObjectMatch>>  SearchObjectsAsync(DbConnection conn, string? database, CatalogQuery query, CancellationToken ct = default);

    /// <summary>Full structured introspection of one table or view: columns with full metadata, plus the
    /// index/constraint dependency facts the sync planner needs. Null when the object does not exist. This is
    /// the ONE method the sync engine shares with discovery.</summary>
    Task<CatalogObject?> IntrospectObjectAsync(DbConnection conn, ThreePartName name, CancellationToken ct = default);
}
```

`ThreePartName` is the catalog-layer twin of `RelationalObject` (database/schema/object, bracket-escaped). It lives in `SqlFlow.Core.Catalog` so the catalog assembly does not depend on the ingestion model; `RelationalObject.ToThreePartName()` adapts one to the other. The sync engine passes the target's `RelationalObject` through that adapter, so there is exactly one introspection call site shape.

### 1.2 Why one connection, opened once

Both features acquire their `DbConnection` identically:

```csharp
var resolved = await _resolver.ResolveAsync(reference, role, ct);   // "@alias" or inline
await using var conn = await _factory.OpenAsync(resolved, ct);      // pooled, keyed on CanonicalString
```

The canonical string is a secret; only `RedactedString` is ever logged. The catalog reader never sees a connection string, only an already-open `DbConnection`, so it physically cannot leak credentials in a trace or exception. This is the single code path the engine uses to ingest, reused verbatim.

### 1.3 The full-introspection model (`CatalogObject`), used by both features

```csharp
// src/SqlFlow.Core/Catalog/CatalogModel.cs  (provider-neutral, no SqlClient)
namespace SqlFlow.Core.Catalog;

public enum ObjectType { Table, View }

public sealed record CatalogObject
{
    public required ThreePartName Name { get; init; }
    public required ObjectType Type { get; init; }
    public required IReadOnlyList<CatalogColumn> Columns { get; init; }
    public IReadOnlyList<CatalogIndex> Indexes { get; init; } = [];
    public IReadOnlyList<CatalogConstraint> Constraints { get; init; } = [];
    public bool IsTemporal { get; init; }
}

public sealed record CatalogColumn
{
    public required string Name { get; init; }
    public required int Ordinal { get; init; }
    /// <summary>The rendered native type string, e.g. <c>nvarchar(50)</c>, <c>decimal(18, 2)</c>, <c>datetime2(7)</c>.
    /// SQL Server renders this so it round-trips through <see cref="SqlFlow.SqlServer.Schema.SqlDataType"/>; MySQL
    /// renders its own native type for display and is mapped to a SQL Server type only at scaffold time.</summary>
    public required string NativeType { get; init; }
    public bool IsNullable { get; init; } = true;
    public string? Collation { get; init; }
    public bool IsIdentity { get; init; }
    public long? IdentitySeed { get; init; }
    public long? IdentityIncrement { get; init; }
    public string? ComputedExpression { get; init; }   // without the AS keyword; null if not computed
    public bool ComputedPersisted { get; init; }
    public string? DefaultExpression { get; init; }
    public bool IsPrimaryKeyMember { get; init; }
}

public sealed record CatalogIndex
{
    public required string Name { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsUnique { get; init; }
    public bool IsClustered { get; init; }
    public bool IsColumnStore { get; init; }
    public required IReadOnlyList<string> KeyColumns { get; init; }
}

public sealed record CatalogConstraint
{
    public required string Name { get; init; }
    public ConstraintKind Kind { get; init; }                 // Default, Check, ForeignKey, PrimaryKey, Unique
    /// <summary>Columns this constraint binds, so the sync planner knows what blocks an ALTER COLUMN.</summary>
    public required IReadOnlyList<string> Columns { get; init; }
    public string? Definition { get; init; }                  // for diagnostics and drift detail
}

public enum ConstraintKind { Default, Check, ForeignKey, PrimaryKey, Unique }
```

This single record resolves the critic's "computed columns half-wired" and "ALTER blocked by dependency" findings at the source: the reader returns indexes **and** constraints, so the sync planner can reason about every dependency on a column before it emits an ALTER, and can emit (or report) computed-column DDL deliberately.

---

## 2. Source discovery

### 2.1 Abstraction, factory, and where it lives

```
src/SqlFlow.Core/Catalog/
  ICatalogReader.cs            ICatalogReaderFactory.cs
  CatalogModel.cs              CatalogQuery.cs            ThreePartName.cs
  CatalogException.cs          ICatalogCache.cs           MemoryCatalogCache.cs
  CatalogScaffolder.cs         CatalogService.cs          SqlIdentifier.cs

src/SqlFlow.SqlServer/Catalog/  SqlServerCatalogReader.cs   SqlServerCatalogSql.cs
src/SqlFlow.MySql/Catalog/      MySqlCatalogReader.cs       MySqlCatalogSql.cs
```

```csharp
public interface ICatalogReaderFactory
{
    /// <summary>Selects the reader for the resolved connection's provider. Throws CatalogUnsupportedProviderException
    /// for a kind with no reader, never returns null.</summary>
    ICatalogReader For(ResolvedConnection connection);
}
```

`CatalogService` (Core) is the single orchestrator both CLI and API call. It resolves the reference through the registry, opens the connection, picks the reader, applies caching, and returns DTOs. It is the only place that knows the resolve-open-read sequence.

```csharp
public sealed class CatalogService
{
    public CatalogService(IConnectionResolver resolver, IConnectionFactory factory,
                          ICatalogReaderFactory readers, ICatalogCache cache) { /* ... */ }

    public async Task<IReadOnlyList<DatabaseInfo>> DatabasesAsync(string reference, CatalogQuery q, CancellationToken ct)
    {
        var resolved = await _resolver.ResolveAsync(reference, ConnectionRole.Source, ct);
        return await _cache.GetOrAddAsync(CacheKey.Databases(resolved, q), async () =>
        {
            await using var conn = await _factory.OpenAsync(resolved, ct);
            return await _readers.For(resolved).ListDatabasesAsync(conn, q, ct);
        }, ct);
    }
    // SchemasAsync, ObjectsAsync, SearchAsync, IntrospectAsync, ScaffoldAsync follow the same shape.
}
```

### 2.2 Query, search, pagination

```csharp
public sealed record CatalogQuery
{
    public string? NameLike { get; init; }            // case-insensitive contains; translated to a safe LIKE
    public bool IncludeViews { get; init; } = true;
    public bool IncludeTables { get; init; } = true;
    public bool IncludeSystem { get; init; }          // default false: hide system schemas/objects
    public int Offset { get; init; }
    public int Limit { get; init; } = 200;            // clamped to [1, 2000]
    public ObjectSort Sort { get; init; } = ObjectSort.Name;
}

public sealed record CatalogPage<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required long Total { get; init; }         // COUNT(*) under the same filter, so a UI can page
    public bool HasMore => Offset + Items.Count < Total;
}
```

`NameLike` is never interpolated. It is passed as a parameter, and the literal `LIKE` wildcards (`%`, `_`, `[`) inside the user value are escaped with an explicit `ESCAPE` clause (SQL Server) / backslash escaping (MySQL). Pagination uses `OFFSET @off ROWS FETCH NEXT @lim ROWS ONLY` on SQL Server and `LIMIT @lim OFFSET @off` on MySQL, both over a deterministic `ORDER BY` so paging is stable.

### 2.3 SQL Server catalog SQL (exact, in `SqlServerCatalogSql.cs`)

All names that scope a query (database, schema, object) are validated as identifiers via `SqlIdentifier.Validate` and injected as bracket-quoted identifiers, never as string literals; all user search values are parameters.

List databases (online, accessible, optionally hide system):

```sql
SELECT d.name AS DatabaseName,
       d.database_id AS DatabaseId,
       d.collation_name AS Collation,
       d.state_desc AS State
FROM sys.databases AS d
WHERE d.state = 0                                   -- ONLINE only
  AND HAS_DBACCESS(d.name) = 1                      -- least-privilege: only what the login can enter
  AND (@includeSystem = 1 OR d.database_id > 4)     -- hide master/tempdb/model/msdb
  AND (@nameLike IS NULL OR d.name LIKE @nameLike ESCAPE '\')
ORDER BY d.name
OFFSET @off ROWS FETCH NEXT @lim ROWS ONLY;
```

`HAS_DBACCESS` makes the list least-privilege by construction: a login that cannot enter a database never sees it, and the query never errors on an inaccessible one.

List schemas in a database (the database is a validated identifier used to switch context):

```sql
-- executed against [<database>] via "USE" set on the command's connection context for this call only
SELECT s.name AS SchemaName, p.name AS Owner
FROM sys.schemas AS s
JOIN sys.database_principals AS p ON p.principal_id = s.principal_id
WHERE (@includeSystem = 1 OR s.name NOT IN
        ('sys','INFORMATION_SCHEMA','guest','db_owner','db_accessadmin','db_securityadmin',
         'db_ddladmin','db_backupoperator','db_datareader','db_datawriter','db_denydatareader','db_denydatawriter'))
  AND (@nameLike IS NULL OR s.name LIKE @nameLike ESCAPE '\')
ORDER BY s.name;
```

List tables and views in a scope, with a row count estimate and the total for paging:

```sql
SELECT o.[name]                                  AS ObjectName,
       SCHEMA_NAME(o.schema_id)                  AS SchemaName,
       o.[type]                                  AS ObjectTypeCode,    -- 'U' table, 'V' view
       CAST(ISNULL(ps.row_count, 0) AS bigint)   AS ApproxRows,
       COUNT(*) OVER ()                          AS Total
FROM sys.objects AS o
OUTER APPLY (
    SELECT SUM(p.[rows]) AS row_count
    FROM sys.partitions AS p
    WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)
) AS ps
WHERE o.[type] IN (SELECT v FROM (VALUES ('U'),('V')) AS t(v)
                   WHERE (@includeTables = 1 AND v = 'U') OR (@includeViews = 1 AND v = 'V'))
  AND (@schema IS NULL OR SCHEMA_NAME(o.schema_id) = @schema)
  AND (@nameLike IS NULL OR o.[name] LIKE @nameLike ESCAPE '\')
  AND o.is_ms_shipped = 0
ORDER BY SCHEMA_NAME(o.schema_id), o.[name]
OFFSET @off ROWS FETCH NEXT @lim ROWS ONLY;
```

Free-text search across the database (objects only, ranked exact-then-prefix-then-contains):

```sql
SELECT TOP (@lim)
       SCHEMA_NAME(o.schema_id) AS SchemaName,
       o.[name]                 AS ObjectName,
       o.[type]                 AS ObjectTypeCode,
       CASE WHEN o.[name] = @term THEN 0
            WHEN o.[name] LIKE @prefix ESCAPE '\' THEN 1
            ELSE 2 END          AS Rank
FROM sys.objects AS o
WHERE o.is_ms_shipped = 0
  AND o.[type] IN ('U','V')
  AND o.[name] LIKE @contains ESCAPE '\'
ORDER BY Rank, SchemaName, ObjectName;
```

Full object introspection (`IntrospectObjectAsync`) returns columns, indexes, and constraints in one round trip per concern. Columns:

```sql
SELECT c.column_id                                   AS Ordinal,
       c.[name]                                      AS ColumnName,
       t.[name]                                      AS BaseTypeName,
       c.max_length                                  AS MaxLengthBytes,
       c.[precision]                                 AS Prec,
       c.scale                                       AS Scale,
       c.is_nullable                                 AS IsNullable,
       c.collation_name                              AS Collation,
       c.is_identity                                 AS IsIdentity,
       ic.seed_value                                 AS IdentitySeed,
       ic.increment_value                            AS IdentityIncrement,
       cc.[definition]                               AS ComputedDefinition,
       cc.is_persisted                               AS ComputedPersisted,
       dc.[definition]                               AS DefaultDefinition,
       CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS IsPkMember
FROM sys.columns AS c
JOIN sys.types AS t       ON t.user_type_id = c.user_type_id
LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
OUTER APPLY (
    SELECT TOP 1 ic2.column_id
    FROM sys.indexes AS i
    JOIN sys.index_columns AS ic2 ON ic2.object_id = i.object_id AND ic2.index_id = i.index_id
    WHERE i.is_primary_key = 1 AND ic2.object_id = c.object_id AND ic2.column_id = c.column_id
) AS pk
WHERE c.object_id = @objectId
ORDER BY c.column_id;
```

The reader computes the rendered `NativeType` once, in one place, fixing the legacy off-by-2x churn: for the unicode text types (`nchar`, `nvarchar`) it divides `max_length` by 2 (`-1` stays `-1` for `max`); for everything else it uses the catalog values directly, and it normalizes effective scale (see 3.7). `@objectId` is `OBJECT_ID(@qualified)` evaluated **in the target database context**; for a three-part name the reader sets the connection's database for this call (`conn.ChangeDatabase` or a `USE [db];` prefix on the batch) so `OBJECT_ID` resolves cross-database correctly. This closes the critic's "OBJECT_ID only resolves in current database" gap for both features.

Indexes and constraints come from `sys.indexes`/`sys.index_columns` (with `is_columnstore` derived from `i.type IN (5,6)`) and from `sys.key_constraints` + `sys.check_constraints` + `sys.foreign_keys` joined to their column lists.

### 2.4 MySQL catalog SQL (exact, in `MySqlCatalogSql.cs`)

MySQL has no schema-inside-database layer: a MySQL "schema" is a database. The reader maps `ListDatabasesAsync` and `ListSchemasAsync` to the same `information_schema.schemata`, and reports a single synthetic schema named after the database for object scoping, so the cross-provider model stays uniform.

List databases:

```sql
SELECT s.SCHEMA_NAME AS DatabaseName, s.DEFAULT_COLLATION_NAME AS Collation
FROM information_schema.SCHEMATA AS s
WHERE (? = 1 OR s.SCHEMA_NAME NOT IN ('mysql','information_schema','performance_schema','sys'))
  AND (? IS NULL OR s.SCHEMA_NAME LIKE ? ESCAPE '\\')
ORDER BY s.SCHEMA_NAME
LIMIT ? OFFSET ?;
```

List tables and views:

```sql
SELECT t.TABLE_SCHEMA AS SchemaName,
       t.TABLE_NAME   AS ObjectName,
       t.TABLE_TYPE   AS ObjectTypeText,            -- 'BASE TABLE' / 'VIEW'
       IFNULL(t.TABLE_ROWS, 0) AS ApproxRows
FROM information_schema.TABLES AS t
WHERE t.TABLE_SCHEMA = ?
  AND ( (? = 1 AND t.TABLE_TYPE = 'BASE TABLE') OR (? = 1 AND t.TABLE_TYPE = 'VIEW') )
  AND (? IS NULL OR t.TABLE_NAME LIKE ? ESCAPE '\\')
ORDER BY t.TABLE_NAME
LIMIT ? OFFSET ?;
```

Columns:

```sql
SELECT c.ORDINAL_POSITION              AS Ordinal,
       c.COLUMN_NAME                   AS ColumnName,
       c.DATA_TYPE                     AS BaseTypeName,
       c.CHARACTER_MAXIMUM_LENGTH      AS CharLen,
       c.NUMERIC_PRECISION             AS Prec,
       c.NUMERIC_SCALE                 AS Scale,
       c.DATETIME_PRECISION            AS DtPrec,
       c.IS_NULLABLE                   AS IsNullable,      -- 'YES'/'NO'
       c.COLLATION_NAME                AS Collation,
       c.COLUMN_TYPE                   AS FullType,        -- e.g. 'int unsigned', 'enum(...)'
       c.EXTRA                         AS Extra,           -- 'auto_increment', 'VIRTUAL GENERATED', ...
       c.GENERATION_EXPRESSION         AS GenExpr,
       c.COLUMN_DEFAULT                AS DefaultValue,
       c.COLUMN_KEY                    AS KeyKind          -- 'PRI' marks a PK member
FROM information_schema.COLUMNS AS c
WHERE c.TABLE_SCHEMA = ? AND c.TABLE_NAME = ?
ORDER BY c.ORDINAL_POSITION;
```

The MySQL reader keeps `NativeType = COLUMN_TYPE` for display fidelity (it carries unsigned, enum, etc.) and defers MySQL-to-SQL-Server mapping to scaffold time only.

### 2.5 Least-privilege behavior

The design is least-privilege by construction, not by special-casing: `HAS_DBACCESS` and `is_ms_shipped = 0` mean a low-privilege login simply sees fewer rows rather than hitting an error. A genuine permission denial (e.g. `VIEW DEFINITION` denied on a single object during introspection) is surfaced as a typed `CatalogPermissionException` carrying the object name and the redacted connection only, never swallowed (closing the legacy "empty catch hides permission errors" defect). The reader never assumes `sysadmin`; every query runs with what the ingest login already has.

### 2.6 Caching

```csharp
public interface ICatalogCache
{
    Task<T> GetOrAddAsync<T>(CatalogCacheKey key, Func<Task<T>> factory, CancellationToken ct);
    void Invalidate(string referenceFingerprint);
}
```

`MemoryCatalogCache` is an in-process `IMemoryCache` with a default 60-second TTL for enumerations and 5 minutes for single-object introspection, keyed on a non-secret fingerprint of the `ResolvedConnection` (a hash of `RedactedString` + `Kind`, never the canonical string) plus the query. The CLI `discover` verbs accept `--no-cache`; the schema-sync engine never reads the cache (it always introspects live, because it is about to mutate). A `--refresh` flag and the `Invalidate` method clear a reference's entries when an operator knows the source changed. The cache never stores credentials, only schema metadata.

### 2.7 CLI commands

New verbs under `sqlflow discover`, all delegating to `CatalogService`. Connection is given as `@alias` (full mode) or an inline reference resolved exactly as a flow's source is.

```
sqlflow discover databases  --source @SalesProd [--like ord] [--json] [--no-cache]
sqlflow discover schemas    --source @SalesProd --database AdventureWorks [--like Sales]
sqlflow discover tables     --source @SalesProd --database AdventureWorks --schema Sales
                            [--views] [--no-tables] [--like Order] [--limit 200] [--offset 0]
sqlflow discover search     --source @SalesProd --database AdventureWorks --term Customer
sqlflow discover columns    --source @SalesProd --object [AdventureWorks].[Sales].[SalesOrderHeader]
sqlflow discover scaffold   --source @SalesProd --object [AdventureWorks].[Sales].[SalesOrderHeader]
                            --target @Warehouse --target-object [DW].[stg].[SalesOrderHeader]
                            [--keys SalesOrderID] [--hash] [--out flows/sales_order.yaml]
```

Output is a human table by default and strict JSON with `--json` (so the verbs compose in scripts). Errors print the redacted connection and the typed reason, never the canonical string.

### 2.8 API

A new minimal-API host project `src/SqlFlow.Api` exposes the same `CatalogService`. Read-only `GET` for browsing, `POST` for scaffold (it produces a document):

```
GET  /api/catalog/databases?source=@SalesProd&like=ord
GET  /api/catalog/schemas?source=@SalesProd&database=AdventureWorks
GET  /api/catalog/objects?source=@SalesProd&database=AdventureWorks&schema=Sales&offset=0&limit=200
GET  /api/catalog/search?source=@SalesProd&database=AdventureWorks&term=Customer
GET  /api/catalog/object?source=@SalesProd&object=[AdventureWorks].[Sales].[SalesOrderHeader]
POST /api/catalog/scaffold      { source, object, target, targetObject, keys[], hash }
```

The `source`/`target` reference is resolved server-side through the registry; the API never accepts a raw connection string in a query parameter, so a secret cannot arrive over the wire or land in an access log. Responses are the `CatalogModel` DTOs serialized as-is. Paged endpoints return `CatalogPage<T>`.

### 2.9 Flow scaffolding from a discovered object

`CatalogScaffolder` (Core, pure) turns a `CatalogObject` into an `IngestionFlow`, then serializes it to YAML. It does not invent policy: it maps the discovered shape into the source/target object names, lifts a discovered single-column PK into `Load.KeyColumns` (unless `--keys` overrides), optionally seeds `Change.HashColumns` with all non-key columns when `--hash` is set, and leaves the rest at model defaults so `SchemaSync.Sync = true` lets the sync engine build the actual target shape on first run.

```csharp
public IngestionFlow Scaffold(CatalogObject src, ThreePartName target, ScaffoldOptions opt)
{
    var keys = opt.Keys.Count > 0
        ? opt.Keys
        : src.Columns.Where(c => c.IsPrimaryKeyMember).Select(c => c.Name).ToList();

    return new IngestionFlow
    {
        Source = new IngestionSource { Server = opt.SourceAlias, Table = src.Name.ToRelationalObject() },
        Target = new IngestionTarget { Server = opt.TargetAlias, Table = target.ToRelationalObject() },
        Load   = new IngestionLoadPolicy { KeyColumns = keys },
        Change = opt.Hash
            ? new ChangePolicy { HashColumns = src.Columns.Where(c => !keys.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                                                          .Select(c => c.Name).ToList() }
            : new ChangePolicy(),
        SchemaSync = new SchemaSyncPolicy { Sync = true },
    };
}
```

Because the scaffolder emits the model the sync engine consumes, discovery and sync meet at one model with no parallel authoring path. Discovery is for SQL Server and MySQL; scaffolding always emits a SQL Server target (target is SQL Server by design), mapping a MySQL source column's `NativeType` to a SQL Server type through the same `ISqlTypeMapper` the ingestion engine uses, so the mapping has one home.

---

## 3. Robust schema sync-up

This replaces legacy `SyncSchema` / `SMOTableComparison` / `CommonDB.ExecDDLScript`. It builds on slice 1 (`SqlDataType`, `SqlTypeResolution`, `HashKey`) and reads the target only through `ICatalogReader.IntrospectObjectAsync` (the shared path from section 1). It is transactional, idempotent, concurrency-safe, plan-able, drift-aware, and gated. New code lives in `src/SqlFlow.SqlServer/Schema/`.

### 3.1 The structured column the planner works on (`SqlColumn`)

The Core model `ColumnDefinition` stays string-typed at the boundary. Inside `SqlFlow.SqlServer`, the planner works on `SqlColumn`, which carries the full metadata the legacy diff dropped and is built directly from `CatalogColumn` (no lossy string round-trip):

```csharp
// src/SqlFlow.SqlServer/Schema/SqlColumn.cs
public sealed record SqlColumn
{
    public required string Name { get; init; }
    public required SqlDataType Type { get; init; }
    public bool IsNullable { get; init; } = true;
    public string? Collation { get; init; }
    public IdentitySpec? Identity { get; init; }
    public string? ComputedExpression { get; init; }
    public bool ComputedPersisted { get; init; }
    public string? DefaultExpression { get; init; }
    public ColumnRole Role { get; init; } = ColumnRole.Normal;

    /// <summary>How the value is produced. BulkCopied = a real source column landed by the bulk loader;
    /// Computed = a virtual/system column produced by a select expression at upsert time, never bulk-copied.</summary>
    public ColumnOrigin Origin { get; init; } = ColumnOrigin.BulkCopied;

    /// <summary>For a Computed column, the T-SQL that produces it (a VirtualColumn.SelectExpression or a
    /// system-column default). Drives the upsert select-list, never the bulk copy.</summary>
    public string? SelectExpression { get; init; }

    public static SqlColumn FromCatalog(CatalogColumn c, ColumnRole role = ColumnRole.Normal) => new()
    {
        Name = c.Name, Type = SqlDataType.Parse(c.NativeType), IsNullable = c.IsNullable, Collation = c.Collation,
        Identity = c.IsIdentity ? new IdentitySpec { Seed = c.IdentitySeed ?? 1, Increment = c.IdentityIncrement ?? 1 } : null,
        ComputedExpression = c.ComputedExpression, ComputedPersisted = c.ComputedPersisted,
        DefaultExpression = c.DefaultExpression, Role = role,
    };
}

public sealed record IdentitySpec { public long Seed { get; init; } = 1; public long Increment { get; init; } = 1; }
public enum ColumnOrigin { BulkCopied, Computed }

[Flags]
public enum ColumnRole { Normal = 0, PrimaryKey = 1, HashKey = 2, System = 4, Identity = 8, BusinessKey = 16, Virtual = 32 }
```

`ColumnOrigin` resolves the critic's virtual-column gap: a virtual column is a real schema column that is computed at upsert time, not bulk-copied, and the build records that distinction so the loader's select-list is correct.

### 3.2 The desired-schema builder (`IngestionSchemaBuilder`) - including virtual columns

One builder produces the desired `DesiredTable` for both the staging and target passes. The relational path uses this richer builder; the file path keeps the existing `DesiredSchemaBuilder`; both emit the same `SqlColumn[]` the one planner consumes, so there is a single planning path.

```csharp
public sealed record DesiredTable
{
    public required RelationalObject Object { get; init; }
    public required IReadOnlyList<SqlColumn> Columns { get; init; }
    public required IReadOnlyDictionary<string, string> SourceToTargetNames { get; init; } // bulk-copied columns only
    public required IReadOnlyList<string> CriticalColumns { get; init; }                    // hash key (+ keys per policy 3.6)
    public IndexPlan? Indexes { get; init; }
    public bool TemporalVersioning { get; init; }
}
```

`Build(flow, target, detectedSourceColumns)` runs once, in this order, no parallel path:

1. **Shape detected columns.** Each detected `SourceColumn` (from the source stream, section 3.3) gets overrides applied, then `ISqlTypeMapper.Map` to a type, then `SqlColumn` with `Origin = BulkCopied`. Ignored columns were already projected out of the source SELECT.
2. **Name cleanup** (`IColumnNameCleaner`, when `SchemaSync.CleanColumnNames`): regex clean, empty becomes a configured name, collisions de-duped, all comparisons via the single `IIdentifierComparer` (3.5). Records the source-to-target map for bulk-copied columns.
3. **Unicode conversion** (`IUnicodeConverter`, when `SchemaSync.ConvertUnicodeToNonUnicode`): a `SqlDataType` transform (`nvarchar->varchar`, `nchar->char`, `ntext->text`), so it stays widening-compatible.
4. **Inject virtual columns.** For each `flow.VirtualColumns` entry, materialize a `SqlColumn` with `Role |= Virtual`, `Origin = Computed`, `SelectExpression = vc.SelectExpression`, and a type from `vc.DataType` (parsed) or resolved from `vc.DataTypeExpression`. Virtual columns are **not** added to `SourceToTargetNames` (they are computed, not bulk-copied). This closes the dropped-virtual-column defect and keeps the upsert select-list correct.
5. **Inject system columns** per `SystemColumnsPolicy`: `InsertedDate_DW`/`UpdatedDate_DW`/`DeletedDate_DW` as `datetime2(3)`, `RowStatus_DW` as `char(1)`, all `Role = System`, `Origin = Computed`.
6. **Inject hash key** when `Change.HasHashKey`: `HashKey_DW` typed via `HashKey` (binary(N) by algorithm), `Role = HashKey`, `Origin = Computed`. Present in both staging and target.
7. **Inject identity** (target build only, when `Target.IdentityColumn` set): `int identity(seed,increment) not null`, `Role = Identity | PrimaryKey`, with a clustered PK. Never injected into staging.
8. **Mark business keys.** Columns in `Load.KeyColumns` get `Role |= BusinessKey`.
9. **Nullability invariant** (the monotonic-nullability rule, 3.7): staging forces all non-PK columns nullable (computed in memory from the built set, not via a `WITH (NOLOCK)` catalog read). Target nullability follows override/source but is reconciled monotonically by the planner (never tightened on an existing populated column).
10. **Order:** PK-prefixed, PK-suffixed, ordinary in source order, virtual columns, then `_DW` columns last. Deterministic CREATE output.

`timestamp`/`rowversion` becomes a plain nullable `binary(8)`, excluded from the hash and the insert list, never NOT NULL.

### 3.3 Source-stream schema detection (the shared reader, used for the source side)

The source shape is detected by executing the shaped projection with a zero-row guard and reading the result-set metadata, cross-provider, over the registry connection. This is the same firewall-permitted path discovery uses and the V3 equivalent of legacy `SQLReaderSchema`:

```csharp
public interface ISourceSchemaReader
{
    Task<IReadOnlyList<SourceColumn>> GetResultSchemaAsync(ResolvedConnection src, string shapedSelect, CancellationToken ct);
}
```

The SQL Server implementation uses `sp_describe_first_result_set` (no `SET FMTONLY`, which is deprecated and unreliable for procedures); MySQL prepares the statement and reads `MySqlDataReader.GetSchemaTable()` from a `WHERE 1=0` execution. The detected `SourceColumn[]` (with its true collation) feeds both the staging build and the key/hash collation gate (3.6), so the gate compares the genuine source against the target, never the engine-created staging table against the target.

### 3.4 The planner (`SchemaEvolutionPlanner`): desired vs live -> typed plan

```csharp
public sealed record EvolutionPlan
{
    public required RelationalObject Object { get; init; }   // full three-part, bracket-escaped name (not bare strings)
    public bool CreateTable { get; init; }
    public IReadOnlyList<SqlColumn> ColumnsToAdd { get; init; } = [];
    public IReadOnlyList<ColumnAlter> ColumnsToAlter { get; init; } = [];
    public IReadOnlyList<CriticalMismatch> CriticalMismatches { get; init; } = [];
    public IReadOnlyList<DriftFinding> Drift { get; init; } = [];
    public IndexPlan? Indexes { get; init; }                 // meaningful on CreateTable
    public bool CreateTemporalVersioning { get; init; }
    public bool Blocks => CriticalMismatches.Any(m => m.Blocks);
    public bool HasDdl => CreateTable || ColumnsToAdd.Count > 0 || ColumnsToAlter.Count > 0;
}

public sealed record ColumnAlter
{
    public required string Name { get; init; }
    public required SqlDataType From { get; init; }
    public required SqlDataType To { get; init; }
    public required bool TargetNullable { get; init; }       // monotonic on nullability (3.7)
    public string? Collation { get; init; }
    /// <summary>Indexes that bind this column and must be dropped and recreated around the ALTER, in order.</summary>
    public IReadOnlyList<CatalogIndex> DependentIndexes { get; init; } = [];
}

public sealed record CriticalMismatch { public required string Column { get; init; } public required string Reason { get; init; } public bool Blocks { get; init; } }
public enum DriftKind { Identity, Index, ComputedExpression, Default, Collation, ExtraTargetColumn, NullabilityTightenRequested, DependencyBlocksAlter }
public sealed record DriftFinding { public required DriftKind Kind { get; init; } public required string Detail { get; init; } }
public sealed record GateDecision { public bool Blocked { get; init; } public IReadOnlyList<CriticalMismatch> Reasons { get; init; } = []; public static GateDecision Pass => new(); }
```

Algorithm (one pass):

- **`live is null`** -> `CreateTable = true`, `ColumnsToAdd = desired.Columns`, `Indexes = desired.Indexes`, no alters, no gate. The create body emits computed columns as `AS (<expr>) [PERSISTED]` and the identity/PK/columnstore/temporal deliberately (no half-wiring).
- **Otherwise**, build a case-correct lookup of live columns via `IIdentifierComparer` (3.5). For each desired column:
  - **Not in live** -> `ColumnsToAdd`, forced nullable (no backfill). A desired `Identity` column not present on an existing populated table is **not** addable: surfaced as a `DriftFinding(DependencyBlocksAlter)` only if the desired actually requires it; in practice the identity column is created on the first run and is preserved thereafter as `ExtraTargetColumn` drift (see 3.8), so it is never re-added.
  - **In live** -> normalize both effective scales/precisions (3.7), then `SqlTypeResolution.Resolve(live.Type, desired.Type)`:
    - `Keep` -> no type DDL. If it is a critical column (3.6), additionally compare collation **of the source stream vs target** and block on a difference.
    - `Alter(merged)` -> if critical (3.6) this is a blocking `CriticalMismatch`; otherwise a `ColumnAlter` with the dependent-index list attached (3.4.1) and `TargetNullable` per the monotonic-nullability rule.
    - `Incompatible(reason)` -> `CriticalMismatch { Blocks = critical }`; non-critical incompatibles are warn-only and the column is left as-is.
  - **Nullability:** if an override requests NOT NULL on a live column that is currently NULL, this is **never** auto-applied; it is `DriftFinding(NullabilityTightenRequested)`. The planner is monotonic on nullability as well as type.
  - **Drift:** identity seed/increment difference, computed-expression difference, default difference, non-critical collation difference, target-only columns (`ExtraTargetColumn`, never dropped), and index drift vs `desired.Indexes`.

#### 3.4.1 ALTER must not be blocked by a dependency (the fool-proof core)

Before emitting any `ColumnAlter`, the planner consults `live.Indexes` and `live.Constraints` for that column:

- If the column is bound only by ordinary non-clustered indexes that can be recreated, the generator emits **drop dependent indexes -> ALTER COLUMN -> recreate dependent indexes**, all inside the one evolution transaction, in order. The recreated index definitions come from `CatalogIndex` (name, columns, unique/clustered/columnstore), so the table returns to the same index shape.
- If the column is bound by something that cannot be safely recreated around an in-place ALTER (a PRIMARY KEY, a UNIQUE constraint backing a foreign key, a schema-bound view, a CHECK referencing it, a computed column referencing it, or a DEFAULT that conflicts), the planner does **not** emit a doomed ALTER. It records `DriftFinding(DependencyBlocksAlter)` with the precise dependency, leaves the column as-is, and (only if the column is critical) raises a non-forced `CriticalMismatch`. This is exactly the runtime-failure-that-blocks-the-target hole the critic flagged: the planner now reasons about the dependency from `CatalogObject.Indexes`/`Constraints` instead of emitting an ALTER that throws inside the transaction and bricks every re-run.

Monotonic widening comes entirely from `SqlTypeResolution`, so the planner never narrows (legacy narrow-happy bug gone), never swaps decimal precision/scale (slice 1 parses correctly), and never falls through on `datetimeoffset` (family-based, no missing break).

### 3.5 Single identifier comparer

```csharp
public interface IIdentifierComparer : IEqualityComparer<string> { bool CaseSensitive { get; } }
```

One instance, derived from the target database/column collation reported by introspection (`OrdinalIgnoreCase` by default; ordinal when a `_CS_` collation is seen). Injected into the builder, planner, and name-cleaner dedupe. Legacy's mix of ordinal and invariant-ignore-case is impossible, so `Amount` vs `amount` matches on a case-insensitive DB and never produces a duplicate ADD.

### 3.6 The critical-mismatch gate (policy-controlled, legacy-faithful by default)

The gate is computed in the planner (it owns the roles and the resolution result), not as a separate dead query. A column blocks the load when it is a **critical column** and any of: type family changed (`Incompatible`), type widened (`Alter` on a critical column), a length/precision/scale difference, or a **source-vs-target collation difference** (compared correctly, not against itself).

Which columns are critical is policy-controlled to avoid newly blocking migrated flows:

```csharp
public sealed record GatePolicy
{
    /// <summary>Default false: gate only on the hash key, exactly as legacy CriticalMismatch did. Set true to
    /// also protect business keys (a deliberate, documented hardening, not a silent regression).</summary>
    public bool ProtectBusinessKeys { get; init; }
}
```

Default behavior matches legacy precisely: `CriticalColumns = HashKey only`; a business-key type change is a `DriftFinding`, not a block. With `ProtectBusinessKeys = true`, business keys join the critical set. The collation comparison is always source-stream vs target, so a target key column with an intentional non-default collation and an unchanged source is `Keep`, never a false block.

`FlowRunner` (relational variant) checks `plan.Gate.Blocked` after planning and **before any target mutation**; if blocked it fails with the structured reasons in the trace and touches nothing.

### 3.7 Normalization invariants (kill churn and one-way breakage)

- **Effective scale/precision:** before `Resolve`, both sides normalize `datetime2`/`time`/`datetimeoffset` to effective scale 7 when unspecified, and `decimal` to its effective precision/scale, so a desired bare `datetime2` compares equal to a catalog `datetime2(7)` (`Keep`, no churny ALTER each run).
- **Monotonic nullability:** `ColumnAlter.TargetNullable = live.IsNullable || (desiredNotNull && live.IsNullable == false)`. Evolution never tightens NULL to NOT NULL on an existing column regardless of override; a tightening request is drift, never DDL. This covers all alters, not only type widens.
- **N-char length normalized once** in the reader (unicode `/2`, `max` stays `-1`), so nvarchar never looks "changed" on every run.

### 3.8 What feeds the target pass (pins identity/extra-column behavior)

The target evolution pass takes the **freshly created staging table's introspected shape** (post-create `IntrospectObjectAsync`) as its desired input plus the target-only injections (identity, target indexes), exactly as legacy used the staging table as the target's source object. Therefore the identity column lives only on the target (added at create), is absent from the staging-derived desired set, and on every later run is preserved as `ExtraTargetColumn` drift, never re-added and never blocked. Re-runs on an identity-PK table are idempotent.

### 3.9 ALTER-aware, idempotent DDL generation

The generator consumes `EvolutionPlan` and renders every facet, guarded for idempotency. It throws `SchemaEvolutionBlockedException` if `plan.Blocks`, so no DDL is ever produced for a blocked plan. All quoting goes through one shared escaper that reuses `RelationalObject.QualifiedName` semantics (three-part, `]`-escaped), so an object name containing `]` is safe and the database part is never lost.

```csharp
public IReadOnlyList<DdlStatement> Generate(EvolutionPlan plan)
{
    if (plan.Blocks) throw new SchemaEvolutionBlockedException(plan);
    var q = plan.Object.QualifiedName;                  // [Db].[Schema].[Name], ']'-escaped, one source of truth
    var stmts = new List<DdlStatement>();

    if (plan.CreateTable)
    {
        stmts.Add(new($"IF OBJECT_ID(N'{Literal(plan.Object)}','U') IS NULL\nBEGIN\n{CreateBody(plan, q)}\nEND;", DdlKind.CreateTable));
        if (plan.Indexes is { } ix) stmts.AddRange(CreatePkAndIndexes(plan, ix, q));   // PK named PK_<table>, columnstore wired
        if (plan.CreateTemporalVersioning) stmts.Add(new(TemporalVersioning(plan, q), DdlKind.Temporal));
        return stmts;
    }

    foreach (var c in plan.ColumnsToAdd)
        stmts.Add(new($"IF COL_LENGTH(N'{Literal(plan.Object)}',N'{EscLit(c.Name)}') IS NULL\n  ALTER TABLE {q} ADD {ColumnDef(c, forNewTable:false)};", DdlKind.AddColumn));

    foreach (var a in plan.ColumnsToAlter)
    {
        foreach (var ix in a.DependentIndexes)          // drop dependents first (3.4.1)
            stmts.Add(new($"DROP INDEX [{EscLit(ix.Name)}] ON {q};", DdlKind.DropIndex));
        stmts.Add(new($"ALTER TABLE {q} ALTER COLUMN [{EscLit(a.Name)}] {a.To.Render()}{Collate(a.Collation)} {(a.TargetNullable ? "NULL" : "NOT NULL")};", DdlKind.AlterColumn));
        foreach (var ix in a.DependentIndexes)          // recreate after
            stmts.Add(new(RecreateIndex(ix, q), DdlKind.CreateIndex));
    }

    return stmts;
}
```

`CreateBody` emits computed columns as `[c] AS (<expr>)[ PERSISTED]` (deliberate, not a plain column), identity as `IDENTITY(seed,increment)`, and the clustered PK named `PK_<table>` (the legacy `CI_`-vs-`PK_` branch bug is gone). `Collate(...)` returns empty for a null/blank collation (no invalid trailing `COLLATE`). The `COL_LENGTH` guard on ADD makes re-runs no-ops. ALTER never targets a critical column (those are gated) and is widening-only, so it cannot truncate a populated table; with dependent-index drop/recreate it cannot fail on an indexed column.

### 3.10 Transactional, idempotent, concurrency-safe execution

`ApplyEvolutionAsync` runs the whole plan under an object-scoped app lock inside one serializable transaction, checking the lock return code, rolling back on any non-benign failure, and re-introspecting after commit.

```csharp
public async Task<EvolutionOutcome> ApplyEvolutionAsync(DbConnection dbConn, EvolutionPlan plan, CancellationToken ct)
{
    if (plan.Blocks) throw new SchemaEvolutionBlockedException(plan);
    if (!plan.HasDdl) return EvolutionOutcome.NoChange(plan);

    var conn = (SqlConnection)dbConn;                                   // single guarded downcast
    var statements = _ddl.Generate(plan);
    var lockName = $"SqlFlow.Schema:{plan.Object.QualifiedName}";

    await AcquireAppLockOrThrowAsync(conn, lockName, timeoutMs: 30_000, ct);   // checks the return code (below)
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
        foreach (var s in statements)
        {
            await using var cmd = new SqlCommand(s.Text, conn, tx) { CommandTimeout = 0 };
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (SqlException ex) when (s.Kind == DdlKind.CreateTable && ex.Number == 2714) { /* benign create race */ }
        }
        await tx.CommitAsync(ct);
    }
    catch { await tx.RollbackAsync(ct); throw; }                        // partial-failure rollback (legacy had none)
    finally { await ReleaseAppLockAsync(conn, lockName); }              // session-owned, also auto-released on disconnect

    var live = await _reader.IntrospectObjectAsync(conn, plan.Object, ct);   // act on the true post-evolution shape
    return EvolutionOutcome.Applied(plan, live!, statements.Select(s => s.Text).ToList());
}

private static async Task AcquireAppLockOrThrowAsync(SqlConnection conn, string resource, int timeoutMs, CancellationToken ct)
{
    await using var cmd = new SqlCommand("sys.sp_getapplock", conn) { CommandType = CommandType.StoredProcedure };
    cmd.Parameters.AddWithValue("@Resource", resource);
    cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
    cmd.Parameters.AddWithValue("@LockOwner", "Session");               // survives a later tx rollback; auto-frees on disconnect
    cmd.Parameters.AddWithValue("@LockTimeout", timeoutMs);
    var ret = new SqlParameter("@ret", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
    cmd.Parameters.Add(ret);
    await cmd.ExecuteNonQueryAsync(ct);
    var code = (int)ret.Value;                                          // 0/1 acquired; -1 timeout; -2 cancel; -3 deadlock
    if (code < 0)
        throw new SchemaLockTimeoutException(resource, code);           // never proceeds to DDL without the lock
}
```

The lock return code is captured and checked: a failure throws `SchemaLockTimeoutException` (distinguishing timeout from deadlock) and DDL never runs unserialized. The session-owned lock is taken before `BeginTransaction` and released in `finally`; a connection drop auto-frees it, so a crashed run cannot wedge the next. Each statement is its own `SqlCommand` (precise rollback and error attribution), unlike the legacy single-blob execute with the `dontUseTransaction = true` lie.

### 3.11 Staging lifecycle and same-flow concurrency

Staging is canonical per flow, rebuilt each execution, and its lifecycle is driven by the run outcome.

**Naming (canonical, traceable).** The staging object is `[raw].[<targetSchema>_<targetTable>_<flowId>]`. The `raw` schema by itself marks its tables as staging, so the name carries no `stg_` prefix, and the name reads straight back to the target it feeds (for example `[raw].[arc_Orders_279975153]` stages flow 279975153 into `[arc].[Orders]`). Characters outside `A-Z a-z 0-9 _` in the target identifiers fold to `_`; if the composed name would exceed the 128-character identifier limit, the traceable middle is trimmed and the `flowId` suffix, which guarantees uniqueness, never is. The match-keys key table is the same shape with an `mkey_` prefix, so the two work tables a flow can own stay distinguishable side by side.

**Rebuild, and concurrency.** Each run ensures the `raw` schema (guarded `CREATE SCHEMA`), drops the previous incarnation (`DROP TABLE IF EXISTS`), and creates the table fresh through `ApplyDdlAsync` (`IF OBJECT_ID IS NULL` guarded), so staging always starts empty with the run's exact source shape and a flow never owns more than one staging table. Same-flow concurrency is serialized at the run queue, not by naming: the claim's pipeline gate (`RunQueueStore.ClaimSqlTemplate`) never hands out a run whose pipeline already has a running execution, so two runs of one flow cannot contend for the canonical table. The target lock stays keyed on the target object, so distinct flows still parallelize freely.

**Outcome-driven lifecycle (the debugging rule).** On SUCCESS the staging table is dropped by default; set `KeepStagingTable` to retain it. On FAILURE the staging table is ALWAYS kept, regardless of `KeepStagingTable`, because it holds the data needed to debug the failure (it is the forensic record). This replaces the legacy `TruncatePreTableOnCompletion` semantics: keep-and-truncate becomes the explicit opt-in, and drop-on-success is the default.

**No orphan accumulation.** Because the name is canonical, kept-on-failure tables and hard crashes that skip the drop leave at most one staging table (plus one `mkey_` table) per flow, and the next run's rebuild resets it. No age-based sweep, name-embedded timestamp, or staging registry is needed; per-flow cleanup is a single `DROP TABLE IF EXISTS` of a deterministic name.

### 3.12 Plan / dry-run

Plan mode runs detect -> shape -> build -> introspect -> plan -> generate and stops before `ApplyEvolutionAsync`. Run reuses the identical core, so the DDL a dry-run prints is byte-for-byte what Run executes (single code path). The relational plan result:

```csharp
public sealed record IngestionPlan
{
    public required EvolutionPlan StagingPlan { get; init; }
    public required EvolutionPlan TargetPlan { get; init; }
    public required IReadOnlyList<DdlStatement> StagingDdl { get; init; }   // DdlKind-classified
    public required IReadOnlyList<DdlStatement> TargetDdl { get; init; }
    public required GateDecision Gate { get; init; }
    public IReadOnlyList<DriftFinding> Drift { get; init; } = [];
}
```

The CLI `--plan` flag and the API plan endpoint print the classified DDL, the gate decision, and the drift findings, with zero target mutation.

### 3.13 Single-code-path integration with the existing runner

The existing `FlowRunner` (file path) keeps `IDdlGenerator.Generate(TargetSpec, SchemaDelta)` and `ExecuteDdlAsync` for its simpler ADD-only flows. The relational path is driven by a new `IngestionFlowRunner` that reuses `FlowRunner`'s `StageAsync` tracing/event machinery verbatim and calls **only** `SchemaEvolutionPlanner` + `SqlServerDdlGenerator.Generate(EvolutionPlan)` + `ApplyEvolutionAsync`. The two runners share telemetry, not two evolution paths: the relational evolution has exactly one planner, one generator overload, and one apply method, used by both the staging and target passes. There is no second `Generate`/`ExecuteDdlAsync` call site competing with `ApplyEvolutionAsync` on the relational path.

### 3.14 Orchestration sequence (`IngestionFlowRunner`)

1. Resolve source and target through the registry; open both connections.
2. `ISourceSchemaReader.GetResultSchemaAsync(src, shapedSelect)` -> detected `SourceColumn[]` (true collation).
3. `IngestionSchemaBuilder.Build(flow, Staging, detected)` -> staging `DesiredTable`.
4. Drop+create the flow's canonical staging table via `ApplyEvolutionAsync` under the staging lock (3.11).
5. Bulk-copy source into staging following `SourceToTargetNames` (bulk-copied columns only; virtual/system/hash columns are computed, not copied).
6. `IntrospectObjectAsync(staging)` -> the staging live shape; build the target desired from it plus target injections (3.8); `Plan(targetDesired, targetLive)`.
7. Evaluate `plan.Gate`; if blocked, fail before any target mutation (3.6).
8. `ApplyEvolutionAsync(targetPlan)` under the target lock (3.10).
9. Temporal versioning on first create.
10. Upsert (UPDATE-then-INSERT), select-list built from `SqlColumn.Origin`/`SelectExpression` so virtual/system/hash columns are computed and bulk-copied columns are read from staging.

### 3.15 Failure-mode catalogue (justifies "fool-proof")

| Failure mode | Legacy behavior (cited) | V3 handling |
|---|---|---|
| Multi-statement DDL fails midway | No transaction (`ExecDDLScript:1581` hardcodes `dontUseTransaction=true`); partial schema | Serializable transaction, per-statement, full rollback on any non-benign failure (3.10) |
| Re-run after partial failure | Drifted table, different DDL | `IF OBJECT_ID`/`COL_LENGTH` guards make every statement idempotent; re-run no-ops the applied parts |
| ALTER COLUMN on an indexed column | Bare ALTER fails at runtime inside the (non-)transaction; blocks every re-run | Planner reads `live.Indexes`/`Constraints`; drops and recreates dependent indexes around the ALTER in one tx, or reports `DependencyBlocksAlter` and leaves the column (3.4.1) |
| ALTER blocked by PK/view/check/computed | Doomed ALTER | Not emitted; surfaced as drift / non-forced mismatch (3.4.1) |
| Lock acquisition fails under load | `IF OBJECT_ID` + swallow 2714; no real lock; return code never existed | `sp_getapplock` return code captured and checked; `< 0` throws `SchemaLockTimeoutException`; DDL never runs unserialized (3.10) |
| Two concurrent runs of same flow | Shared staging name, column-level races | The run queue's pipeline gate serializes same-flow executions, so the canonical staging table is never shared by two live runs (3.11) |
| Connection drops mid-DDL | Indeterminate | Session-owned lock auto-frees; transaction auto-rolls-back |
| Virtual columns | Injected in legacy; absent from this design's first draft | Materialized as `Origin=Computed` columns, ordered before `_DW`, excluded from bulk copy, present in the upsert select-list (3.2 step 4) |
| Source narrower than target | Forces source type, shrinks/truncates | `SqlTypeResolution` monotonic -> `Keep`, no DDL |
| Source wider than target | Same narrow-happy logic | `Alter(merged)` to the widened type, nullability/collation preserved |
| Cross-family change | Silent doomed ALTER | `Incompatible` -> warn (ordinary) or block (critical) with a structured reason |
| Decimal precision/scale | Swapped (`SMOHelper:1502`) | Slice 1 parses `(precision, scale)` correctly; `WidenDecimal` clamps to 38 |
| `datetimeoffset` parse | Missing `break` falls into Float | Family-based resolution; no fall-through |
| `datetime2` scale churn | n/a | Effective-scale normalization makes bare vs `datetime2(7)` compare `Keep` (3.7) |
| Case mismatch (`Amount` vs `amount`) | Ordinal dict -> duplicate ADD -> error | Single `IIdentifierComparer` honoring collation everywhere (3.5) |
| Collation drift on key column | Dead self-comparison gate | Gate compares source stream vs target collation; critical-column difference blocks (3.6) |
| Non-default target collation, unchanged source | n/a (gate dead) | Compared source-vs-target, not staging-vs-target -> `Keep`, no false block (3.6) |
| Empty/invalid `COLLATE` clause | Emits trailing `COLLATE ` | `Collate(...)` returns empty for null/blank (3.9) |
| `max` types | Two fragile switch passes | `-1` parses and renders cleanly through `SqlDataType` |
| N-char length off-by-2x | `/2` only in one place -> churn | Length normalized once in the reader (3.7) |
| `timestamp`/`rowversion` | Blindly `binary(8)`, NOT NULL insert risk | Desired `binary(8)`, always nullable, excluded from hash/insert |
| Computed columns | Metadata commented out -> plain column, no expression | `ComputedExpression` carried; CREATE emits `AS (...)[PERSISTED]`; drift reported on existing (3.9) |
| Identity drift / re-add on re-run | Never reconciled; delta drops identity | Identity created once; preserved as `ExtraTargetColumn` drift on later runs; never re-added/blocked (3.8) |
| Nullability tightening on populated column | Inert defaults | Monotonic on nullability; NOT NULL request on a NULL column is drift, never DDL (3.7) |
| Index drift | Dead `alterScript` | `Drift(Index)` reported; load-time index manager rebuilds |
| Business-key type change blocking migrated flow | Gated only on hash key | Default matches legacy (hash-key only); business-key protection is opt-in policy (3.6) |
| Columnstore | Read, never applied | Wired deliberately at create from `Target.ColumnStoreIndex` (3.9) |
| Cross-database target OBJECT_ID | URN string surgery | Reader sets the database context for the introspection call; three-part `OBJECT_ID` resolves (2.3) |
| Object name with `]` | URN split breaks | One shared escaper reusing `RelationalObject.QualifiedName`; `]` escaped (3.9) |
| Permission/transient error masked as "not found" | Empty `catch {}` | No empty catches; null means a true absence (`OBJECT_ID IS NULL`); real errors propagate with redacted context |
| Blocked plan still emits DDL | n/a | Generator and `ApplyEvolutionAsync` throw `SchemaEvolutionBlockedException`; no DDL for a blocked plan (3.9, 3.10) |

---

## 4. C# contracts and where each lives

**`SqlFlow.Core` (provider-neutral; no SqlClient):**
- `Catalog/ICatalogReader.cs`, `ICatalogReaderFactory.cs`, `CatalogModel.cs` (`CatalogObject`, `CatalogColumn`, `CatalogIndex`, `CatalogConstraint`, `DatabaseInfo`, `SchemaInfo`, `ObjectInfo`, `ObjectMatch`, `CatalogPage<T>`), `CatalogQuery.cs`, `ThreePartName.cs`, `CatalogException.cs` (`CatalogPermissionException`, `CatalogNotFoundException`, `CatalogUnsupportedProviderException`), `ICatalogCache.cs`, `MemoryCatalogCache.cs`, `CatalogScaffolder.cs`, `CatalogService.cs`, `SqlIdentifier.cs`.
- `Engine/IIdentifierComparer.cs`, `IColumnNameCleaner.cs` + `ColumnNameCleaner.cs`, `IngestionFlowRunner.cs`.
- `Abstractions/ISourceSchemaReader.cs`.
- `Model/Results.cs`: add `IngestionPlan`.
- `Ingestion/RelationalObject.cs`: add `ToThreePartName()` adapter (and `ThreePartName.ToRelationalObject()`).

**`SqlFlow.SqlServer` (the SQL Server provider; structured types live here):**
- `Catalog/SqlServerCatalogReader.cs`, `SqlServerCatalogSql.cs` (implements `ICatalogReader`, both granularities; the one `IntrospectObjectAsync` the sync engine shares).
- `Schema/SqlColumn.cs`, `LiveTable.cs` (thin alias over `CatalogObject` for the planner), `EvolutionPlan.cs` (`EvolutionPlan`, `ColumnAlter`, `CriticalMismatch`, `DriftFinding`, `GateDecision`, `GatePolicy`, `IndexPlan`, `DdlStatement`/`DdlKind`, `EvolutionOutcome`), `SchemaEvolutionPlanner.cs`, `IngestionSchemaBuilder.cs`, `IUnicodeConverter.cs` + `UnicodeConverter.cs`, `SqlServerSourceSchemaReader.cs`, `SchemaEvolutionExceptions.cs` (`SchemaEvolutionBlockedException`, `SchemaLockTimeoutException`).
- `SqlServerSchemaProvider.cs`: add `ApplyEvolutionAsync` (delegating introspection to `ICatalogReader`); keep `GetTableSchemaAsync`/`ExecuteDdlAsync` for the file path.
- `SqlServerDdlGenerator.cs`: add `Generate(EvolutionPlan)` (ALTER COLUMN with dependent-index handling, guarded idempotent CREATE/ADD, computed columns, PK/clustered/columnstore/temporal, blocked-plan guard); keep the `SchemaDelta` overload.

**New `SqlFlow.MySql`:** `Catalog/MySqlCatalogReader.cs`, `MySqlCatalogSql.cs`, `MySqlConnectionFactory.cs` (returns `DbConnection`). MySQL is a discovery source only; the target is always SQL Server.

**New `SqlFlow.Api`:** minimal-API host exposing `CatalogService` and the ingestion plan endpoint.

**`SqlFlow.Cli`:** new `discover` verb group; `--plan` on the ingestion run.

**Reused unchanged:** `IConnectionResolver`/`IConnectionFactory`/`ResolvedConnection` (the one firewall-permitted connection), `SqlDataType`/`SqlTypeResolution`/`HashKey` (slice 1), `ISqlTypeMapper`, `IBulkLoader`, `FlowRunner.StageAsync` tracing/events.

---

## 5. Integration plan (checklist against the existing tree)

- [ ] Add `SqlFlow.Core/Catalog/*` (reader contract, model, query, exceptions, cache, service, scaffolder, `SqlIdentifier`, `ThreePartName`).
- [ ] Add `RelationalObject.ToThreePartName()` and `ThreePartName.ToRelationalObject()`.
- [ ] Add `SqlFlow.Core/Abstractions/ISourceSchemaReader.cs`.
- [ ] Add `SqlFlow.Core/Engine/IIdentifierComparer.cs`, `IColumnNameCleaner.cs` + impl, `IngestionFlowRunner.cs`.
- [ ] Extend `SqlFlow.Core/Model/Results.cs` with `IngestionPlan`.
- [ ] Add `SqlFlow.SqlServer/Catalog/SqlServerCatalogReader.cs` + `SqlServerCatalogSql.cs`.
- [ ] Add `SqlFlow.SqlServer/Schema/{SqlColumn,LiveTable,EvolutionPlan,SchemaEvolutionPlanner,IngestionSchemaBuilder,UnicodeConverter,SqlServerSourceSchemaReader,SchemaEvolutionExceptions}.cs`.
- [ ] Extend `SqlServerSchemaProvider.cs` with `ApplyEvolutionAsync` (introspect via `ICatalogReader`).
- [ ] Extend `SqlServerDdlGenerator.cs` with `Generate(EvolutionPlan)`.
- [ ] Create `SqlFlow.MySql` project: `MySqlCatalogReader`, `MySqlCatalogSql`, `MySqlConnectionFactory`; register in the reader/connection factories.
- [ ] Create `SqlFlow.Api` host: catalog endpoints + ingestion plan endpoint.
- [ ] Add `discover` verbs and `--plan` to `SqlFlow.Cli`.
- [ ] DI: register `ICatalogReaderFactory`, `CatalogService`, `ICatalogCache`, `IIdentifierComparer`, `ISourceSchemaReader`, `IngestionFlowRunner`.
- [ ] Tests (each a regression for a cited defect): widen an indexed column (drop/recreate); widen a column with a default; lock-timeout throws and runs no DDL; concurrent same-flow runs use distinct staging; virtual column appears in staging/target and in the upsert select-list; key column with non-default collation + unchanged source is `Keep`; identity-PK table re-run is idempotent; NOT NULL override on a NULL column is drift not DDL; `datetime2` vs `datetime2(7)` is `Keep`; object name containing `]`; decimal fixture other than `(18,2)`; MySQL discovery + scaffold to a SQL Server target; least-privilege login sees a reduced list, not an error.

---

## 6. Build slices (dependency order)

1. **Shared catalog foundation.** `ICatalogReader` + model + `ThreePartName` + `SqlIdentifier` + `SqlServerCatalogReader.IntrospectObjectAsync` (the one method both features need first). Gate: introspect a real SQL Server table and round-trip its columns through `SqlDataType`.
2. **Discovery enumeration + service + cache.** The list/search/page methods, `CatalogService`, `MemoryCatalogCache`. Gate: enumerate databases/schemas/tables over a registry alias under a low-privilege login.
3. **ALTER-aware planner + generator.** `SqlColumn`, `EvolutionPlan`, `SchemaEvolutionPlanner` (using slice 1 + dependency-aware ALTER), `SqlServerDdlGenerator.Generate(EvolutionPlan)`, `IIdentifierComparer`, normalization invariants. Gate: the planner-and-generator regression tests in section 5.
4. **Transactional apply.** `ApplyEvolutionAsync` (checked app lock, serializable tx, rollback, re-introspect), `SchemaEvolutionExceptions`. Gate: partial-failure rollback and lock-timeout tests.
5. **Desired-schema builder + source reader + runner.** `IngestionSchemaBuilder` (virtual/system/hash/identity injection, ordering), `ISourceSchemaReader`, `IngestionFlowRunner` two-pass orchestration, `IngestionPlan`, `--plan`. Gate: end-to-end staging-then-target evolution with a virtual column and an identity PK, idempotent on re-run.
6. **MySQL provider.** `MySqlCatalogReader` + connection factory. Gate: discover a MySQL source and scaffold a SQL Server ingestion flow.
7. **API + CLI surface.** `SqlFlow.Api` endpoints and `sqlflow discover` verbs over `CatalogService`. Gate: browse and scaffold through both surfaces.
8. **Scaffolding.** `CatalogScaffolder` producing `IngestionFlow` YAML from a discovered object. Gate: scaffold, then run the scaffolded flow to create the target.

---

## 7. Open decisions for the user

1. **Business-key gate default.** Either (a) match legacy exactly (gate on the hash key only; business-key type changes are drift), or (b) protect business keys by default. Recommendation: **(a)** as the default with `GatePolicy.ProtectBusinessKeys` available to opt in, so migrated flows behave as before and hardening is a deliberate choice.

2. **Dependent-index ALTER policy.** When a widened ordinary column is bound by a recreatable non-clustered index, either (a) auto drop/recreate the index inside the evolution transaction, or (b) report it as drift and require an explicit `--allow-index-rebuild` flag. Recommendation: **(a)** for non-unique, non-constraint-backing indexes (safe and keeps the flow moving), **(b)** for anything constraint-backing.

3. **Catalog cache default TTL.** Either (a) 60 s enumerations / 5 min object introspection as drafted, or (b) cache disabled by default and opt-in with `--cache`. Recommendation: **(a)**; discovery is read-mostly and the TTL is short, and the sync engine never uses the cache anyway.

4. **API authentication surface.** Either (a) ship `SqlFlow.Api` with no built-in auth (assume it sits behind an existing gateway/network boundary), or (b) require an API key/bearer token in the host. Recommendation: **(b)** a pluggable bearer/API-key check on by default, since the API resolves registry aliases and an unauthenticated caller could enumerate a source's structure.

5. **MySQL scaffold type mapping ownership.** Either (a) reuse the ingestion `ISqlTypeMapper` for MySQL-to-SQL-Server scaffold mapping, or (b) a dedicated MySQL mapping table in the catalog layer. Recommendation: **(a)**, so there is one CLR/native-to-SQL-Server mapping home and scaffolding cannot drift from what ingestion actually produces.

## 3.12 Non-Blocking, Minimal-Footprint Schema-Change Execution Model

This section is the definitive, production-grade execution model for applying an `EvolutionPlan`. It supersedes the earlier `ExecuteDdlAsync` single-transaction path (`src/SqlFlow.SqlServer/SqlServerSchemaProvider.cs:44-71`) and the `SERIALIZABLE` `ApplyEvolutionAsync` sketch in 3.10. It plugs directly onto the implemented `SchemaEvolutionPlanner` / `EvolutionPlan` and onto the per-execution staging lifecycle. There is one apply path; metadata-only and table-rewrite changes flow through the same method and differ only in transaction boundary and emitted clauses.

### 3.12.1 The non-blocking guarantee, stated precisely

**Guarantee (cross-table, durable):** A schema change on table A can never block a data pipeline operating on a different table B.

This holds because the only locks a schema change acquires are both object-scoped to A:

1. A `sp_getapplock` lock whose resource string is derived from A's fully qualified, bracket-escaped name. Two different objects produce two different resource strings, so SQL Server grants two independent locks that are never compatible-tested against each other. The lock lives in the current database, not server-wide.
2. The schema-modification (Sch-M) lock that `ALTER TABLE` / `CREATE TABLE` inherently takes on A only. Sch-M is object-scoped: it does not lock the database and does not lock B.

A data pipeline on B takes IS/S/IX/X locks on B and, while reading the catalog to resolve B, brief Sch-S latches on `sys` rows. None of those are compatible-incompatible with A's Sch-M lock or with A's app-lock resource string, so there is zero contention between "schema change on A" and "pipeline on B".

**Honest scope of the same-object app lock (corrected from 3.10).** The app lock is acquired on the connection that `ApplyDdlAsync` owns, with `@LockOwner = Session`, and is released before `ApplyDdlAsync` returns. The bulk load (`SqlBulkLoader.LoadAsync`, its own `SqlConnection`), the index disable/rebuild (`SqlServerIndexManager`, its own `SqlConnection`), and the truncate each run on separate connections. A session-scoped app lock dies with the connection that holds it. Therefore the app lock serializes **only the DDL instant for the same object**: two concurrent runs targeting the *same* table cannot be inside the DDL region simultaneously, so they cannot both be mutating A's shape at the same moment. It does **not** hold across the subsequent load and upsert, and it was never intended to. Same-object safety *across* the load is provided not by the app lock but by two properties that already hold:

- The generated DDL is idempotent (`IF COL_LENGTH(...) IS NULL` for adds, `IF OBJECT_ID(...) IS NULL` for create, type-equality guards for alters), so a second run that finds the column already widened emits nothing.
- A bulk load whose column map is fixed at open time tolerates a concurrent additive `ADD COLUMN` of a new nullable column on the same table: the new column is not in the copy's column map and existing rows already carry NULL for it.

What the app lock deliberately does *not* attempt: serializing an in-flight `SqlBulkCopy` against a concurrent same-object widening ALTER. That scenario is prevented at the orchestration level (a single flow does not load and evolve the same target concurrently with itself; two distinct flows writing the same physical target is a configuration error the connection registry surfaces, not a lock the DDL applier can hold across foreign connections). If a deployment genuinely requires DDL+load+upsert to be one serialized critical section per object, that requires a shared `SqlConnection`/owner threaded through `FlowRunner` across all three steps; that is explicitly out of scope for this model and is called out as the only way to extend the guarantee, never silently assumed.

### 3.12.2 Lock scope and acquisition

**Resource naming, escaped and case-canonical.** The resource string is the single lever deciding what serializes against what. It must be built from a fully bracket-escaped name, and it must be case-canonical so that two references to the same object on a case-insensitive instance map to the same lock.

The implemented `TargetSpec.QualifiedName` (`src/SqlFlow.Core/Model/FlowDefinition.cs:91`) is `$"[{Schema}].[{Table}]"` with **no `]`-escaping**, unlike `RelationalObject.QualifiedName` (`src/SqlFlow.Core/Ingestion/RelationalObject.cs:18`) which escapes via `Replace("]", "]]")`, and unlike `SqlServerIndexManager.Qualify` (`src/SqlFlow.SqlServer/SqlServerIndexManager.cs:98-100`) which also escapes. We do not feed the unescaped `TargetSpec.QualifiedName` to either the lock key or the DDL. The fix has two coordinated parts:

1. Escape `TargetSpec.QualifiedName` to match the rest of the codebase, so it becomes a true single source of truth for the bracketed name used in DDL:

   ```csharp
   // src/SqlFlow.Core/Model/FlowDefinition.cs
   public string QualifiedName => $"[{Escape(Schema)}].[{Escape(Table)}]";
   private static string Escape(string part) => part.Replace("]", "]]", StringComparison.Ordinal);
   ```

2. Derive the lock resource from a **case-canonical key**, independent of the bracketed name, so `[dbo].[Customer]` and `[dbo].[customer]` (the same object on a case-insensitive instance) serialize correctly. The canonical key folds the raw schema and table to invariant lower case and is the value passed to `sp_getapplock`. Folding is the correct default because the overwhelming majority of SQL Server instances use a case-insensitive collation; a case-sensitive instance where `Customer` and `customer` are genuinely two tables is rare, and over-serializing those two (the only downside of folding) is harmless to correctness and affects only same-name DDL throughput, never cross-table pipelines.

   ```csharp
   // src/SqlFlow.SqlServer/SqlServerSchemaProvider.cs
   // Case-canonical, collation-independent. sp_getapplock compares the resource
   // string with an ordinal/binary comparison, so we fold case ourselves.
   internal static string SchemaLockResource(string schema, string table)
       => "SqlFlow.Schema:"
          + schema.ToLowerInvariant() + "." + table.ToLowerInvariant();
   ```

   The resource is *not* the bracketed DDL name, so a `]` in an identifier cannot break the lock string, and it sidesteps escaping entirely for the lock. The bracketed (escaped) name is used only inside the DDL text. We never hand-format the bracketed name twice; the DDL uses `target.QualifiedName`.

   For the staging table the resource is built from the raw schema and the flow's canonical staging name `<targetSchema>_<targetTable>_<flowId>`, so target evolution and staging create serialize per object; distinct flows never share a staging name, and same-flow runs are already serialized by the run queue's pipeline gate.

**Acquisition is polite (fail fast, no queue-blocking).** `sp_getapplock` is called with `@LockMode = Exclusive`, `@LockOwner = Session`, and `@LockTimeout` equal to the configured acquisition timeout (default 30000 ms). `Session` owner means the lock outlives the DDL transaction's commit/rollback so the same connection can run metadata-only and rewrite transactions back to back under one lock, and a dropped connection auto-frees it so a crashed run never wedges the next. The return code is checked: `0`/`1` acquired; any value `< 0` (`-1` timeout, `-2` caller cancelled, `-3` deadlock, `-999` validation) throws the typed `SchemaLockTimeoutException`, and no DDL runs unlocked.

**`SET LOCK_TIMEOUT` governs the DDL's own Sch-M waits (corrected from the prior draft).** `sp_getapplock @LockTimeout` governs only the acquisition of our app lock (contention with another schema run on the *same* object). It does not govern the Sch-M lock that the `ALTER`/`CREATE` statements themselves wait for when the target table is busy with active readers and writers. That wait is governed by the session `LOCK_TIMEOUT`. `SET LOCK_TIMEOUT` applies to all ordinary engine lock-acquisition waits on the session, **including Sch-M**. We therefore set it before running DDL:

```sql
SET LOCK_TIMEOUT 5000;   -- ms; a blocked DDL statement aborts with error 1222 instead of queuing
```

With this, an `ALTER TABLE` that cannot immediately acquire its Sch-M lock (because a long query holds an incompatible lock on the table) aborts after 5 seconds with SQL error **1222** rather than sitting at the head of the lock queue. Sitting at the head of the queue is itself a blocking event: a pending Sch-M request blocks *new* shared-lock requests behind it, so a DDL statement that waited indefinitely would block the very readers it is queued behind. Failing fast at 1222 prevents the schema run from becoming a blocker. We translate 1222 (and the related `1204` lock-resources and `1205` deadlock-victim) into `SchemaLockTimeoutException` so the caller retries the flow later, and we leave `LOCK_TIMEOUT` on this short-lived connection (the connection is disposed at method end; no reset needed because nothing else reuses it).

The two timeouts are complementary, not redundant: `@LockTimeout` is app-lock acquisition (same-object schema-run contention), `LOCK_TIMEOUT` is engine lock acquisition (the DDL's Sch-M wait against active table users). `LOCK_TIMEOUT` is distinct from `CommandTimeout`: we keep `CommandTimeout = 0` so a *legitimately slow but unblocked* metadata operation is not killed mid-flight; the politeness comes from `LOCK_TIMEOUT` (blocked-on-a-lock) for every statement and from `WAIT_AT_LOW_PRIORITY` for the index rebuilds, never from a blunt command timeout. The current code conflates the two: `SqlServerSchemaProvider.cs:60` uses the 30 s ADO.NET default, so "I am blocked" and "I am slow" both surface as the same generic timeout.

**`WAIT_AT_LOW_PRIORITY` belongs to index rebuilds, not to `ALTER COLUMN` (corrected from the prior draft).** The `low_priority_lock_wait` clause is **not valid grammar on `ALTER TABLE ... ALTER COLUMN`**. Writing `ALTER TABLE [t] ALTER COLUMN [c] bigint WITH (WAIT_AT_LOW_PRIORITY ...)` is a syntax error. The clause is accepted only on `ALTER TABLE ... SWITCH`, partition operations, and `ALTER INDEX ... REBUILD WITH (ONLINE = ON (WAIT_AT_LOW_PRIORITY ...))`. Therefore:

- For the **table-rewrite `ALTER COLUMN` itself** (for example int to bigint), the only polite lever is the session `LOCK_TIMEOUT`: the rewrite's Sch-M acquisition fails fast at 1222 instead of queuing in front of readers. The "yields to active queries" property for the `ALTER COLUMN` is derived from `LOCK_TIMEOUT`, not from `WAIT_AT_LOW_PRIORITY`. Once the rewrite *acquires* the Sch-M lock it does hold it for the duration of the row rewrite; that is intrinsic to in-place `ALTER COLUMN` and is exactly why a rewrite is gated by policy (3.12.4) rather than run silently.
- `WAIT_AT_LOW_PRIORITY (MAX_DURATION = N MINUTES, ABORT_AFTER_WAIT = SELF)` and `ONLINE = ON` are emitted **only on the `ALTER INDEX ... REBUILD` statements** in the drop-and-rebuild dance that surrounds a rewrite, and on the post-load index rebuilds (`SqlServerIndexManager.RebuildAsync`). There `WAIT_AT_LOW_PRIORITY` lets the rebuild wait at low priority so it does not block new queries, and `ABORT_AFTER_WAIT = SELF` aborts the rebuild (not the user queries) if it cannot acquire its lock within `MAX_DURATION`.

**`ONLINE = ON` is edition-gated.** `ONLINE = ON` is Enterprise/Developer/Evaluation and Azure SQL only; Standard rejects it. We detect the engine edition once via `SELECT SERVERPROPERTY('EngineEdition')` (value `3` = Enterprise/Developer/Evaluation, `5` = Azure SQL Database, `8` = Azure SQL Managed Instance all support online index operations) and cache it per connection string. The generator emits `ONLINE = ON` and the `WAIT_AT_LOW_PRIORITY` sub-clause only when the edition supports it; on Standard it emits a plain `REBUILD` so the DDL does not fail.

**Isolation level: READ COMMITTED, not SERIALIZABLE.** The DDL transaction runs at the connection default, READ COMMITTED (equivalent to passing no isolation argument). SERIALIZABLE (3.10) is both unnecessary and mildly harmful here:

- *Unnecessary:* serialization of same-object DDL is already provided by the app lock, so no two sessions are inside the DDL region for the same object at once. There is nothing for SERIALIZABLE to protect.
- *Harmful:* SERIALIZABLE makes the transaction hold key-range / range-S locks on everything it reads, including the catalog reads DDL performs to resolve object ids, columns, and index metadata. Those range locks on `sys` rows are held until commit and can collide with another session's metadata reads whose catalog rows fall in the same range. That is a broader-than-object lock footprint we exist to avoid.
- *No multi-statement read consistency is needed:* the idempotency guards are point catalog lookups (`OBJECT_ID`, `COL_LENGTH`) evaluated under the app lock, not repeatable reads across statements.

A footnote on catalog contention, to keep the guarantee honest: `CREATE TABLE` and `ALTER TABLE` take short-lived X locks on the catalog rows for the object and its schema (`sys.objects`, `sys.columns`, `sysschobjs`) for the DDL instant. Two concurrent schema *evolutions* on *different* tables of the *same* schema can therefore briefly serialize on those catalog locks for the DDL instant. This does not affect the requirement: the requirement is that a schema change must not block *data pipelines* on other tables, and those pipelines take S/IS locks compatible with the brief catalog activity. Schema-evolution-versus-schema-evolution on different tables of one schema briefly serializing at the catalog level is acceptable and bounded to the DDL instant.

### 3.12.3 Transaction scoping: DDL only, short, separate from load and upsert

The legacy single-blob model is the core defect: `ExecuteDdlAsync` is the vehicle for evolution DDL (`FlowRunner.cs:104`), user PreProcess (`:109`), and user PostProcess (`:163`), each call wrapping its whole statement list in one READ COMMITTED transaction, so a multi-statement PreProcess holds statement 1's object locks until statement N commits, and a rewrite ALTER in the evolution batch holds the table Sch-M lock for the full rewrite under one commit boundary.

The new model splits by commit boundary:

- **In the DDL transaction(s):** only `EvolutionPlan` DDL (create / add / alter). Nothing else.
- **Metadata-only changes** (create table, additive nullable adds, metadata-only alters) commit together in **one** short READ COMMITTED transaction: atomic, additive, idempotent on re-run.
- **Each table-rewrite ALTER** commits in **its own** short transaction. A rewrite failure then never rolls back the additive work that already committed, and a long rewrite Sch-M lock is held alone and briefly, never bundled with unrelated statements.
- The object Sch-M lock and the app lock are both released **before `ApplyDdlAsync` returns**, hence before the bulk load (`FlowRunner.cs:126`) and any upsert run. The long-running load never holds a schema-change transaction, and the schema-change transaction never holds a lock during the load.
- **Not in the schema path:** user PreProcess / PostProcess. Mixing arbitrary user SQL into the object-locked, idempotent schema transaction would hand user scripts the schema lock and atomicity semantics they were not designed for, and would overload one method for three jobs (a single-code-path violation in the wrong direction). They move to a separate `IScriptExecutor` (3.12.5) that runs on its own connection with no app lock and no `LOCK_TIMEOUT` override.

### 3.12.4 Minimal-footprint classification and policy

**The `ChangeFootprint` enum (two values, not a gradient).** At the SQL Server lock level the cost is binary: either the engine flips a catalog bit (metadata-only, Sch-M held for microseconds) or it touches every data page under a held Sch-M lock (rewrite). A gradient invites a fuzzy threshold and a silent middle ground; two values force every change to be explicitly one or the other.

```csharp
// src/SqlFlow.SqlServer/Schema/EvolutionPlan.cs
public enum ChangeFootprint
{
    /// <summary>Recorded in metadata only; no data pages touched; Sch-M held for microseconds. Safe inline.</summary>
    MetadataOnly,

    /// <summary>Forces a full in-place row rewrite under a table Sch-M lock held for the whole rewrite,
    /// blocking every reader/writer/pipeline on THIS table. Never applied silently inline; gated by policy.</summary>
    TableRewrite,
}
```

**Exact metadata-only versus table-rewrite rules.** These are computed from the structured `SqlDataType` pair (`FromType`, `ToType`) the planner already produces in `ColumnAlter`, so there is no string sniffing. They line up exactly with what `SqlTypeResolution.WidenWithinFamily` can emit (`src/SqlFlow.SqlServer/Schema/SqlTypeResolution.cs:73-155`).

A change is **`TableRewrite`** when any of the following holds:

- **Integer rank increase across the storage-width boundary:** `Family == Integer` and the byte width grows. `tinyint`(1 byte) -> `smallint`(2) -> `int`(4) -> `bigint`(8). Any rank increase is a fixed-width rewrite. (`WidenInteger` emits exactly these promotions.)
- **Decimal precision crossing a storage-class boundary:** `Family == Decimal` and the source and target precision fall in different decimal storage classes. The class boundaries are precision `1-9` (5 bytes), `10-19` (9 bytes), `20-28` (13 bytes), `29-38` (17 bytes). Growth that crosses any boundary is a rewrite; growth that stays in the same class is metadata-only. Scale change within the same byte class is metadata-only. (`WidenDecimal` can cross these.)
- **Fixed-width text or binary widening:** `char(n)` -> `char(m)` with `m > n`, or `binary(n)` -> `binary(m)` with `m > n`. Fixed-width widening rewrites every row to the new fixed length. (`WidenText`/`WidenBinary` produce this when both sides are fixed.)
- **Any transition to `(max)`:** `varchar(n)` -> `varchar(max)`, `nvarchar(n)` -> `nvarchar(max)`, `varbinary(n)` -> `varbinary(max)` (`ToType.IsMax`). Moving an in-row variable column to LOB storage is a rewrite.
- **`datetime2` / `time` / `datetimeoffset` scale increase crossing a storage byte boundary:** scale `0-2` (smaller), `3-4` (medium), `5-7` (larger) occupy different byte widths; a scale increase crossing one of these boundaries rewrites. Same-byte-class scale change is metadata-only. (`WidenDateTime` widens scale of an identical base.)

A change is **`MetadataOnly`** otherwise, specifically:

- Every additive `ADD [c] <type> NULL` on an existing table. The implemented generator forces existing-table adds to nullable with no default (`SqlServerDdlGenerator.cs:43-45`), and a nullable add with no default never touches existing rows. So **every add on an existing table is `MetadataOnly`**.
- `varchar(n)` -> `varchar(m)` and `nvarchar(n)` -> `nvarchar(m)` with `m > n` (variable length, not to max): metadata-only, no row rewrite.
- Decimal scale/precision growth staying within one storage byte class.
- `datetime2`/`time`/`datetimeoffset` scale growth staying within one storage byte class.
- The single `CREATE TABLE` on a brand-new (empty) table: there are no rows to rewrite, so it is `MetadataOnly` regardless of the column types; it is flagged `IsCreateTable` so a benign 2714 race is swallowed.

**How the planner annotates each change.** The classifier is a pure function added beside the planner; the planner stays pure and does no I/O. `ColumnAlter` gains a `Footprint`, and `EvolutionPlan` gains aggregates so the executor and the policy gate can read the classification without recomputing it. This fills the gap where the implemented `ColumnAlter` (`EvolutionPlan.cs:4-12`) carries only `Name`/`FromType`/`ToType`/`IsNullable` and no cost, so the genuine rewrites (`tinyint`->`bigint`, decimal class growth, fixed widening, to-`max`) currently fall through as plain alters with no signal.

```csharp
// src/SqlFlow.SqlServer/Schema/EvolutionPlan.cs (additions)
public sealed record ColumnAlter
{
    public required string Name { get; init; }
    public required SqlDataType FromType { get; init; }
    public required SqlDataType ToType { get; init; }
    public bool IsNullable { get; init; }

    /// <summary>The lock/data footprint of this ALTER, set by ChangeFootprintClassifier.</summary>
    public required ChangeFootprint Footprint { get; init; }
}

public sealed record EvolutionPlan
{
    // ...existing members unchanged...

    /// <summary>The alters that force a table rewrite; the subset of ColumnsToAlter with TableRewrite footprint.</summary>
    public IReadOnlyList<ColumnAlter> RewriteColumns =>
        ColumnsToAlter.Where(a => a.Footprint == ChangeFootprint.TableRewrite).ToList();

    public bool HasRewrite => RewriteColumns.Count > 0;
}
```

```csharp
// src/SqlFlow.SqlServer/Schema/ChangeFootprintClassifier.cs (new, pure)
namespace SqlFlow.SqlServer.Schema;

/// <summary>Classifies a single widening ALTER as MetadataOnly or TableRewrite from the structured
/// SqlDataType pair. Pure; no I/O. Encodes the SQL Server in-place ALTER COLUMN rewrite rules.</summary>
public static class ChangeFootprintClassifier
{
    public static ChangeFootprint Classify(SqlDataType from, SqlDataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return from.Family switch
        {
            SqlTypeFamily.Integer => IntRank(to.BaseType) > IntRank(from.BaseType)
                ? ChangeFootprint.TableRewrite : ChangeFootprint.MetadataOnly,
            SqlTypeFamily.Decimal => DecimalClass(to.Precision ?? 18) != DecimalClass(from.Precision ?? 18)
                ? ChangeFootprint.TableRewrite : ChangeFootprint.MetadataOnly,
            SqlTypeFamily.Text => TextRewrite(from, to),
            SqlTypeFamily.Binary => BinaryRewrite(from, to),
            SqlTypeFamily.DateTime => TimeClass(to.Scale ?? 7) != TimeClass(from.Scale ?? 7)
                ? ChangeFootprint.TableRewrite : ChangeFootprint.MetadataOnly,
            _ => ChangeFootprint.MetadataOnly,
        };
    }

    private static int IntRank(string t) => t switch
        { "tinyint" => 1, "smallint" => 2, "int" => 3, "bigint" => 4, _ => 0 };

    // 5/9/13/17-byte storage classes for decimal precision.
    private static int DecimalClass(int p) => p switch
        { <= 9 => 0, <= 19 => 1, <= 28 => 2, _ => 3 };

    // 3/4/5-byte storage classes for datetime2/time/datetimeoffset fractional scale.
    private static int TimeClass(int s) => s switch { <= 2 => 0, <= 4 => 1, _ => 2 };

    private static ChangeFootprint TextRewrite(SqlDataType from, SqlDataType to)
    {
        if (to.IsMax && !from.IsMax)
        {
            return ChangeFootprint.TableRewrite;            // (n) -> (max): in-row to LOB
        }

        var fromFixed = from.BaseType is "char" or "nchar";
        var toFixed = to.BaseType is "char" or "nchar";
        if (fromFixed && toFixed && (to.Length ?? 1) > (from.Length ?? 1))
        {
            return ChangeFootprint.TableRewrite;            // fixed widening rewrites every row
        }

        return ChangeFootprint.MetadataOnly;                // varchar(n)->varchar(m) etc.
    }

    private static ChangeFootprint BinaryRewrite(SqlDataType from, SqlDataType to)
    {
        if (to.IsMax && !from.IsMax)
        {
            return ChangeFootprint.TableRewrite;
        }

        if (from.BaseType == "binary" && to.BaseType == "binary" && (to.Length ?? 1) > (from.Length ?? 1))
        {
            return ChangeFootprint.TableRewrite;
        }

        return ChangeFootprint.MetadataOnly;
    }
}
```

The planner sets `Footprint = ChangeFootprintClassifier.Classify(existing.DataType, resolution.Merged!)` when it builds each `ColumnAlter` in `SchemaEvolutionPlanner.cs:47-53`. The planner remains pure.

**Policy for an expensive rewrite (never a silent long lock).** A `TableRewrite` is never applied inline by default. The decision is a pure gate:

```csharp
// src/SqlFlow.SqlServer/Schema/RewritePolicy.cs (new, pure)
public enum RewriteHandling { Inline, RequireOptIn }

public static class RewritePolicy
{
    /// <summary>A rewrite is allowed inline only when the flow has explicitly opted in
    /// (Schema.AllowTableRewrite). Without opt-in, the rewrite is refused so it never silently
    /// holds a long Sch-M lock; the operator runs it in a maintenance window or sets the flag.</summary>
    public static RewriteHandling Decide(bool allowTableRewrite)
        => allowTableRewrite ? RewriteHandling.Inline : RewriteHandling.RequireOptIn;
}
```

`AllowTableRewrite` (default `false`) is a new field on the schema-evolution spec (`Schema.Evolve` area of `FlowDefinition`). When a plan `HasRewrite` and the flow has not opted in, the apply path throws the typed `SchemaRewriteNotPermittedException` (a `SqlFlowException` subtype) listing the offending columns and their `From`/`To` types, before acquiring any lock or running any DDL. The metadata-only changes are not applied either, because applying a partial widen would let the load attempt to insert values that do not fit the un-widened rewrite column; the run fails cleanly and is retried after the operator opts in or schedules a maintenance window. When opted in, the rewrite runs inline as its own short transaction with `LOCK_TIMEOUT` politeness (3.12.2) and surrounding index rebuilds carrying `WAIT_AT_LOW_PRIORITY`/`ONLINE` where the edition allows. There is no third "silently run a long lock" branch.

### 3.12.5 Exact code and DDL changes, and where they plug in

**Contract (`ISchemaProvider`).** `ExecuteDdlAsync` is replaced by a classified-batch applier so cost survives to execution time.

```csharp
// src/SqlFlow.Core/Abstractions/ISchemaProvider.cs
public interface ISchemaProvider
{
    Task<TableSchema?> GetTableSchemaAsync(string connectionString, string schema, string table, CancellationToken ct = default);

    /// <summary>
    /// Applies a classified DDL batch to one target object under an object-scoped session app lock.
    /// Metadata-only statements commit in one short transaction; each table-rewrite statement commits in
    /// its own short transaction. The app lock and Sch-M lock are released before this returns, well before
    /// any bulk load. Throws <see cref="SchemaLockTimeoutException"/> when the app lock or a DDL Sch-M lock
    /// cannot be acquired within the configured timeout (no queue-blocking).
    /// </summary>
    Task ApplyDdlAsync(string connectionString, DdlBatch batch, SchemaApplyOptions options, CancellationToken ct = default);
}
```

**Model (`SqlFlow.Core/Model/DdlBatch.cs`, new).** The classified handoff visible to both Core and SqlServer.

```csharp
namespace SqlFlow.Core.Model;

public enum DdlCost { MetadataOnly, Rewrite }

public sealed record DdlStatement
{
    public required string Text { get; init; }
    public required DdlCost Cost { get; init; }

    /// <summary>The single CREATE TABLE; a benign 2714 (object exists) is swallowed on a create race.</summary>
    public bool IsCreateTable { get; init; }
}

public sealed record DdlBatch
{
    /// <summary>Raw (unbracketed) schema and table; the case-canonical lock key derives from these,
    /// the escaped bracketed name lives only in DdlStatement.Text.</summary>
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required IReadOnlyList<DdlStatement> Statements { get; init; }
    public bool HasChanges => Statements.Count > 0;
}

public sealed record SchemaApplyOptions
{
    public int AppLockTimeoutMs { get; init; } = 30_000;
    public int DdlLockTimeoutMs { get; init; } = 5_000;
    public int RewriteMaxDurationMinutes { get; init; } = 1;
}
```

**Typed exceptions (`SqlFlow.Core`, beside `SqlFlowException.cs`).**

```csharp
public sealed class SchemaLockTimeoutException : SqlFlowException
{
    public SchemaLockTimeoutException(string resource, int? returnCode, Exception? inner = null)
        : base($"Could not acquire the schema lock on '{resource}'"
               + (returnCode is { } c ? $" (sp_getapplock returned {c})." : " (DDL lock timeout, SQL error 1222)."), inner)
    { Resource = resource; ReturnCode = returnCode; }

    public string Resource { get; }
    public int? ReturnCode { get; }
}

public sealed class SchemaRewriteNotPermittedException : SqlFlowException
{
    public SchemaRewriteNotPermittedException(string target, IReadOnlyList<string> columns)
        : base($"Schema evolution on '{target}' requires a table rewrite on column(s) {string.Join(", ", columns)} "
               + "(for example int to bigint). This holds a table lock for the full rewrite and is refused by default. "
               + "Set Schema.AllowTableRewrite to run it, ideally in a maintenance window.")
        => Columns = columns;

    public IReadOnlyList<string> Columns { get; }
}
```

**DDL generator (`IDdlGenerator` / `SqlServerDdlGenerator`).** The generator is extended to (a) emit `ALTER COLUMN` for `ColumnsToAlter` (the implemented generator currently emits only CREATE and additive ADD, so the alter path does not yet produce SQL at all), (b) return `DdlStatement` carrying `DdlCost` mapped from `ColumnAlter.Footprint`, (c) wrap each statement in its idempotency guard, and (d) attach `WAIT_AT_LOW_PRIORITY`/`ONLINE` to the index rebuilds around a rewrite, not to the `ALTER COLUMN`.

```csharp
// src/SqlFlow.Core/Abstractions/IDdlGenerator.cs
DdlBatch Generate(TargetSpec target, EvolutionPlan plan, SqlServerEngineEdition edition);
```

Representative emitted T-SQL (target `[dbo].[Customer]`, escaped via the corrected `TargetSpec.QualifiedName`):

```sql
-- MetadataOnly: additive nullable add, idempotent
IF COL_LENGTH('[dbo].[Customer]', 'Notes') IS NULL
    ALTER TABLE [dbo].[Customer] ADD [Notes] nvarchar(400) NULL;

-- MetadataOnly: variable-length widen, idempotent against the merged type
ALTER TABLE [dbo].[Customer] ALTER COLUMN [Code] varchar(64) NULL;

-- TableRewrite: int -> bigint. No WAIT_AT_LOW_PRIORITY here (invalid on ALTER COLUMN);
-- politeness comes from SET LOCK_TIMEOUT on the session. Its own transaction.
ALTER TABLE [dbo].[Customer] ALTER COLUMN [Id] bigint NOT NULL;

-- Surrounding index rebuild (when a rewrite forces one): WAIT_AT_LOW_PRIORITY is valid HERE.
-- ONLINE = ON only emitted when SERVERPROPERTY('EngineEdition') supports it.
ALTER INDEX [IX_Customer_Id] ON [dbo].[Customer]
    REBUILD WITH (ONLINE = ON (WAIT_AT_LOW_PRIORITY (MAX_DURATION = 1 MINUTES, ABORT_AFTER_WAIT = SELF)));
```

**Provider (`SqlServerSchemaProvider.ApplyDdlAsync`, replaces `ExecuteDdlAsync:44-71`).**

```csharp
public async Task ApplyDdlAsync(string connectionString, DdlBatch batch, SchemaApplyOptions options, CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(batch);
    ArgumentNullException.ThrowIfNull(options);
    if (!batch.HasChanges) return;

    var resource = SchemaLockResource(batch.Schema, batch.Table);

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(ct).ConfigureAwait(false);

    // SET LOCK_TIMEOUT governs the Sch-M waits of the DDL itself: a blocked ALTER aborts with 1222
    // instead of queuing in front of readers.
    await SetSessionLockTimeoutAsync(connection, options.DdlLockTimeoutMs, ct).ConfigureAwait(false);

    // Object-scoped, case-canonical app lock: table A and table B get distinct resources, never contend.
    await AcquireAppLockOrThrowAsync(connection, resource, options.AppLockTimeoutMs, ct).ConfigureAwait(false);
    try
    {
        var metadata = batch.Statements.Where(s => s.Cost == DdlCost.MetadataOnly).ToList();
        if (metadata.Count > 0)
        {
            await using var tx = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            try
            {
                foreach (var s in metadata)
                    await ExecuteStatementAsync(connection, tx, s, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        }

        foreach (var s in batch.Statements.Where(x => x.Cost == DdlCost.Rewrite))
        {
            await using var tx = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            try
            {
                await ExecuteStatementAsync(connection, tx, s, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        }
    }
    finally
    {
        await ReleaseAppLockAsync(connection, resource).ConfigureAwait(false);
    }
}
```

`ExecuteStatementAsync` uses `CommandTimeout = 0`, swallows 2714 only when `IsCreateTable`, and maps `1222`/`1204`/`1205` to `SchemaLockTimeoutException`. Rollback uses `CancellationToken.None` so a cancelled run still rolls back cleanly rather than double-faulting on a cancelled rollback (corrected from the prior draft, which passed `ct` to rollback and could throw `OperationCanceledException` out of the `catch`, masking the real failure). `AcquireAppLockOrThrowAsync` calls `sys.sp_getapplock` with the return-value parameter and throws on `code < 0`. `ReleaseAppLockAsync` calls `sys.sp_releaseapplock` with `CancellationToken.None`, guards on `connection.State == Open`, and swallows a release `SqlException` (the connection close that follows frees the session lock anyway, and a release failure must not mask the original DDL exception).

**FlowRunner wiring.**

- `FlowRunner.cs:104` builds a `DdlBatch` from the classified plan and calls `_schema.ApplyDdlAsync(connectionString, plan.DdlBatch, applyOptions, ct)`. Before that, if `plan.EvolutionPlan.HasRewrite && !flow.Schema.AllowTableRewrite`, it throws `SchemaRewriteNotPermittedException`; the existing `catch (Exception ex) when (ex is not OperationCanceledException)` at `:212` turns both that and `SchemaLockTimeoutException` into a `Failed` `FlowResult` and best-effort rebuilds any disabled indexes (`:216-226`).
- `FlowRunner.cs:109` (PreProcess) and `:163` (PostProcess) call the new `IScriptExecutor.ExecuteAsync(connectionString, scripts, ct)` instead of `_schema.ExecuteDdlAsync`. This change lands in the same commit as the interface change so the build never has a dangling script path.

**`IScriptExecutor` (new, `SqlFlow.Core/Abstractions` + `SqlFlow.SqlServer`).** Runs user PreProcess/PostProcess on its own `SqlConnection`, each script its own `SqlCommand`, with no app lock and no `LOCK_TIMEOUT` override. Whether it wraps the scripts in one transaction is its own concern, independent of the schema lock; it does not borrow schema semantics.

**Staging.** Staging create flows through the same `ApplyDdlAsync`, with `DdlBatch.Schema = "raw"` and `DdlBatch.Table = <targetSchema>_<targetTable>_<flowId>` (the flow's canonical staging name), so the lock resource is the staging name, distinct from the target. Distinct flows never share a staging name, and same-flow runs are serialized by the run queue's pipeline gate. The drop-on-success / keep-on-failure lifecycle (section 3.11) is unchanged; only staging create uses the object-keyed lock path.

### 3.12.6 Failure and edge handling

| Situation | Behavior |
| --- | --- |
| App-lock acquisition times out (another schema run holds the same object) | `sp_getapplock` returns `-1`; `SchemaLockTimeoutException` (`ReturnCode = -1`) before any DDL runs. FlowRunner reports `Failed`; the flow is safely retryable. |
| DDL statement blocked on a Sch-M lock (table busy with a long query) | After `LOCK_TIMEOUT` (5000 ms) the statement aborts with SQL error 1222; mapped to `SchemaLockTimeoutException` (`ReturnCode = null`). The pending Sch-M request never queues in front of and blocks active readers. Retryable. |
| Rewrite required but `AllowTableRewrite` is false | `SchemaRewriteNotPermittedException` listing the offending columns and their `From`/`To` types, thrown before any lock or DDL. No partial widen is applied. Operator opts in or schedules a maintenance window. |
| Rewrite allowed: the rewrite itself blocked on Sch-M | Same 1222 fail-fast as above (politeness on `ALTER COLUMN` is `LOCK_TIMEOUT`, not `WAIT_AT_LOW_PRIORITY`). Already-committed metadata-only changes are preserved because the rewrite is a separate transaction; a retry re-applies only the rewrite (idempotency guards skip the additive work). |
| Surrounding index rebuild cannot get its lock within `MAX_DURATION` | `ABORT_AFTER_WAIT = SELF` aborts the rebuild (not user queries); surfaces as a DDL failure for that statement and the run reports `Failed`; FlowRunner's best-effort index rebuild restores any disabled non-clustered indexes. |
| Two runs of the same flow concurrently | Distinct staging names so staging never collides; the target app lock serializes the DDL instant; the second run's metadata-only DDL is a no-op via idempotency guards and its load tolerates the first run's additive ADD. |
| Concurrent same-target widening on the SAME column by two flows | Only the DDL instant is serialized by the app lock; the two ALTERs run one after the other, and monotonic widening makes them order-independent (both converge on the wider type, the second is a no-op). |
| Connection drop mid-DDL | The session-scoped app lock auto-frees on disconnect; any open transaction rolls back; nothing is left locked. The next run reacquires cleanly. |
| `CREATE TABLE` create race (two runs create the same new table) | The losing statement raises 2714, swallowed only for the `IsCreateTable` statement; the run proceeds against the now-existing table. |
| Cancellation (`ct`) during DDL | Honored on every `await` that takes `ct`; rollback and `sp_releaseapplock` run with `CancellationToken.None` so cleanup completes and the lock is released immediately rather than lingering until connection close. |

### 3.12.7 What changed versus 3.10 and why

- **Isolation:** READ COMMITTED, not SERIALIZABLE. The app lock already serializes same-object DDL; SERIALIZABLE only adds catalog range locks (a broader footprint).
- **App-lock honesty:** the app lock serializes the DDL *instant* for the same object, not the whole run. Same-object safety across the load rests on idempotent guards plus additive-add tolerance, not on a lock the applier cannot hold across the loader's and index manager's separate connections.
- **Lock-timeout semantics corrected:** `SET LOCK_TIMEOUT` *does* govern the DDL's Sch-M waits; that is precisely why it makes a blocked ALTER fail fast at 1222. `sp_getapplock @LockTimeout` is the complementary app-lock acquisition timeout.
- **`WAIT_AT_LOW_PRIORITY` corrected:** invalid on `ALTER COLUMN`; emitted only on `ALTER INDEX ... REBUILD`. Politeness for the rewrite `ALTER COLUMN` comes from `LOCK_TIMEOUT`.
- **Lock key escaping and casing:** `TargetSpec.QualifiedName` is now `]`-escaped to match `RelationalObject`/`SqlServerIndexManager`; the lock resource is a separate case-canonical (invariant-lowercase) key so case-variant references to one object on a case-insensitive instance serialize correctly, and a `]` in an identifier cannot corrupt the lock string.
- **Rewrite classification owned:** `ChangeFootprint` on `ColumnAlter`, computed by the pure `ChangeFootprintClassifier`, with `RewritePolicy` gating an opt-in. Without this, the genuine rewrites the planner already produces (`tinyint`->`bigint`, decimal class growth, fixed widening, to-`max`) would silently batch as metadata-only and hold a long Sch-M lock, reintroducing the exact blocking this model forbids.
- **Transaction granularity:** metadata-only changes batch in one short transaction; each rewrite is isolated, so a rewrite failure cannot undo additive work and a long rewrite Sch-M lock is held alone, briefly, and politely.
- **PreProcess/PostProcess:** moved off the schema path to `IScriptExecutor`, landing in the same commit as the interface change so the build is never broken.
- **Rollback double-fault fixed:** transaction rollback and lock release use `CancellationToken.None`, so a cancelled run cleans up instead of throwing a second exception that masks the first.
```

---

Design complete. The Markdown section above is ready to append to `C:\Projects\SQLFlowV3\docs\schema-sync-and-discovery-design.md` as section 3.12. Every critic finding is folded in and grounded against the live code I verified:

- **App-lock scope (high):** corrected to "DDL instant only" with the honest reason (`SqlBulkLoader.cs:17`, `SqlServerIndexManager.cs:30/64`, `SqlBulkLoader.TruncateAsync:43` each open separate `SqlConnection`s, so a session-scoped lock dies before the load). Same-object cross-load safety rebased on idempotency guards plus additive-add tolerance.
- **`WAIT_AT_LOW_PRIORITY` (high):** removed from `ALTER COLUMN` (invalid grammar); restricted to `ALTER INDEX ... REBUILD`; rewrite politeness re-derived from `SET LOCK_TIMEOUT`.
- **`LOCK_TIMEOUT` scope (high):** corrected to state it *does* govern Sch-M waits; described as complementary to `sp_getapplock @LockTimeout`.
- **`QualifiedName` escaping/casing (high):** `TargetSpec.QualifiedName` (FlowDefinition.cs:91, unescaped) fixed to escape like `RelationalObject.cs:18`; the lock resource made a separate case-canonical key so it is escaping-proof and collation-correct.
- **Rewrite cost bit (medium):** owned via `ChangeFootprint` on `ColumnAlter` + pure `ChangeFootprintClassifier`, mapped from the actual `SqlTypeResolution` widenings, plus `RewritePolicy` opt-in gate and `SchemaRewriteNotPermittedException`.
- **PreProcess/PostProcess (medium):** moved to `IScriptExecutor`, sequenced in the same commit; FlowRunner call sites (`:104/:109/:163`) specified.
- **Catalog contention (medium):** acknowledged for schema-evolution-vs-evolution on the same schema, scoped out of the data-pipeline guarantee.
- **Deadlock-victim rollback double-fault:** rollback and release switched to `CancellationToken.None`.

No em dash used. No time estimates.

Relevant files this design plugs into (all absolute):
- `C:\Projects\SQLFlowV3\src\SqlFlow.SqlServer\SqlServerSchemaProvider.cs` (replace `ExecuteDdlAsync` at :44-71 with `ApplyDdlAsync`)
- `C:\Projects\SQLFlowV3\src\SqlFlow.Core\Abstractions\ISchemaProvider.cs` (contract)
- `C:\Projects\SQLFlowV3\src\SqlFlow.Core\Model\FlowDefinition.cs` (`TargetSpec.QualifiedName` escaping, :91; `Schema.AllowTableRewrite`)
- `C:\Projects\SQLFlowV3\src\SqlFlow.SqlServer\Schema\EvolutionPlan.cs` (`ChangeFootprint`, `ColumnAlter.Footprint`, `RewriteColumns`/`HasRewrite`)
- `C:\Projects\SQLFlowV3\src\SqlFlow.SqlServer\Schema\SchemaEvolutionPlanner.cs` (:47-53 set `Footprint`)
- `C:\Projects\SQLFlowV3\src\SqlFlow.SqlServer\Schema\ChangeFootprintClassifier.cs` (new), `RewritePolicy.cs` (new)
- `C:\Projects\SQLFlowV3\src\SqlFlow.Core\Model\DdlBatch.cs` (new), `SchemaLockTimeoutException.cs` / `SchemaRewriteNotPermittedException.cs` (new, beside `SqlFlowException.cs`)
- `C:\Projects\SQLFlowV3\src\SqlFlow.Core\Abstractions\IDdlGenerator.cs` + `C:\Projects\SQLFlowV3\src\SqlFlow.SqlServer\SqlServerDdlGenerator.cs` (emit `ALTER COLUMN` + `DdlCost` + index-only `WAIT_AT_LOW_PRIORITY`/`ONLINE`)
- `C:\Projects\SQLFlowV3\src\SqlFlow.Core\Engine\FlowRunner.cs` (:104 -> `ApplyDdlAsync`; :109/:163 -> `IScriptExecutor`)
- `C:\Projects\SQLFlowV3\src\SqlFlow.Core\Abstractions\IScriptExecutor.cs` (new) + SqlServer impl
