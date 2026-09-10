import { useCallback, useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import {
  ArrowLeft, ChevronLeft, ChevronRight, CircleAlert, Loader2, Lock,
} from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import type {
  DatasourceDatabase, DatasourceObject, DatasourceObjectPage, DatasourceSchema,
} from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ComboBoxField } from "../../components/ComboBoxField";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { activeFilterClass, FilterBar } from "../../components/FilterBar";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { useTabTitle } from "../../layout/workbench/TabsContext";
import { ObjectInspector } from "./ObjectInspector";
import { useCompute } from "./useCompute";

const objectColumns: Column<DatasourceObject>[] = [
  {
    id: "name",
    header: "Object",
    render: (row) => <Mono className="font-semibold">{row.schema}.{row.name}</Mono>,
  },
  { id: "type", header: "Type", render: (row) => <Badge variant="outline">{row.type}</Badge> },
  {
    id: "rows",
    header: "Approx. rows",
    align: "right",
    render: (row) => (
      <span className="font-mono tabular-nums">
        {row.type === "Table" ? row.approxRows.toLocaleString() : "-"}
      </span>
    ),
  },
];

/**
 * The live browser for one datasource: databases, then schemas, then a filtered, paged table/view listing,
 * each fetched as a compute task executed by a worker node that can reach the source (the browser never
 * connects to anything itself). Clicking an object opens the live introspection sheet.
 */
