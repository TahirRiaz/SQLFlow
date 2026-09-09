import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Search } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { searchApi } from "../../api/endpoints";
import { isApiError } from "../../api/client";
import type {
  ColumnHit, DefinitionHit, FileHit, FlowColumnHit, FlowHit, ObjectHit, SearchCategory, StatementHit,
  SubscriberHit,
} from "../../api/types";
import { LineageJumpButton, type LineageJumpTarget } from "../../components/LineageJumpButton";
import { ConnectionRef } from "../../components/ConnectionRef";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { TruncatedText } from "../../components/TruncatedText";
import { formatBytes, parseUtc } from "../../lib/time";

/**
 * The trailing "Lineage" action column: a labeled button (not a bare icon) that opens the lineage jump picker for
 * the row. `target` returns null for rows that cannot be traced (a processed file whose run carries no repo), which
 * simply renders no action.
 */
function lineageColumn<T>(target: (row: T) => LineageJumpTarget | null): Column<T> {
  return {
    id: "lineage-graph",
    header: "Lineage",
    align: "right",
    width: 132,
    render: (row) => {
      const jump = target(row);
      return jump === null ? null : <LineageJumpButton target={jump} variant="outlined" />;
    },
  };
}

// One lineage target per hit kind. Objects/columns/code focus the object node (repo resolved on open); flows and
// files focus the flow node in the flow's own repo (a file focuses the flow that ingested it).
const objectTarget = (row: ObjectHit): LineageJumpTarget => ({
  kind: "object", objectKey: row.key, objectKind: row.kind, label: row.name,
  sublabel: [row.kind, row.schema, row.serverRef].filter(Boolean).join(" · "),
});
const columnTarget = (row: ColumnHit): LineageJumpTarget => ({
  kind: "object", objectKey: row.objectKey, objectKind: "", label: row.objectName,
  sublabel: `column: ${row.columnName}`,
});
const definitionTarget = (row: DefinitionHit): LineageJumpTarget => ({
  kind: "object", objectKey: row.key, objectKind: row.kind, label: row.name, sublabel: row.kind,
});
const flowTarget = (row: FlowHit): LineageJumpTarget => ({
  kind: "node", repoId: row.repoId, repoName: row.repoName, focusId: row.id, label: row.name,
  sublabel: `${row.kind} flow · ${row.repoName}`,
});
const flowColumnTarget = (row: FlowColumnHit): LineageJumpTarget => ({
  kind: "node", repoId: row.repoId, repoName: row.repoName, focusId: row.pipelineId, label: row.flowName,
  sublabel: `column: ${row.columnName}`,
});
// A file focuses the flow that ingests it, resolved by matching the file against the flow source specs in the
// catalog (works from the file's full path when known, else its name).
const fileTarget = (row: FileHit): LineageJumpTarget => ({
  kind: "file", filePath: row.path ?? row.name, label: row.name,
  sublabel: row.flowName ? `ingested by ${row.flowName}` : undefined,
});

// Tab order; index 0 (All) is the landing view for every search.
const TABS = ["All", "Objects", "Columns", "Definitions", "Files", "Flows", "Flow columns", "Executed SQL", "Subscribers"] as const;
type TabIndex = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8;

/** An object/column/flow/file name cell: data wears mono (DESIGN.md section 4), the hit name emphasized. */
function NameCell({ children }: { children: ReactNode }) {
  return <span className="font-mono text-[12px] font-medium">{children}</span>;
}

const objectColumns: Column<ObjectHit>[] = [
  { id: "name", header: "Name", render: (row) => <NameCell>{row.name}</NameCell> },
  { id: "kind", header: "Kind", render: (row) => <Badge variant="secondary">{row.kind}</Badge> },
  { id: "schema", header: "Schema", render: (row) => <Mono>{row.schema ?? "-"}</Mono> },
  { id: "database", header: "Database", render: (row) => <Mono>{row.database ?? "-"}</Mono> },
  { id: "serverRef", header: "Server", render: (row) => <ConnectionRef value={row.serverRef} /> },
];

