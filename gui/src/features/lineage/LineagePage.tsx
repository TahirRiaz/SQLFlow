import { useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Network, X } from "lucide-react";
import { useLocalStorageState, useUrlSeed } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Separator } from "@/components/ui/separator";
import {
  Sheet,
  SheetClose,
  SheetContent,
  SheetDescription,
  SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { LineageObject, LineageObjectColumn } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { ConnectionRef } from "../../components/ConnectionRef";
import { CorrelationError } from "../../components/CorrelationError";
import { DetailPair } from "../../components/DetailPair";
import { activeFilterClass, FilterBar } from "../../components/FilterBar";
import { graphViewPath } from "./graphLocation";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";

const OBJECT_KINDS = ["Table", "View", "Procedure", "Function", "Trigger", "Synonym", "File"];

/** Local debounce for free-text filters: the table re-queries 400ms after the user stops typing. */
function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

const objectTableColumns: Column<LineageObject>[] = [
  { id: "database", header: "Database", render: (row) => <Mono>{row.database ?? "-"}</Mono> },
  { id: "schema", header: "Schema", render: (row) => <Mono>{row.schema ?? "-"}</Mono> },
  {
    id: "name",
    header: "Name",
    render: (row) => <span className="font-mono text-[12px] font-medium">{row.name}</span>,
  },
  { id: "level", header: "Level", render: (row) => row.level ?? "-", width: 72 },
  { id: "kind", header: "Kind", render: (row) => <Badge variant="secondary">{row.kind}</Badge> },
  { id: "serverRef", header: "Server", render: (row) => <ConnectionRef value={row.serverRef} /> },
  { id: "lastSeen", header: "Last seen", render: (row) => <RelativeTime value={row.lastSeenUtc} /> },
];

const columnTableColumns: Column<LineageObjectColumn>[] = [
  {
    id: "ordinal",
    header: "#",
    render: (row) => <span className="font-mono tabular-nums">{row.ordinal}</span>,
    width: 56,
  },
  { id: "name", header: "Name", render: (row) => <Mono>{row.name}</Mono> },
  { id: "dataType", header: "Data type", render: (row) => <Mono>{row.dataType ?? "-"}</Mono> },
  {
    id: "nullable",
    header: "Nullable",
    render: (row) => (
      <Badge variant={row.nullable ? "outline" : "secondary"}>{row.nullable ? "null" : "not null"}</Badge>
    ),
  },
];

/** The drawer body: object detail (with the SQL definition when captured) plus the paged column list. */
function ObjectDrawerContent({ objectKey }: { objectKey: string }) {
  const detail = useQuery({
    queryKey: ["lineage-object-detail", objectKey],
    queryFn: () => lineageApi.objectDetail(objectKey),
  });

  if (detail.isPending) {
    return (
      <div className="flex flex-col gap-2 p-4">
        <Skeleton className="h-8 w-60" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-4/5" />
        <Skeleton className="h-4 w-3/5" />
        <Skeleton className="mt-2 h-60 w-full" />
      </div>
    );
  }

  if (detail.isError) {
    return (
      <div className="p-4">
        {isApiError(detail.error)
          ? <CorrelationError error={detail.error} />
          : <p className="text-[13px] text-destructive">{String(detail.error)}</p>}
      </div>
    );
  }

  const data = detail.data;
  return (
    <div className="p-4">
      <div className="mb-1 flex items-center gap-2">
        <h2 className="min-w-0 break-words font-mono text-base font-medium">{data.name}</h2>
        <Badge variant="secondary">{data.kind}</Badge>
      </div>
      <div className="mb-4 break-all font-mono text-xs text-muted-foreground" data-testid="object-key">
        {data.key}
      </div>

      <div className="mb-4 grid grid-cols-2 gap-3">
        <DetailPair label="Server"><ConnectionRef value={data.serverRef} copyTestId="copy-lineage-server" /></DetailPair>
        <DetailPair label="Database"><Mono>{data.database ?? "-"}</Mono></DetailPair>
        <DetailPair label="Schema"><Mono>{data.schema ?? "-"}</Mono></DetailPair>
        <DetailPair label="Level">{data.level ?? "-"}</DetailPair>
        <DetailPair label="First seen"><RelativeTime value={data.firstSeenUtc} absolute /></DetailPair>
        <DetailPair label="Last seen"><RelativeTime value={data.lastSeenUtc} absolute /></DetailPair>
      </div>

      {data.definition !== null && (
        <div className="mb-4">
          <h3 className="mb-1.5 text-[13px] font-medium">Definition</h3>
          <CodeView value={data.definition} language="sql" height={320} data-testid="object-definition" />
        </div>
      )}

      <Separator className="mb-4" />
      <h3 className="mb-1.5 text-[13px] font-medium">Columns</h3>
      <PagedTable<LineageObjectColumn>
        queryKey={["lineage-object-columns", objectKey]}
        fetchPage={(page, pageSize) => lineageApi.objectColumns(objectKey, { page, pageSize })}
        columns={columnTableColumns}
        rowKey={(row) => row.ordinal}
        emptyMessage="No columns recorded for this object."
        data-testid="object-columns-table"
      />
    </div>
  );
}

/**
 * The lineage catalog: every object the analyzer has seen, filterable by name, kind, and server, with a
 * detail sheet (definition + columns) per object. The search page deep-links here with ?name=.
 */
export default function LineagePage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [name, setName] = useLocalStorageState("sqlflow.filters.lineage.name", "");
  const [kind, setKind] = useLocalStorageState("sqlflow.filters.lineage.kind", "all");
  const [serverRef, setServerRef] = useLocalStorageState("sqlflow.filters.lineage.serverRef", "");
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  // A ?name= deep link (from the search page) overrides and updates the remembered filter.
  useUrlSeed(searchParams.get("name"), setName);

  const debouncedName = useDebounced(name, 400);
  const debouncedServerRef = useDebounced(serverRef, 400);

  return (
    <Page data-testid="page-lineage">
      <PageHeader
        title="Lineage"
        actions={(
          <Button
            variant="outline"
            size="sm"
            // Back to the drawing the operator left, not the bare landing: the graph keeps its whole state in
            // its query string, which this restores.
            onClick={() => navigate(graphViewPath())}
            data-testid="open-lineage-graph"
          >
            <Network />
            Graph view
          </Button>
        )}
      />

      <FilterBar>
        <Input
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Name"
          aria-label="Name"
          data-testid="filter-object-name"
          className={cn("h-8 w-56", name !== "" && activeFilterClass)}
        />
        <Select value={kind} onValueChange={setKind}>
          <SelectTrigger size="sm" active={kind !== "all"} className="h-8 w-40" aria-label="Kind" data-testid="filter-object-kind">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All kinds</SelectItem>
            {OBJECT_KINDS.map((option) => (
              <SelectItem key={option} value={option}>{option}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Input
          value={serverRef}
          onChange={(event) => setServerRef(event.target.value)}
          placeholder="Server ref"
          aria-label="Server ref"
          data-testid="filter-server-ref"
          className={cn("h-8 w-56", serverRef !== "" && activeFilterClass)}
        />
      </FilterBar>

      <PagedTable<LineageObject>
        queryKey={["lineage-objects", debouncedName, kind, debouncedServerRef]}
        fetchPage={(page, pageSize) => lineageApi.objects({
          name: debouncedName === "" ? undefined : debouncedName,
          kind: kind === "all" ? undefined : kind,
          serverRef: debouncedServerRef === "" ? undefined : debouncedServerRef,
          page,
          pageSize,
        })}
        columns={objectTableColumns}
        rowKey={(row) => row.key}
        onRowClick={(row) => setSelectedKey(row.key)}
        emptyMessage="No lineage objects match the current filters."
        data-testid="lineage-objects-table"
      />

      <Sheet
        open={selectedKey !== null}
        onOpenChange={(open) => {
          if (!open) {
            setSelectedKey(null);
          }
        }}
      >
        <SheetContent
          side="right"
          showCloseButton={false}
          data-testid="object-drawer"
          className="w-full gap-0 overflow-y-auto sm:max-w-[560px]"
        >
          <SheetTitle className="sr-only">Object details</SheetTitle>
          <SheetDescription className="sr-only">
            Definition and columns for the selected lineage object.
          </SheetDescription>
          <div className="flex justify-end px-2 pt-2">
            <SheetClose asChild>
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label="Close object details"
                data-testid="object-drawer-close"
              >
                <X />
              </Button>
            </SheetClose>
          </div>
          {selectedKey !== null && <ObjectDrawerContent objectKey={selectedKey} />}
        </SheetContent>
      </Sheet>
    </Page>
  );
}
