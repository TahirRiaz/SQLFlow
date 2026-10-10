import { useCallback, useMemo, useState, type FocusEvent } from "react";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { BookOpen, CircleAlert, Database, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { NodeLabel, TreeContext, TreeNode, type TreeState } from "@/components/Tree";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { semanticLayerApi } from "../../api/endpoints";
import type { SemanticObjectCoverage, SemanticSchemaCoverage } from "../../api/types";
import { metaForKind } from "../catalog/kindMeta";
import { UNRESOLVED_LABEL } from "../catalog/nodeIds";
import { errorText, SEMANTIC_ROOT, useDebouncedValue } from "./shared";

const LEAF_PAGE_SIZE = 200;
const OBJECT_PREFIX = "obj:";

const objectNodeId = (key: string) => `${OBJECT_PREFIX}${encodeURIComponent(key)}`;
const databaseNodeId = (database: string | null) => `db:${encodeURIComponent(database ?? "")}`;
const schemaNodeId = (database: string | null, schema: string | null) =>
  `schema:${encodeURIComponent(database ?? "")}/${encodeURIComponent(schema ?? "")}`;

interface SchemaBranch { schema: string | null; objectCount: number; layerObjectCount: number }
interface DatabaseBranch { database: string | null; objectCount: number; layerObjectCount: number; schemas: SchemaBranch[] }

const byNameCi = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "base" });

/** Folds the flat coverage rows into database > schema; with `layerOnly`, schemas holding no layer table drop out. */
function foldDatabases(rows: SemanticSchemaCoverage[], layerOnly: boolean): DatabaseBranch[] {
  const databases = new Map<string | null, DatabaseBranch>();
  for (const row of rows) {
    if (layerOnly && row.layerObjectCount === 0) {
      continue;
    }
    let database = databases.get(row.database);
    if (database === undefined) {
      database = { database: row.database, objectCount: 0, layerObjectCount: 0, schemas: [] };
      databases.set(row.database, database);
    }
    database.objectCount += row.objectCount;
    database.layerObjectCount += row.layerObjectCount;
    database.schemas.push({ schema: row.schema, objectCount: row.objectCount, layerObjectCount: row.layerObjectCount });
  }

  const branches = [...databases.values()];
  for (const database of branches) {
    database.schemas.sort((a, b) => byNameCi(a.schema ?? "", b.schema ?? ""));
  }
  return branches.sort((a, b) => byNameCi(a.database ?? "", b.database ?? ""));
}

/** One object row: its kind icon, its name (and business name), and allowed/total columns as the coverage badge. */
function ObjectLeaf({ row }: { row: SemanticObjectCoverage }) {
  return (
    <TreeNode
      id={objectNodeId(row.key)}
      label={(
        <NodeLabel
          icon={metaForKind(row.kind).icon}
          text={row.businessName === null ? row.name : `${row.name} (${row.businessName})`}
          badge={`${row.allowedColumns}/${row.totalColumns}`}
        />
      )}
    />
  );
}