const columnColumns: Column<ColumnHit>[] = [
  { id: "columnName", header: "Column", render: (row) => <NameCell>{row.columnName}</NameCell> },
  { id: "dataType", header: "Data type", render: (row) => <Mono>{row.dataType ?? "-"}</Mono> },
  {
    id: "nullable",
    header: "Nullable",
    render: (row) => (
      <Badge variant={row.nullable ? "outline" : "secondary"}>{row.nullable ? "null" : "not null"}</Badge>
    ),
  },
  { id: "objectName", header: "Object", render: (row) => <Mono>{row.objectName}</Mono> },
  {
    id: "objectKey",
    header: "Object key",
    render: (row) => <TruncatedText text={row.objectKey} mono maxWidth={420} />,
  },
];

const definitionColumns: Column<DefinitionHit>[] = [
  { id: "name", header: "Name", render: (row) => <NameCell>{row.name}</NameCell> },
  { id: "kind", header: "Kind", render: (row) => <Badge variant="secondary">{row.kind}</Badge> },
  { id: "source", header: "Matched", render: (row) => <Badge variant="outline">{row.source}</Badge> },
  {
    id: "snippet",
    header: "Snippet",
    render: (row) => <Mono className="whitespace-pre-wrap">{row.snippet}</Mono>,
  },
];

const fileColumns: Column<FileHit>[] = [
  { id: "name", header: "File", render: (row) => <NameCell>{row.name}</NameCell> },
  {
    id: "flowName",
    header: "Flow",
    // Direct file -> pipeline navigation: the flow name links to the pipeline that ingested the file.
    render: (row) => (
      <Link
        to={`/pipelines/${row.pipelineId}`}
        onClick={(event) => event.stopPropagation()}
        className="text-primary hover:underline"
      >
        {row.flowName}
      </Link>
    ),
  },
  {
    id: "rows",
    header: "Rows",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.rows.toLocaleString()}</span>,
  },
  {
    id: "columns",
    header: "Columns",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.columns.toLocaleString()}</span>,
  },
  {
    id: "sizeBytes",
    header: "Size",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{formatBytes(row.sizeBytes)}</span>,
  },
  {
    id: "runUtc",
    header: "Run",
    render: (row) => <Mono>{row.runUtc ? parseUtc(row.runUtc).toLocaleString() : "-"}</Mono>,
  },
  {
    id: "path",
    header: "Path",
    render: (row) => <TruncatedText text={row.path} mono maxWidth={420} />,
  },
];

const flowColumns: Column<FlowHit>[] = [
  { id: "name", header: "Flow", render: (row) => <NameCell>{row.name}</NameCell> },
  { id: "kind", header: "Kind", render: (row) => <Badge variant="secondary">{row.kind}</Badge> },
  { id: "matchedIn", header: "Matched", render: (row) => <Badge variant="outline">{row.matchedIn}</Badge> },
  {
    id: "snippet",
    header: "Path / snippet",
    render: (row) => <Mono className="whitespace-pre-wrap">{row.snippet}</Mono>,
  },
];

const flowColumnColumns: Column<FlowColumnHit>[] = [
  { id: "columnName", header: "Column", render: (row) => <NameCell>{row.columnName}</NameCell> },
  { id: "dataType", header: "Data type", render: (row) => <Mono>{row.dataType ?? "-"}</Mono> },
  { id: "flowName", header: "Flow", render: (row) => <Mono>{row.flowName}</Mono> },
  { id: "kind", header: "Set", render: (row) => <Badge variant="secondary">{row.kind}</Badge> },
  { id: "matchedIn", header: "Matched", render: (row) => <Badge variant="outline">{row.matchedIn}</Badge> },
  {
    id: "expression",
    header: "Source / expression",
    render: (row) => <TruncatedText text={row.expression ?? row.sourceColumn} mono maxWidth={420} />,
  },
];