export default function DatasourceBrowsePage() {
  const [params] = useSearchParams();
  const reference = params.get("ref") ?? "";
  const kind = params.get("kind");
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  useTabTitle(reference === "" ? undefined : `Browse ${reference}`);

  const databases = useCompute<{ databases: DatasourceDatabase[] }>();
  const schemas = useCompute<{ schemas: DatasourceSchema[] }>();
  const objects = useCompute<DatasourceObjectPage>();
  const { run: runDatabases } = databases;
  const { run: runSchemas } = schemas;
  const { run: runObjects } = objects;

  const [database, setDatabase] = useState<string | null>(null);
  const [schema, setSchema] = useState<string | null>(null);
  const [nameLike, setNameLike] = useState("");
  const [appliedNameLike, setAppliedNameLike] = useState("");
  const [includeViews, setIncludeViews] = useLocalStorageState("sqlflow.filters.datasourceBrowse.includeViews", true);
  const [includeSystem, setIncludeSystem] = useLocalStorageState("sqlflow.filters.datasourceBrowse.includeSystem", false);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(50);
  const [selected, setSelected] = useState<DatasourceObject | null>(null);

  const usable = reference !== "" && canOperate;

  // Databases load once per datasource; schemas reload when the database scope changes.
  useEffect(() => {
    if (usable) {
      void runDatabases({ reference, kind, operation: "listDatabases" });
    }
  }, [usable, runDatabases, reference, kind]);

  useEffect(() => {
    if (usable) {
      void runSchemas({ reference, kind, database, operation: "listSchemas" });
    }
  }, [usable, runSchemas, reference, kind, database]);

  const loadObjects = useCallback(
    (targetPage: number, targetPageSize: number, filter: string) => {
      if (!usable) {
        return;
      }

      void runObjects({
        reference,
        kind,
        database,
        schema,
        nameLike: filter === "" ? null : filter,
        includeViews,
        includeSystem,
        offset: targetPage * targetPageSize,
        limit: targetPageSize,
        operation: "listObjects",
      });
    },
    [usable, runObjects, reference, kind, database, schema, includeViews, includeSystem],
  );

  // The object listing tracks every scope/filter change; paging state resets when the scope changes so a page
  // index from the previous scope is never applied to a new one.
  useEffect(() => {
    setPage(0);
    loadObjects(0, pageSize, appliedNameLike);
    // pageSize changes go through the pagination handlers below (which also refetch), so it is not a dep here.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loadObjects, appliedNameLike]);

  if (reference === "") {
    return (
      <Page data-testid="page-datasource-browse">
        <PageHeader title="Browse datasource" />
        <EmptyState
          title="No datasource selected"
          description="Open this page from the Datasources list so it knows which reference to browse."
          action={(
            <Button variant="outline" size="sm" asChild>
              <Link to="/datasources">
                <ArrowLeft />
                Datasources
              </Link>
            </Button>
          )}
        />
      </Page>
    );
  }

  if (!canOperate) {
    return (
      <Page data-testid="page-datasource-browse">
        <PageHeader title="Browse datasource" />
        <EmptyState
          icon={<Lock />}
          title="Operate scope required"
          description="Browsing runs live queries against the source through a worker node, so it needs the operate scope. Ask an administrator for the operator role."
        />
      </Page>
    );
  }

  const total = Number(objects.data?.total ?? 0);
  const from = total === 0 ? 0 : page * pageSize + 1;
  const to = Math.min(total, (page + 1) * pageSize);
  const lastPage = Math.max(0, Math.ceil(total / pageSize) - 1);

  return (
    <Page data-testid="page-datasource-browse">
      <PageHeader
        title={(
          <span className="flex flex-wrap items-center gap-2">
            <span>Browse</span>
            <span className="font-mono">{reference}</span>
            {kind !== null && <Badge variant="outline">{kind}</Badge>}
          </span>
        )}
        subtitle="Everything on this page executes as a compute task on a worker node that can reach the source."
        actions={(
          <Button variant="outline" size="sm" asChild data-testid="browse-back">
            <Link to="/datasources">
              <ArrowLeft />
              Datasources
            </Link>
          </Button>
        )}
      />

      <FilterBar>
        {/* Free-text search leads the row (DESIGN.md 7.1); the database and schema pickers that scope it follow. */}
        <Input
          value={nameLike}
          onChange={(e) => setNameLike(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              setAppliedNameLike(nameLike.trim());
            }
          }}
          onBlur={() => setAppliedNameLike(nameLike.trim())}
          placeholder="Name filter"
          aria-label="Name filter"
          data-testid="browse-name-filter"
          className={cn("h-8 w-full sm:w-44", nameLike !== "" && activeFilterClass)}
        />
        <ComboBoxField
          ariaLabel="Database"
          options={databases.data?.databases.map((d) => d.name) ?? []}
          optionValue={(name) => name}
          renderOption={(name) => <span className="font-mono text-[12px]">{name}</span>}
          value={database}
          onChange={(value) => {
            setDatabase(value);
            setSchema(null);
          }}
          clearOption={{
            label: "connection default",
            onClear: () => {
              setDatabase(null);
              setSchema(null);
            },
          }}
          loading={databases.running}
          placeholder="connection default"
          loadingMessage="Loading from the source..."
          testId="browse-database"
          className="w-full sm:w-60"
        />
        <ComboBoxField
          ariaLabel="Schema"
          options={schemas.data?.schemas.map((s) => s.name) ?? []}
          optionValue={(name) => name}
          renderOption={(name) => <span className="font-mono text-[12px]">{name}</span>}
          value={schema}
          onChange={(value) => setSchema(value)}
          clearOption={{ label: "all schemas", onClear: () => setSchema(null) }}
          loading={schemas.running}
          placeholder="all schemas"
          loadingMessage="Loading from the source..."
          testId="browse-schema"
          className="w-full sm:w-50"
        />
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch
            checked={includeViews}
            onCheckedChange={setIncludeViews}
            data-testid="browse-include-views"
          />
          Views
        </Label>
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch
            checked={includeSystem}
            onCheckedChange={setIncludeSystem}
            data-testid="browse-include-system"
          />
          System objects
        </Label>
      </FilterBar>

      {databases.error !== null && (
        <Alert variant="destructive" data-testid="browse-databases-error">
          <CircleAlert />
          <AlertDescription>{databases.error}</AlertDescription>
        </Alert>
      )}
      {schemas.error !== null && databases.error === null && (
        <Alert variant="destructive" data-testid="browse-schemas-error">
          <CircleAlert />
          <AlertDescription>{schemas.error}</AlertDescription>
        </Alert>
      )}
      {objects.error !== null && (
        <Alert variant="destructive" data-testid="browse-objects-error">
          <CircleAlert />
          <AlertDescription>{objects.error}</AlertDescription>
        </Alert>
      )}

      {objects.running && (
        <div className="flex items-center gap-2 text-[13px] text-muted-foreground" data-testid="browse-loading">
          <Loader2 className="size-4 animate-spin" />
          Listing objects on a worker node...
        </div>
      )}

      <DataTable
        columns={objectColumns}
        rows={objects.running && objects.data === null ? undefined : objects.data?.items ?? []}
        rowKey={(row) => `${row.schema}.${row.name}`}
        onRowClick={(row) => setSelected(row)}
        emptyMessage={objects.error !== null
          ? "The listing failed; fix the error above and adjust the scope."
          : "No tables or views match this scope."}
        data-testid="browse-objects-table"
        footer={(
          <div className="flex items-center justify-between gap-4 border-t border-border px-3 py-1.5">
            <div className="flex items-center gap-2 text-xs text-muted-foreground">
              Rows per page
              <Select
                value={String(pageSize)}
                onValueChange={(value) => {
                  const nextSize = Number.parseInt(value, 10);
                  setPageSize(nextSize);
                  setPage(0);
                  loadObjects(0, nextSize, appliedNameLike);
                }}
              >
                <SelectTrigger size="sm" className="h-7 w-[72px] text-xs" aria-label="Rows per page">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {[25, 50, 100, 200].map((size) => (
                    <SelectItem key={size} value={String(size)}>{size}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="flex items-center gap-1 text-xs text-muted-foreground">
              <span className="font-mono tabular-nums">{from}-{to} of {total}</span>
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label="Previous page"
                disabled={page === 0}
                onClick={() => {
                  const next = Math.max(0, page - 1);
                  setPage(next);
                  loadObjects(next, pageSize, appliedNameLike);
                }}
              >
                <ChevronLeft />
              </Button>
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label="Next page"
                disabled={page >= lastPage}
                onClick={() => {
                  const next = Math.min(lastPage, page + 1);
                  setPage(next);
                  loadObjects(next, pageSize, appliedNameLike);
                }}
              >
                <ChevronRight />
              </Button>
            </div>
          </div>
        )}
      />
      {objects.data !== null && (
        <p className="text-xs text-muted-foreground">
          {objects.data.items.length} of {objects.data.total.toLocaleString()} object(s) in scope.
        </p>
      )}

      {selected !== null && (
        <ObjectInspector
          reference={reference}
          kind={kind}
          database={database}
          object={selected}
          onClose={() => setSelected(null)}
        />
      )}
    </Page>
  );
}