/** The objects of one schema, lazily paged on expand. */
function ObjectLeaves({
  parentId, database, schema, layerOnly,
}: {
  parentId: string;
  database: string | null;
  schema: string | null;
  layerOnly: boolean;
}) {
  const leaves = useInfiniteQuery({
    queryKey: [SEMANTIC_ROOT, "objects", database, schema, layerOnly],
    queryFn: ({ pageParam }) => semanticLayerApi.objects({
      database: database ?? undefined,
      schema: schema ?? undefined,
      inLayer: layerOnly,
      page: pageParam,
      pageSize: LEAF_PAGE_SIZE,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
  });

  if (leaves.isPending) {
    return <TreeNode id={`${parentId}#loading`} disabled label={<NodeLabel text="Loading…" />} />;
  }
  if (leaves.isError) {
    return <TreeNode id={`${parentId}#error`} disabled label={<NodeLabel text={`Could not load objects: ${errorText(leaves.error)}`} />} />;
  }

  const rows = leaves.data.pages.flatMap((page) => page.items);
  const remaining = (leaves.data.pages[0]?.total ?? 0) - rows.length;
  return (
    <>
      {rows.length === 0 && <TreeNode id={`${parentId}#empty`} disabled label={<NodeLabel text="No objects" />} />}
      {rows.map((row) => <ObjectLeaf key={row.key} row={row} />)}
      {remaining > 0 && (
        <TreeNode
          id={`${parentId}#more`}
          disabled
          label={(
            <Button
              variant="ghost"
              size="xs"
              onClick={() => void leaves.fetchNextPage()}
              disabled={leaves.isFetchingNextPage}
            >
              {leaves.isFetchingNextPage && <Loader2 className="animate-spin" />}
              {`Load ${Math.min(remaining, LEAF_PAGE_SIZE)} more of ${remaining}`}
            </Button>
          )}
        />
      )}
    </>
  );
}

/**
 * The semantic layer editor's explorer: database > schema > object over every object that has catalogued columns
 * (the only objects a column policy applies to), each object badged with allowed/total columns so the layer's
 * coverage reads at a glance. "In the layer only" narrows to objects with at least one allowed column. Typing two or
 * more characters replaces the tree with a flat name/business-name search.
 */
export function SemanticTree({ selectedKey, onSelect }: { selectedKey: string | null; onSelect: (key: string) => void }) {
  const [filter, setFilter] = useLocalStorageState("sqlflow.filters.semantic-layer.tree", "");
  const [scope, setScope] = useLocalStorageState("sqlflow.filters.semantic-layer.scope", "all");
  const layerOnly = scope === "layer";
  const [expanded, setExpanded] = useState<string[]>([]);
  const term = useDebouncedValue(filter.trim(), 350);
  const searching = term.length >= 2;

  const schemas = useQuery({ queryKey: [SEMANTIC_ROOT, "schemas"], queryFn: () => semanticLayerApi.schemas() });
  const matches = useQuery({
    queryKey: [SEMANTIC_ROOT, "objects", "match", term, layerOnly],
    queryFn: () => semanticLayerApi.objects({ name: term, inLayer: layerOnly, pageSize: 50 }),
    enabled: searching,
  });

  const databases = useMemo(() => foldDatabases(schemas.data ?? [], layerOnly), [schemas.data, layerOnly]);
  const expandedSet = useMemo(() => new Set(expanded), [expanded]);

  const setOpen = useCallback((id: string, open: boolean) => {
    setExpanded((current) => {
      if (open) {
        return current.includes(id) ? current : [...current, id];
      }
      return current.filter((item) => item !== id);
    });
  }, []);

  const treeState: TreeState = {
    expanded: expandedSet,
    setOpen,
    toggle: (id) => setOpen(id, !expandedSet.has(id)),
    selectedId: selectedKey === null ? null : objectNodeId(selectedKey),
    select: (id) => {
      if (id.startsWith(OBJECT_PREFIX)) {
        onSelect(decodeURIComponent(id.slice(OBJECT_PREFIX.length)));
      }
    },
  };

  const onTreeFocus = (event: FocusEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) {
      return;
    }
    const rows = [...event.currentTarget.querySelectorAll<HTMLElement>("[data-tree-row]")];
    (rows.find((row) => row.dataset.id === treeState.selectedId) ?? rows[0])?.focus();
  };

  return (
    <div className="flex flex-col gap-3" data-testid="semantic-tree">
      <Input
        className="h-8"
        placeholder="Search tables by name or business name"
        aria-label="Search tables by name or business name"
        value={filter}
        onChange={(event) => setFilter(event.target.value)}
        data-testid="semantic-tree-filter"
      />
      <div className="flex items-center gap-2">
        <Switch
          id="semantic-tree-layer-only"
          checked={layerOnly}
          onCheckedChange={(checked) => setScope(checked ? "layer" : "all")}
          data-testid="semantic-tree-layer-only"
        />
        <Label htmlFor="semantic-tree-layer-only" className="text-[13px] font-normal">In the layer only</Label>
      </div>

      {schemas.isError && (
        <Alert variant="destructive" data-testid="semantic-tree-error">
          <CircleAlert />
          <AlertTitle>The table tree could not load</AlertTitle>
          <AlertDescription>
            <p>{errorText(schemas.error)}</p>
            <Button variant="outline" size="xs" onClick={() => void schemas.refetch()}>Retry</Button>
          </AlertDescription>
        </Alert>
      )}
      {schemas.isPending && (
        <div className="flex flex-col gap-2">
          {Array.from({ length: 8 }, (_, i) => <Skeleton key={i} className="h-7 w-full" />)}
        </div>
      )}

      {schemas.data !== undefined && (
        <div role="tree" aria-label="Semantic layer tables" tabIndex={0} onFocus={onTreeFocus} className="outline-none">
          <TreeContext.Provider value={treeState}>
            {searching && (
              <>
                {matches.isPending && <TreeNode id="matches#loading" disabled label={<NodeLabel text="Searching…" />} />}
                {matches.isError && (
                  <TreeNode id="matches#error" disabled label={<NodeLabel text={errorText(matches.error)} />} />
                )}
                {matches.data?.items.length === 0 && (
                  <TreeNode id="matches#empty" disabled label={<NodeLabel text={`No table matches "${term}".`} />} />
                )}
                {matches.data?.items.map((row) => <ObjectLeaf key={row.key} row={row} />)}
              </>
            )}

            {!searching && databases.length === 0 && (
              <TreeNode
                id="dbs#empty"
                disabled
                label={<NodeLabel text={layerOnly ? "No table is in the semantic layer yet" : "No object has catalogued columns yet"} />}
              />
            )}
            {!searching && databases.map((database) => {
              const databaseId = databaseNodeId(database.database);
              return (
                <TreeNode
                  key={databaseId}
                  id={databaseId}
                  label={(
                    <NodeLabel
                      icon={<Database className="size-4" />}
                      text={database.database ?? UNRESOLVED_LABEL}
                      badge={`${database.layerObjectCount}/${database.objectCount}`}
                    />
                  )}
                >
                  {database.schemas.map((schema) => {
                    const schemaId = schemaNodeId(database.database, schema.schema);
                    return (
                      <TreeNode
                        key={schemaId}
                        id={schemaId}
                        label={(
                          <NodeLabel
                            icon={<BookOpen className="size-4" />}
                            text={schema.schema ?? UNRESOLVED_LABEL}
                            badge={`${schema.layerObjectCount}/${schema.objectCount}`}
                          />
                        )}
                      >
                        <ObjectLeaves
                          parentId={schemaId}
                          database={database.database}
                          schema={schema.schema}
                          layerOnly={layerOnly}
                        />
                      </TreeNode>
                    );
                  })}
                </TreeNode>
              );
            })}
          </TreeContext.Provider>
        </div>
      )}
    </div>
  );
}