const statementColumns: Column<StatementHit>[] = [
  { id: "flowName", header: "Flow", render: (row) => <NameCell>{row.flowName}</NameCell> },
  { id: "step", header: "Step", render: (row) => <Badge variant="secondary">{row.step}</Badge> },
  {
    id: "occurrences",
    header: "Times run",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.occurrences.toLocaleString()}</span>,
  },
  {
    id: "lastSeenUtc",
    header: "Last seen",
    render: (row) => <Mono>{row.lastSeenUtc ? parseUtc(row.lastSeenUtc).toLocaleString() : "-"}</Mono>,
  },
  {
    id: "snippet",
    header: "SQL",
    render: (row) => <Mono className="whitespace-pre-wrap">{row.snippet}</Mono>,
  },
];

/** A note's first line, for the one-line summaries. Notes are multi-line by design (a remark, then often an
 *  "Incomplete dataset" list), and the first line is the part that says what is wrong. */
function firstLine(text: string | null): string | null {
  if (text === null) return null;
  const line = text.split("\n").map((s) => s.trim()).find((s) => s !== "");
  return line ?? null;
}

const subscriberColumns: Column<SubscriberHit>[] = [
  { id: "name", header: "Subscriber", render: (row) => <NameCell>{row.name}</NameCell> },
  { id: "type", header: "Type", render: (row) => <Badge variant="secondary">{row.type}</Badge> },
  { id: "owner", header: "Owner", render: (row) => <Mono>{row.owner ?? "-"}</Mono> },
  {
    id: "description",
    header: "Description",
    render: (row) => <TruncatedText text={row.description} maxWidth={360} />,
  },
  {
    id: "notes",
    header: "Notes",
    render: (row) => <TruncatedText text={firstLine(row.notes)} maxWidth={360} />,
  },
  { id: "url", header: "Location", render: (row) => <TruncatedText text={row.url} maxWidth={320} /> },
  { id: "file", header: "Declared in", render: (row) => <TruncatedText text={row.file} mono maxWidth={320} /> },
];

/**
 * Global search over the whole catalog: objects and columns by name, code (module bodies and emitted DDL),
 * processed files by name or path, flow YAML by name, path, or body text, and the subscribers that consume the
 * warehouse. The title-bar search box lands here with ?q=; the All tab shows a grouped preview across every
 * surface and each dedicated tab pages one surface.
 */
