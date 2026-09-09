import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { KeyRound, MonitorPlay, Network } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { LineageObjectColumn, ObjectRelationship, ObjectSubscriber } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { ConnectionRef } from "../../components/ConnectionRef";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { EmptyState } from "../../components/EmptyState";
import { LineageJumpButton } from "../../components/LineageJumpButton";
import { Mono } from "../../components/Mono";
import { RelativeTime } from "../../components/RelativeTime";
import { encodeNodeId } from "./nodeIds";

/** The set of key-column names (lowercase) an object's interpreted key names, for the PK marker. */
function keyColumnSet(keyColumns: string | null): Set<string> {
  return new Set(
    (keyColumns ?? "")
      .split(",")
      .map((name) => name.trim().toLowerCase())
      .filter((name) => name.length > 0),
  );
}

function columnTableColumns(keys: Set<string>): Column<LineageObjectColumn>[] {
  return [
    {
      id: "ordinal",
      header: "#",
      align: "right",
      render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.ordinal}</span>,
      width: 56,
    },
    {
      id: "name",
      header: "Name",
      render: (row) => (
        <span className="flex items-center gap-1.5">
          {keys.has(row.name.toLowerCase()) && (
            <Tooltip>
              <TooltipTrigger asChild>
                <KeyRound className="size-4 shrink-0 text-warning" />
              </TooltipTrigger>
              <TooltipContent>Interpreted key column</TooltipContent>
            </Tooltip>
          )}
          <Mono>{row.name}</Mono>
        </span>
      ),
    },
    { id: "dataType", header: "Data type", render: (row) => (row.dataType === null ? "-" : <Mono>{row.dataType}</Mono>) },
    {
      id: "nullable",
      header: "Nullable",
      render: (row) => (
        <Badge variant={row.nullable ? "outline" : "secondary"} className="text-[11px]">
          {row.nullable ? "null" : "not null"}
        </Badge>
      ),
    },
    { id: "tier", header: "Tier", render: (row) => <Badge variant="outline" className="text-[11px]">{row.tier}</Badge> },
  ];
}

/** One direction of the data-model table: the other table, the join condition, and how it was interpreted. */
function relationshipColumns(): Column<ObjectRelationship>[] {
  return [
    {
      id: "other",
      header: "Table",
      render: (row) => (
        <Link
          to={`/catalog?node=${encodeURIComponent(encodeNodeId({ type: "object", objectKey: row.otherObjectKey }))}`}
          className="font-mono text-[12px] text-primary hover:underline"
        >
          {[row.otherDatabase, row.otherSchema, row.otherName].filter((part) => part !== null).join(".")}
        </Link>
      ),
    },
    {
      id: "join",
      header: "Join on",
      render: (row) => (
        <span className="whitespace-normal break-words font-mono text-[12px]">
          {`${row.ownColumns} = ${row.otherColumns}`}
        </span>
      ),
    },
    {
      id: "origin",
      header: "Interpreted from",
      render: (row) => (
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="inline-flex">
              <Badge variant={row.origin === "Constraint" ? "default" : "outline"} className="text-[11px]">
                {row.origin === "Constraint" ? "constraint" : "joins in code"}
              </Badge>
            </span>
          </TooltipTrigger>
          <TooltipContent>
            {row.name ?? (row.origin === "Constraint" ? "Constraint clause in the codebase" : "Join predicates in the codebase's SQL")}
          </TooltipContent>
        </Tooltip>
      ),
      width: 140,
    },
    {
      id: "occurrences",
      header: "Seen in",
      render: (row) => `${row.occurrences} script${row.occurrences === 1 ? "" : "s"}`,
      width: 110,
    },
    { id: "tier", header: "Tier", render: (row) => <Badge variant="outline" className="text-[11px]">{row.tier}</Badge>, width: 100 },
  ];
}

/** The consumers table: who reads this object, what they are, who owns them, and through which queries. This
 * is the impact-analysis view, so the owner column is deliberately as prominent as the name. */