export default function SearchPage() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const q = (searchParams.get("q") ?? "").trim();
  const [term, setTerm] = useState(q);
  const [tab, setTab] = useState<TabIndex>(0);

  // The title-bar search navigates here while this page is already mounted: mirror the new term into the input.
  useEffect(() => {
    setTerm(q);
  }, [q]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = term.trim();
    setSearchParams(trimmed === "" ? {} : { q: trimmed }, { replace: true });
  };

  const openLineage = (name: string) => navigate(`/lineage/objects?name=${encodeURIComponent(name)}`);
  const openRun = (runId: string) => navigate(`/runs/${runId}`);
  const openPipeline = (id: string) => navigate(`/pipelines/${id}`);
  const openSubscriber = (key: string) => navigate(`/subscribers?key=${encodeURIComponent(key)}`);

  return (
    <Page data-testid="page-search">
      <PageHeader title="Search" />

      <form onSubmit={submit} className="flex items-center gap-2">
        <Input
          value={term}
          onChange={(event) => setTerm(event.target.value)}
          placeholder="Search term"
          aria-label="Search term"
          data-testid="search-input"
          className="h-8 max-w-[480px] flex-1"
        />
        <Button type="submit" size="sm" data-testid="search-submit">
          <Search />
          Search
        </Button>
      </form>

      <Tabs
        value={String(tab)}
        onValueChange={(value) => setTab(Number(value) as TabIndex)}
        data-testid="search-tabs"
      >
        <TabsList>
          {TABS.map((label, index) => (
            <TabsTrigger
              key={label}
              value={String(index)}
              data-testid={`search-tab-${label.toLowerCase()}`}
              className="px-3 text-[13px]"
            >
              {label}
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>

      {q === "" && (
        <EmptyState
          icon={<Search />}
          title="Type a term to search objects, columns, code, processed files, flows, flow columns, and executed SQL"
          data-testid="search-hint"
        />
      )}

      {q !== "" && tab === 0 && (
        <AllResults
          q={q}
          onSelectTab={setTab}
          openLineage={openLineage}
          openRun={openRun}
          openPipeline={openPipeline}
          openSubscriber={openSubscriber}
        />
      )}

      {q !== "" && tab === 1 && (
        <PagedTable<ObjectHit>
          queryKey={["search", "objects", q]}
          fetchPage={(page, pageSize) => searchApi.objects(q, { page, pageSize })}
          columns={[...objectColumns, lineageColumn<ObjectHit>(objectTarget)]}
          rowKey={(row) => row.key}
          onRowClick={(row) => openLineage(row.name)}
          emptyMessage={`No objects match "${q}".`}
          data-testid="search-objects-table"
        />
      )}

      {q !== "" && tab === 2 && (
        <PagedTable<ColumnHit>
          queryKey={["search", "columns", q]}
          fetchPage={(page, pageSize) => searchApi.columns(q, { page, pageSize })}
          columns={[...columnColumns, lineageColumn<ColumnHit>(columnTarget)]}
          rowKey={(row) => `${row.objectKey}::${row.columnName}`}
          onRowClick={(row) => openLineage(row.objectName)}
          emptyMessage={`No columns match "${q}".`}
          data-testid="search-columns-table"
        />
      )}

      {q !== "" && tab === 3 && (
        <PagedTable<DefinitionHit>
          queryKey={["search", "definitions", q]}
          fetchPage={(page, pageSize) => searchApi.definitions(q, { page, pageSize })}
          columns={[...definitionColumns, lineageColumn<DefinitionHit>(definitionTarget)]}
          rowKey={(row) => row.key}
          onRowClick={(row) => openLineage(row.name)}
          emptyMessage={`No code matches "${q}".`}
          data-testid="search-definitions-table"
        />
      )}

      {q !== "" && tab === 4 && (
        <PagedTable<FileHit>
          queryKey={["search", "files", q]}
          fetchPage={(page, pageSize) => searchApi.files(q, { page, pageSize })}
          columns={[...fileColumns, lineageColumn<FileHit>(fileTarget)]}
          rowKey={(row) => `${row.runId}::${row.name}::${row.path ?? ""}`}
          onRowClick={(row) => openRun(row.runId)}
          emptyMessage={`No processed files match "${q}".`}
          data-testid="search-files-table"
        />
      )}

      {q !== "" && tab === 5 && (
        <PagedTable<FlowHit>
          queryKey={["search", "flows", q]}
          fetchPage={(page, pageSize) => searchApi.flows(q, { page, pageSize })}
          columns={[...flowColumns, lineageColumn<FlowHit>(flowTarget)]}
          rowKey={(row) => row.id}
          onRowClick={(row) => openPipeline(row.id)}
          emptyMessage={`No flows match "${q}".`}
          data-testid="search-flows-table"
        />
      )}

      {q !== "" && tab === 6 && (
        <PagedTable<FlowColumnHit>
          queryKey={["search", "flow-columns", q]}
          fetchPage={(page, pageSize) => searchApi.flowColumns(q, { page, pageSize })}
          columns={[...flowColumnColumns, lineageColumn<FlowColumnHit>(flowColumnTarget)]}
          rowKey={(row) => `${row.pipelineId}::${row.kind}::${row.ordinal}::${row.columnName}`}
          onRowClick={(row) => openPipeline(row.pipelineId)}
          emptyMessage={`No flow columns match "${q}".`}
          data-testid="search-flow-columns-table"
        />
      )}

      {q !== "" && tab === 7 && (
        <PagedTable<StatementHit>
          queryKey={["search", "statements", q]}
          fetchPage={(page, pageSize) => searchApi.statements(q, { page, pageSize })}
          columns={statementColumns}
          rowKey={(row) => `${row.pipelineId}::${row.step}`}
          onRowClick={(row) => openRun(row.runId)}
          emptyMessage={`No executed SQL matches "${q}" in the searched window.`}
          data-testid="search-statements-table"
        />
      )}

      {q !== "" && tab === 8 && (
        <PagedTable<SubscriberHit>
          queryKey={["search", "subscribers", q]}
          fetchPage={(page, pageSize) => searchApi.subscribers(q, { page, pageSize })}
          columns={subscriberColumns}
          rowKey={(row) => row.key}
          onRowClick={(row) => openSubscriber(row.key)}
          emptyMessage={`No subscriber matches "${q}".`}
          data-testid="search-subscribers-table"
        />
      )}
    </Page>
  );
}

interface AllResultsProps {
  q: string;
  onSelectTab: (tab: TabIndex) => void;
  openLineage: (name: string) => void;
  openRun: (runId: string) => void;
  openPipeline: (id: string) => void;
  openSubscriber: (key: string) => void;
}

/** The unified landing view: one query fanned across every surface, each category previewed with a jump to its tab. */
function AllResults({ q, onSelectTab, openLineage, openRun, openPipeline, openSubscriber }: AllResultsProps) {
  const query = useQuery({
    queryKey: ["search", "all", q],
    queryFn: () => searchApi.all(q),
  });

  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const data = query.data;
  if (data === undefined) {
    return (
      <div className="flex flex-col gap-4" data-testid="search-all-loading">
        {Array.from({ length: 3 }, (_, i) => (
          <Skeleton key={`search-all-skeleton-${i}`} className="h-28 rounded-lg" />
        ))}
      </div>
    );
  }

  const totalHits = data.objects.total + data.columns.total + data.definitions.total
    + data.files.total + data.flows.total + data.flowColumns.total + data.statements.total
    + data.subscribers.total;

  if (totalHits === 0) {
    return <EmptyState title={`Nothing matches "${q}".`} data-testid="search-all-empty" />;
  }

  return (
    <div className="flex flex-col gap-4" data-testid="search-all">
      <CategorySection<ObjectHit>
        title="Objects" tab={1} category={data.objects} onSelectTab={onSelectTab}
        rowKey={(row) => row.key} onRowClick={(row) => openLineage(row.name)}
        jumpTarget={objectTarget}
        primary={(row) => row.name}
        secondary={(row) => [row.kind, row.schema, row.database, row.serverRef].filter(Boolean).join(" · ")}
      />
      <CategorySection<ColumnHit>
        title="Columns" tab={2} category={data.columns} onSelectTab={onSelectTab}
        rowKey={(row) => `${row.objectKey}::${row.columnName}`} onRowClick={(row) => openLineage(row.objectName)}
        jumpTarget={columnTarget}
        primary={(row) => row.columnName}
        secondary={(row) => [row.dataType ?? "", `on ${row.objectName}`].filter(Boolean).join(" · ")}
      />
      <CategorySection<DefinitionHit>
        title="Code" tab={3} category={data.definitions} onSelectTab={onSelectTab}
        rowKey={(row) => row.key} onRowClick={(row) => openLineage(row.name)}
        jumpTarget={definitionTarget}
        primary={(row) => `${row.name} (${row.source})`}
        secondary={(row) => row.snippet}
      />
      <CategorySection<FileHit>
        title="Files" tab={4} category={data.files} onSelectTab={onSelectTab}
        rowKey={(row) => `${row.runId}::${row.name}::${row.path ?? ""}`} onRowClick={(row) => openRun(row.runId)}
        jumpTarget={fileTarget}
        primary={(row) => row.name}
        secondary={(row) => [
          row.flowName, `${row.rows.toLocaleString()} rows`, formatBytes(row.sizeBytes),
        ].join(" · ")}
      />
      <CategorySection<FlowHit>
        title="Flows" tab={5} category={data.flows} onSelectTab={onSelectTab}
        rowKey={(row) => row.id} onRowClick={(row) => openPipeline(row.id)}
        jumpTarget={flowTarget}
        primary={(row) => row.name}
        secondary={(row) => `${row.matchedIn}: ${row.snippet}`}
      />
      <CategorySection<FlowColumnHit>
        title="Flow columns" tab={6} category={data.flowColumns} onSelectTab={onSelectTab}
        rowKey={(row) => `${row.pipelineId}::${row.kind}::${row.ordinal}::${row.columnName}`}
        onRowClick={(row) => openPipeline(row.pipelineId)}
        jumpTarget={flowColumnTarget}
        primary={(row) => row.columnName}
        secondary={(row) => [
          row.dataType ?? "", `produced by ${row.flowName}`, `matched ${row.matchedIn}`,
        ].filter(Boolean).join(" · ")}
      />
      <CategorySection<StatementHit>
        title="Executed SQL" tab={7} category={data.statements} onSelectTab={onSelectTab}
        rowKey={(row) => `${row.pipelineId}::${row.step}`} onRowClick={(row) => openRun(row.runId)}
        primary={(row) => `${row.flowName} · ${row.step}`}
        secondary={(row) => row.snippet}
      />
      {/* The consumption side. A note is shown ahead of the description when there is one: a report that has not
          refreshed since 2022, or whose dataset is incomplete, is the thing worth knowing about it at a glance. */}
      <CategorySection<SubscriberHit>
        title="Subscribers" tab={8} category={data.subscribers} onSelectTab={onSelectTab}
        rowKey={(row) => row.key} onRowClick={(row) => openSubscriber(row.key)}
        primary={(row) => row.name}
        secondary={(row) => [
          row.type, row.owner ?? "", firstLine(row.notes) ?? row.description ?? "",
        ].filter(Boolean).join(" · ")}
      />
    </div>
  );
}

interface CategorySectionProps<T> {
  title: string;
  tab: TabIndex;
  category: SearchCategory<T>;
  onSelectTab: (tab: TabIndex) => void;
  rowKey: (row: T) => string;
  onRowClick: (row: T) => void;
  /** The lineage jump for each preview row; return null for a row that cannot be traced (renders no action). */
  jumpTarget?: (row: T) => LineageJumpTarget | null;
  primary: (row: T) => ReactNode;
  secondary: (row: T) => string;
}

/** One grouped block in the All view: a titled count, the top preview rows, and a "see all" jump to the tab. */
function CategorySection<T>({
  title, tab, category, onSelectTab, rowKey, onRowClick, jumpTarget, primary, secondary,
}: CategorySectionProps<T>) {
  if (category.total === 0) {
    return null;
  }

  const remaining = category.total - category.items.length;
  return (
    <Card className="gap-0 rounded-lg p-4" data-testid={`search-all-${title.toLowerCase()}`}>
      <div className="flex items-baseline gap-2">
        <h2 className="text-[13px] font-semibold">{title}</h2>
        <Badge variant="secondary" className="font-mono text-[11px] tabular-nums">
          {category.total.toLocaleString()}
        </Badge>
        <button
          type="button"
          onClick={() => onSelectTab(tab)}
          className="ml-auto text-[13px] text-primary hover:underline"
        >
          See all
        </button>
      </div>
      <div className="mt-2 divide-y divide-border">
        {category.items.map((row) => {
          const jump = jumpTarget?.(row) ?? null;
          return (
            <div key={rowKey(row)} className="flex items-center gap-2">
              <div
                onClick={() => onRowClick(row)}
                className="min-w-0 flex-1 cursor-pointer py-1.5 transition-colors hover:bg-accent/50"
              >
                <div className="truncate font-mono text-[12px] font-medium">{primary(row)}</div>
                <Mono className="block whitespace-pre-wrap text-muted-foreground">{secondary(row)}</Mono>
              </div>
              {jump !== null && <LineageJumpButton target={jump} />}
            </div>
          );
        })}
      </div>
      {remaining > 0 && (
        <div className="mt-2 text-xs text-muted-foreground">{`and ${remaining.toLocaleString()} more`}</div>
      )}
    </Card>
  );
}