function consumerColumns(): Column<ObjectSubscriber>[] {
  return [
    {
      id: "name",
      header: "Subscriber",
      render: (row) => (
        <span className="flex min-w-0 items-center gap-1.5">
          <MonitorPlay className="size-4 shrink-0 text-muted-foreground" />
          <Link
            to={`/subscribers?key=${encodeURIComponent(row.key)}`}
            className="truncate text-[13px] text-primary hover:underline"
          >
            {row.name}
          </Link>
        </span>
      ),
    },
    {
      id: "type",
      header: "Type",
      render: (row) => <Badge variant="secondary" className="text-[11px]">{row.type}</Badge>,
      width: 120,
    },
    {
      id: "owner",
      header: "Owner",
      render: (row) => (row.owner === null ? <span className="text-muted-foreground">-</span> : <Mono>{row.owner}</Mono>),
    },
    {
      id: "queries",
      header: "Through",
      render: (row) => (
        <span className="whitespace-normal break-words font-mono text-[12px] text-muted-foreground">
          {row.queries.length === 0 ? "-" : row.queries.join(", ")}
        </span>
      ),
    },
  ];
}

/** A flow reference: its name (linking to the pipeline view) and kind badge. */
function FlowRef({ pipelineId, flow, kind }: { pipelineId: string; flow: string; kind: string }) {
  return (
    <span className="flex min-w-0 items-center gap-1.5">
      <Network className="size-4 shrink-0 text-muted-foreground" />
      <Link to={`/pipelines/${pipelineId}`} className="truncate font-mono text-[12px] text-primary hover:underline">
        {flow}
      </Link>
      <Badge variant="outline" className="h-[18px] shrink-0 px-1.5 text-[11px] text-muted-foreground">{kind}</Badge>
    </span>
  );
}

/** A file source's provenance: the pipelines that produce it (where it comes from) and the pipelines that
 * consume it, each with the tables the data lands in (where it goes). This is the "where do we source this,
 * through which pipeline, to where" answer for a file. */
function FileProvenance({ objectKey }: { objectKey: string }) {
  const flows = useQuery({
    queryKey: ["catalog-file-flows", objectKey],
    queryFn: () => lineageApi.fileFlows(objectKey),
  });

  if (flows.isPending) {
    return <Skeleton className="h-48 w-full" />;
  }
  if (flows.isError) {
    return isApiError(flows.error)
      ? <CorrelationError error={flows.error} />
      : <p className="text-[13px] text-destructive">{String(flows.error)}</p>;
  }

  const { producers, consumers } = flows.data;
  if (producers.length === 0 && consumers.length === 0) {
    return <EmptyState title="No pipelines reference this file yet." />;
  }

  return (
    <div className="flex flex-col gap-5" data-testid="catalog-file-provenance">
      <section>
        <h3 className="mb-2 text-sm font-medium">{`Produced by (${producers.length})`}</h3>
        {producers.length === 0 ? (
          <p className="text-[13px] text-muted-foreground">
            Nothing in the catalog produces this file: it is an external source landing here.
          </p>
        ) : (
          <div className="flex flex-col gap-1.5">
            {producers.map((p) => <FlowRef key={p.pipelineId} pipelineId={p.pipelineId} flow={p.flow} kind={p.kind} />)}
          </div>
        )}
      </section>
      <section>
        <h3 className="mb-2 text-sm font-medium">{`Consumed by (${consumers.length})`}</h3>
        {consumers.length === 0 ? (
          <p className="text-[13px] text-muted-foreground">No pipeline reads this file.</p>
        ) : (
          <div className="flex flex-col gap-3">
            {consumers.map((c) => (
              <div key={c.pipelineId}>
                <FlowRef pipelineId={c.pipelineId} flow={c.flow} kind={c.kind} />
                <div className="mt-0.5 flex flex-wrap items-baseline gap-x-1.5 gap-y-0.5 pl-6">
                  <span className="text-xs text-muted-foreground">Lands in:</span>
                  {c.lands.length === 0 ? (
                    <span className="text-xs text-muted-foreground">(no table target recorded)</span>
                  ) : (
                    c.lands.map((l, index) => (
                      <span key={l.key} className="text-xs">
                        <Link
                          to={`/catalog?node=${encodeURIComponent(encodeNodeId({ type: "object", objectKey: l.key }))}`}
                          className="font-mono text-primary hover:underline"
                        >
                          {[l.database, l.schema, l.name].filter((part) => part !== null).join(".")}
                        </Link>
                        {index < c.lands.length - 1 && <span className="text-muted-foreground">,</span>}
                      </span>
                    ))
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

/**
 * The catalog tree's details panel for a database object: the dossier across Overview / Columns / Code /
 * Relationships tabs. Relationships are the interpreted DATA MODEL (how this table joins others, read from
 * the codebase's own SQL: constraint clauses and the join predicates in views, procedures, and flow hooks),
 * not the flow lineage; tracing which flows move the data is one click away via the lineage jump.
 */
export function ObjectDetailsPanel({ objectKey }: { objectKey: string }) {
  const [tab, setTab] = useState<"overview" | "columns" | "code" | "relationships" | "pipelines" | "consumers">("overview");
  // Reset to Overview when the selected object changes, so a tab valid only for a file (or only for a table)
  // never lingers onto the next selection.
  useEffect(() => setTab("overview"), [objectKey]);

  const dossier = useQuery({
    queryKey: ["catalog-object-dossier", objectKey],
    queryFn: () => lineageApi.dossier(objectKey),
  });
  const script = useQuery({
    queryKey: ["catalog-object-script", objectKey],
    queryFn: () => lineageApi.script(objectKey),
    enabled: tab === "code",
  });

  if (dossier.isPending) {
    return (
      <div className="flex flex-col gap-2">
        <Skeleton className="h-8 w-72 max-w-full" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-2/3" />
        <Skeleton className="mt-2 h-64 w-full" />
      </div>
    );
  }
  if (dossier.isError) {
    return isApiError(dossier.error)
      ? <CorrelationError error={dossier.error} />
      : <p className="text-[13px] text-destructive">{String(dossier.error)}</p>;
  }

  const { object, columns, references, referencedBy, subscribers } = dossier.data;
  const keys = keyColumnSet(object.keyColumns);
  const relationshipCount = references.length + referencedBy.length;
  // A file source's useful detail is its provenance (pipelines + landing), not columns/keys/joins, which it
  // has none of. A database object gets the semantic/query tabs instead.
  const isFile = object.kind === "File";

  return (
    <div data-testid="catalog-object-details">
      <div className="mb-1 flex flex-wrap items-center gap-2">
        <h2 className="min-w-0 break-words font-mono text-base font-medium">{object.name}</h2>
        <Badge variant="secondary">{object.kind}</Badge>
        <div className="grow" />
        <LineageJumpButton
          target={{
            kind: "object",
            objectKey: object.key,
            objectKind: object.kind,
            label: object.name,
            sublabel: [object.database, object.schema].filter((part) => part !== null).join("."),
          }}
          variant="outlined"
        />
      </div>
      <p className="mb-4 break-all font-mono text-xs text-muted-foreground">{object.key}</p>

      <Tabs value={tab} onValueChange={(value) => setTab(value as typeof tab)} className="gap-4">
        <TabsList variant="line">
          <TabsTrigger value="overview" data-testid="catalog-tab-overview">Overview</TabsTrigger>
          {isFile
            ? <TabsTrigger value="pipelines" data-testid="catalog-tab-pipelines">Pipelines</TabsTrigger>
            : (
              <>
                <TabsTrigger value="columns" data-testid="catalog-tab-columns">{`Columns (${columns.length})`}</TabsTrigger>
                <TabsTrigger value="code" data-testid="catalog-tab-code">Code</TabsTrigger>
                <TabsTrigger value="relationships" data-testid="catalog-tab-relationships">
                  {`Relationships (${relationshipCount})`}
                </TabsTrigger>
              </>
            )}
          {subscribers.length > 0 && (
            <TabsTrigger value="consumers" data-testid="catalog-tab-consumers">
              {`Consumers (${subscribers.length})`}
            </TabsTrigger>
          )}
        </TabsList>

        <TabsContent value="overview">
          <div className="grid grid-cols-2 gap-3">
            <DetailPair label="Server"><ConnectionRef value={object.serverRef} copyTestId="copy-object-server" /></DetailPair>
            <DetailPair label="Database">{object.database === null ? "-" : <Mono>{object.database}</Mono>}</DetailPair>
            <DetailPair label="Schema">{object.schema === null ? "-" : <Mono>{object.schema}</Mono>}</DetailPair>
            <DetailPair label="Level">{object.level ?? "-"}</DetailPair>
            <DetailPair label="Key">
              {object.keyColumns === null ? "-" : (
                <span className="flex flex-wrap items-center gap-1.5">
                  <KeyRound className="size-4 shrink-0 text-warning" />
                  <Mono>{object.keyColumns}</Mono>
                  <Tooltip>
                    <TooltipTrigger asChild>
                      <span className="inline-flex">
                        <Badge variant="outline" className="text-[11px]">{object.keyOrigin?.toLowerCase()}</Badge>
                      </span>
                    </TooltipTrigger>
                    <TooltipContent>
                      {object.keyOrigin === "Constraint"
                        ? "From a PRIMARY KEY clause in the codebase"
                        : object.keyOrigin === "Declared"
                          ? "Declared by the loading flow's YAML key columns"
                          : "From the ON clause of the MERGE that loads it"}
                    </TooltipContent>
                  </Tooltip>
                </span>
              )}
            </DetailPair>
            <DetailPair label="First seen"><RelativeTime value={object.firstSeenUtc} absolute /></DetailPair>
            <DetailPair label="Last seen"><RelativeTime value={object.lastSeenUtc} absolute /></DetailPair>
          </div>
        </TabsContent>

        <TabsContent value="pipelines">
          <FileProvenance objectKey={object.key} />
        </TabsContent>

        <TabsContent value="columns">
          <DataTable<LineageObjectColumn>
            columns={columnTableColumns(keys)}
            rows={columns}
            rowKey={(row) => row.ordinal}
            emptyMessage="No columns recorded for this object (a connected sync fills the column dictionary)."
            data-testid="catalog-object-columns"
          />
        </TabsContent>

        <TabsContent value="code">
          {script.isPending ? (
            <Skeleton className="h-80 w-full" />
          ) : script.isError ? (
            isApiError(script.error)
              ? <CorrelationError error={script.error} />
              : <p className="text-[13px] text-destructive">{String(script.error)}</p>
          ) : script.data.script === null ? (
            <EmptyState title="No code captured for this object yet (no lineage tier saw it created)." />
          ) : (
            <CodeView
              value={script.data.script}
              language={script.data.language === "yaml" ? "yaml" : "sql"}
              height={420}
              data-testid="catalog-object-code"
            />
          )}
        </TabsContent>

        <TabsContent value="relationships">
          {relationshipCount === 0 ? (
            <EmptyState title="No data-model relationships interpreted yet: nothing in the codebase joins this object to another table." />
          ) : (
            <div className="flex flex-col gap-4">
              <section>
                <h3 className="mb-2 text-sm font-medium">{`References (${references.length})`}</h3>
                <DataTable<ObjectRelationship>
                  columns={relationshipColumns()}
                  rows={references}
                  rowKey={(row) => `${row.otherObjectKey}|${row.ownColumns}|${row.origin}`}
                  emptyMessage="This object references no other table."
                  data-testid="catalog-object-references"
                />
              </section>
              <section>
                <h3 className="mb-2 text-sm font-medium">{`Referenced by (${referencedBy.length})`}</h3>
                <DataTable<ObjectRelationship>
                  columns={relationshipColumns()}
                  rows={referencedBy}
                  rowKey={(row) => `${row.otherObjectKey}|${row.ownColumns}|${row.origin}`}
                  emptyMessage="No other table references this object."
                  data-testid="catalog-object-referenced-by"
                />
              </section>
            </div>
          )}
        </TabsContent>

        <TabsContent value="consumers">
          <p className="mb-3 text-[13px] text-muted-foreground">
            Declared consumers whose queries read this object. This is who to tell before a breaking change.
          </p>
          <DataTable<ObjectSubscriber>
            columns={consumerColumns()}
            rows={subscribers}
            rowKey={(row) => row.key}
            emptyMessage="No subscriber reads this object."
            data-testid="catalog-object-consumers"
          />
        </TabsContent>
      </Tabs>
    </div>
  );
}
