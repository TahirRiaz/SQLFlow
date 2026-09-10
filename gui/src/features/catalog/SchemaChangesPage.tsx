import { useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowRight, Database, FileCode, FilePlus2, FileX2, Folder, GitCommit, GitCompareArrows } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { useLocalStorageState, useUrlSeed } from "@/hooks/useLocalStorageState";
import { DiffView } from "@/components/DiffView";
import { cn } from "@/lib/utils";
import { NodeLabel, TreeContext, TreeNode, type TreeState } from "@/components/Tree";
import { isApiError } from "../../api/client";
import { schemaChangeApi } from "../../api/endpoints";
import type { SchemaChange, SchemaObjectCompare } from "../../api/types";
import { activeFilterClass, FilterBar } from "../../components/FilterBar";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { fetchSchemaChanges } from "./fetchSchemaChanges";

const windows = [
  { value: "1", label: "Last 24 hours" },
  { value: "3", label: "Last 3 days" },
  { value: "7", label: "Last 7 days" },
  { value: "30", label: "Last 30 days" },
  { value: "90", label: "Last 90 days" },
  { value: "365", label: "Last year" },
  { value: "all", label: "All time" },
];

type ChangeType = SchemaChange["changeType"];

/** Colour and glyph carry the meaning at a glance; a drop is the one nobody should scroll past. */
const changeMeta: Record<ChangeType, { tone: string; icon: typeof FileCode; verb: string }> = {
  Added: { tone: "text-success", icon: FilePlus2, verb: "added" },
  Changed: { tone: "text-info", icon: FileCode, verb: "changed" },
  Deleted: { tone: "text-destructive", icon: FileX2, verb: "dropped" },
};

/** A branch is coloured by the most severe change under it: a dropped object outranks an edit, an edit an add. */
function dominantChange(changes: readonly SchemaChange[]): ChangeType {
  if (changes.some((c) => c.changeType === "Deleted")) {
    return "Deleted";
  }
  return changes.some((c) => c.changeType === "Changed") ? "Changed" : "Added";
}

function sinceIso(window: string): string | undefined {
  if (window === "all") {
    return undefined;
  }
  return new Date(Date.now() - Number(window) * 24 * 60 * 60 * 1000).toISOString();
}

const byNameCi = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "base" });

interface ObjectBranch {
  /** schema.name, or the bare name for a schema-less object. */
  key: string;
  schema: string | null;
  name: string;
  category: string;
  /** Every change recorded for this object in the window, newest first. */
  history: SchemaChange[];
}

interface SchemaBranch { schema: string; objects: ObjectBranch[] }
interface DatabaseBranch { database: string; schemas: SchemaBranch[]; changeCount: number }

/** Folds the flat feed into database > schema > object, each object carrying its own change history so the
 * details panel needs no second request. Input is newest-first, so each history stays newest-first. */
function foldTree(changes: readonly SchemaChange[]): DatabaseBranch[] {
  const databases = new Map<string, Map<string, Map<string, ObjectBranch>>>();
  const counts = new Map<string, number>();

  for (const change of changes) {
    counts.set(change.database, (counts.get(change.database) ?? 0) + 1);
    let schemas = databases.get(change.database);
    if (schemas === undefined) {
      schemas = new Map();
      databases.set(change.database, schemas);
    }

    const schemaName = change.schema ?? "(no schema)";
    let objects = schemas.get(schemaName);
    if (objects === undefined) {
      objects = new Map();
      schemas.set(schemaName, objects);
    }

    const key = `${schemaName}.${change.name}`;
    const existing = objects.get(key);
    if (existing === undefined) {
      objects.set(key, {
        key,
        schema: change.schema,
        name: change.name,
        category: change.category,
        history: [change],
      });
    } else {
      existing.history.push(change);
    }
  }

  return [...databases.entries()]
    .map(([database, schemas]) => ({
      database,
      changeCount: counts.get(database) ?? 0,
      schemas: [...schemas.entries()]
        .map(([schema, objects]) => ({
          schema,
          objects: [...objects.values()].sort((a, b) => byNameCi(a.name, b.name)),
        }))
        .sort((a, b) => byNameCi(a.schema, b.schema)),
    }))
    .sort((a, b) => byNameCi(a.database, b.database));
}

/** The panel behind a selected object: every time the snapshots saw it move, newest first. */
function ObjectHistory({ branch, database }: { branch: ObjectBranch; database: string }) {
  const navigate = useNavigate();
  return (
    <Card className="gap-0 rounded-lg p-4">
      <div className="font-mono text-sm font-medium">
        {branch.schema === null ? branch.name : `${branch.schema}.${branch.name}`}
      </div>
      <div className="mt-0.5 text-xs text-muted-foreground">
        {branch.category} in <span className="font-mono">{database}</span>
      </div>

      <div className="mt-4 space-y-2">
        {branch.history.map((change) => {
          const meta = changeMeta[change.changeType];
          const Icon = meta.icon;
          return (
            <div key={change.id} className="flex items-center gap-2 text-[13px]">
              <Icon className={cn("size-4 shrink-0", meta.tone)} />
              <span className={cn("w-[70px] shrink-0 font-medium", meta.tone)}>{meta.verb}</span>
              <RelativeTime value={change.occurredUtc} />
              {change.commitSha !== null && (
                <Badge variant="outline" className="h-[18px] gap-1 px-1.5 font-mono text-[11px] text-muted-foreground">
                  <GitCommit className="size-3" />
                  {change.commitSha.slice(0, 8)}
                </Badge>
              )}
              <Button
                variant="ghost"
                size="sm"
                className="ml-auto h-6 text-xs"
                onClick={() => navigate(`/runs/${change.runId}`)}
              >
                Run
              </Button>
            </div>
          );
        })}
      </div>
    </Card>
  );
}

/** One end of the comparison, named by the commit it came from so a reader can trace it back to a snapshot. */
function Revision({ label, commit }: { label: string; commit: SchemaObjectCompare["before"] }) {
  return (
    <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
      <span className="text-muted-foreground">{label}</span>
      {commit === null
        ? <span className="text-muted-foreground">the first snapshot</span>
        : (
          <>
            <GitCommit className="size-3 shrink-0 text-muted-foreground" />
            <span className="font-mono">{commit.shortSha}</span>
            <RelativeTime value={commit.committedUtc} />
          </>
        )}
    </span>
  );
}

/**
 * The DDL behind the selected object at both ends of the window: the script the snapshots held when the window
 * opened, against the one they hold now. The history list above says an object moved; this says what actually
 * moved inside it.
 *
 * However many snapshots touched the object, this stays ONE before and ONE after, because that is the question
 * the window asks: not "what did each nightly run do" but "what is different now from then". The comparison is
 * read from the snapshot repository by the control plane, which resolves the git credential server-side, so the
 * browser never holds one.
 */
function ObjectCompare({
  branch, database, windowLabel, since,
}: { branch: ObjectBranch; database: string; windowLabel: string; since: string | undefined }) {
  // The newest row carries the object's identity and the snapshot flow that recorded it; the server derives the
  // repository path from it, so no path is built here.
  const latest = branch.history[0];
  const compareQuery = useQuery({
    queryKey: ["schema-changes", "compare", latest.id, since],
    queryFn: () => schemaChangeApi.compare(latest.id, since === undefined ? {} : { since }),
    enabled: latest.pipelineId !== null,
  });

  const compare = compareQuery.data;
  const title = branch.schema === null ? branch.name : `${branch.schema}.${branch.name}`;

  return (
    <Card className="gap-0 rounded-lg p-4" data-testid="schema-changes-compare">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <GitCompareArrows className="size-4 shrink-0 text-muted-foreground" />
        <span className="text-sm font-medium">Before and after</span>
        <span className="font-mono text-[12px] text-muted-foreground">{title}</span>
        <span className="text-xs text-muted-foreground">over {windowLabel.toLowerCase()}</span>
        {compare !== undefined && (
          <span className="ml-auto font-mono text-xs tabular-nums">
            <span className="text-success">+{compare.linesAdded}</span>
            {" "}
            <span className="text-destructive">-{compare.linesDeleted}</span>
          </span>
        )}
      </div>

      {latest.pipelineId === null
        ? (
          <Alert className="mt-3">
            <AlertDescription>
              This change was recorded before its snapshot flow reached the catalog, so the repository it was
              committed to cannot be resolved. A later snapshot of
              {" "}<span className="font-mono">{database}</span> records one that can.
            </AlertDescription>
          </Alert>
        )
        : compareQuery.isError
          ? (
            <div className="mt-3">
              {isApiError(compareQuery.error)
                ? <CorrelationError error={compareQuery.error} data-testid="schema-changes-compare-error" />
                : <p className="text-[13px] text-destructive">{String(compareQuery.error)}</p>}
            </div>
          )
          : compare === undefined
            ? <Skeleton className="mt-3 h-64 w-full" />
            : compare.beforeText === null && compare.afterText === null
              ? (
                <Alert className="mt-3">
                  <AlertDescription>
                    The snapshot repository holds no file at
                    {" "}<span className="font-mono">{compare.path}</span> at either end of this window, so there
                    is no script to compare. Widen the window to reach the snapshot that last carried it.
                  </AlertDescription>
                </Alert>
              )
              : (
                <div className="mt-3 flex flex-col gap-3">
                  {compare.truncated && (
                    <Alert>
                      <AlertDescription>
                        One side of this comparison was cut at the server's inline limit, so what is shown is not
                        the whole script. Read the file in the snapshot repository for the rest of it.
                      </AlertDescription>
                    </Alert>
                  )}
                  <DiffView
                    original={compare.beforeText ?? ""}
                    modified={compare.afterText ?? ""}
                    language="sql"
                    height={520}
                    data-testid="schema-changes-diff"
                    caption={(
                      <span className="flex min-w-0 items-center gap-2">
                        <Revision label="from" commit={compare.before} />
                        <ArrowRight className="size-3 shrink-0 text-muted-foreground" />
                        <Revision label="to" commit={compare.after} />
                        <span className="min-w-0 truncate font-mono text-muted-foreground">{compare.path}</span>
                      </span>
                    )}
                  />
                </div>
              )}
    </Card>
  );
}

/**
 * The schema history of the managed databases as a browsable tree: database, then schema, then the objects that
 * moved, each expanding to every time a snapshot saw it change. Source-control (scm) flows write this as they
 * run, so the page reads what the catalog already holds and touches no tracked database.
 *
 * The window defaults to the last 24 hours, which on a daily snapshot cadence is the last run: what moved in the
 * estate overnight, with the wider windows a click away when a change has to be traced further back. A change is
 * dated to the snapshot that first SAW it, so that cadence dates a change to the day, not the minute, the DDL ran.
 */
export default function SchemaChangesPage() {
  const [window, setWindow] = useLocalStorageState("schema-changes.window", "1");
  const [changeType, setChangeType] = useState<"all" | ChangeType>("all");
  const [search, setSearch] = useState("");
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set());

  // A deep link into one object's (or one database's) history: `q` seeds the filter and `window` widens it,
  // because a change older than the default 24 hours would otherwise land on an empty page. Seeded once, so
  // the controls stay the user's from the first keystroke on.
  const [searchParams] = useSearchParams();
  useUrlSeed(searchParams.get("q"), setSearch);
  useUrlSeed(searchParams.get("window"), setWindow);

  const since = useMemo(() => sinceIso(window), [window]);

  const changesQuery = useQuery({
    queryKey: ["schema-changes", "tree", window, changeType],
    queryFn: () => fetchSchemaChanges({
      since,
      changeType: changeType === "all" ? undefined : changeType,
    }),
  });

  const summaryQuery = useQuery({
    queryKey: ["schema-changes", "databases", window],
    queryFn: () => schemaChangeApi.databases(since === undefined ? {} : { since }),
  });

  // Free text narrows in the browser over the already-fetched window, so typing never re-queries.
  const term = search.trim().toLowerCase();
  const matching = useMemo(() => {
    const items = changesQuery.data?.items ?? [];
    if (term === "") {
      return items;
    }
    return items.filter((c) =>
      c.name.toLowerCase().includes(term)
      || (c.schema ?? "").toLowerCase().includes(term)
      || c.category.toLowerCase().includes(term)
      || c.database.toLowerCase().includes(term));
  }, [changesQuery.data, term]);

  const tree = useMemo(() => foldTree(matching), [matching]);
  const totals = useMemo(() => ({
    added: matching.filter((c) => c.changeType === "Added").length,
    changed: matching.filter((c) => c.changeType === "Changed").length,
    deleted: matching.filter((c) => c.changeType === "Deleted").length,
  }), [matching]);

  // The selected object, resolved out of the current tree so a filter change that hides it clears the panel.
  const selected = useMemo(() => {
    if (selectedKey === null) {
      return null;
    }
    for (const database of tree) {
      for (const schema of database.schemas) {
        const match = schema.objects.find((o) => `${database.database}/${o.key}` === selectedKey);
        if (match !== undefined) {
          return { database: database.database, branch: match };
        }
      }
    }
    return null;
  }, [tree, selectedKey]);

  const treeState: TreeState = {
    expanded,
    toggle: (id) => setOpen(id, !expanded.has(id)),
    setOpen,
    selectedId: selectedKey,
    select: setSelectedKey,
  };

  function setOpen(id: string, open: boolean) {
    setExpanded((current) => {
      const next = new Set(current);
      if (open) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
  }

  const windowLabel = windows.find((w) => w.value === window)?.label ?? "this window";
  const summary = summaryQuery.data ?? [];
  const nothingEverRecorded = changesQuery.isSuccess
    && changesQuery.data.total === 0
    && window === "all"
    && term === "";

  return (
    <Page data-testid="page-schema-changes">
      <PageHeader
        title="Schema changes"
        subtitle="What the source-control snapshots found changing in the managed databases."
      />

      {/* One card per tracked database: the tally over the window and how long since it last moved. Together
          they answer both "what changed" and "is this database still being snapshotted at all". */}
      {summary.length > 0 && (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          {summary.map((db) => (
            <Card key={db.database} className="gap-0 rounded-lg p-3" data-testid={`schema-changes-db-${db.database}`}>
              <div className="flex items-center gap-1.5">
                <Database className="size-3.5 shrink-0 text-muted-foreground" />
                <span className="truncate font-mono text-[12px] font-medium">{db.database}</span>
              </div>
              <div className="mt-1 flex items-baseline gap-3 font-mono text-sm tabular-nums">
                <span className="text-success">+{db.added}</span>
                <span className="text-info">~{db.changed}</span>
                <span className="text-destructive">-{db.deleted}</span>
              </div>
              <div className="mt-0.5 text-xs text-muted-foreground">
                last change <RelativeTime value={db.lastChangeUtc} />
              </div>
            </Card>
          ))}
        </div>
      )}

      <FilterBar>
        <div className="flex items-center gap-2">
          <Label className="text-xs text-muted-foreground">Window</Label>
          <Select value={window} onValueChange={setWindow}>
            <SelectTrigger className={cn("h-8 w-[150px]", window !== "1" && activeFilterClass)}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {windows.map((w) => <SelectItem key={w.value} value={w.value}>{w.label}</SelectItem>)}
            </SelectContent>
          </Select>
        </div>

        <div className="flex items-center gap-2">
          <Label className="text-xs text-muted-foreground">Change</Label>
          <Select value={changeType} onValueChange={(value) => setChangeType(value as "all" | ChangeType)}>
            <SelectTrigger className={cn("h-8 w-[140px]", changeType !== "all" && activeFilterClass)}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All changes</SelectItem>
              <SelectItem value="Added">Added</SelectItem>
              <SelectItem value="Changed">Changed</SelectItem>
              <SelectItem value="Deleted">Dropped</SelectItem>
            </SelectContent>
          </Select>
        </div>

        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Object, schema, or type"
          label="Search schema changes"
          testId="schema-changes-search"
          className="w-[240px]"
        />

        <div className="ml-auto font-mono text-xs tabular-nums text-muted-foreground">
          <span className="text-success">+{totals.added}</span>
          {" "}
          <span className="text-info">~{totals.changed}</span>
          {" "}
          <span className="text-destructive">-{totals.deleted}</span>
        </div>
      </FilterBar>

      {changesQuery.data?.capped === true && (
        <Alert>
          <AlertDescription>
            Showing the {changesQuery.data.items.length.toLocaleString()} most recent of
            {" "}{changesQuery.data.total.toLocaleString()} changes in this window. Narrow the window or the change
            type to see the rest.
          </AlertDescription>
        </Alert>
      )}

      {changesQuery.isPending
        ? <Skeleton className="h-64 w-full" />
        : nothingEverRecorded
          ? (
            <EmptyState
              title="No schema history yet"
              description="Schema history is written by source-control flows (flowType: scm). Add one per database you want tracked, or wait for the first scheduled snapshot to run."
            />
          )
          : tree.length === 0
            ? (
              <EmptyState
                title="Nothing changed"
                description={`No object was added, changed, or dropped in ${windowLabel.toLowerCase()}.`}
              />
            )
            : (
              <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
                <div role="tree" aria-label="Schema changes" className="rounded-lg border p-1 outline-none">
                  <TreeContext.Provider value={treeState}>
                    {tree.map((database) => (
                      <TreeNode
                        key={database.database}
                        id={`db:${database.database}`}
                        label={(
                          <NodeLabel
                            icon={<Database className="size-4" />}
                            text={database.database}
                            count={database.changeCount}
                          />
                        )}
                      >
                        {database.schemas.map((schema) => (
                          <TreeNode
                            key={schema.schema}
                            id={`db:${database.database}/schema:${schema.schema}`}
                            label={(
                              <NodeLabel
                                icon={<Folder className="size-4" />}
                                text={schema.schema}
                                count={schema.objects.length}
                              />
                            )}
                          >
                            {schema.objects.map((object) => {
                              const kind = dominantChange(object.history);
                              const Icon = changeMeta[kind].icon;
                              return (
                                <TreeNode
                                  key={object.key}
                                  id={`${database.database}/${object.key}`}
                                  label={(
                                    <>
                                      <span className={cn("inline-flex shrink-0", changeMeta[kind].tone)}>
                                        <Icon className="size-4" />
                                      </span>
                                      <span className="min-w-0 flex-1 truncate font-mono text-[12px]">
                                        {object.name}
                                      </span>
                                      <span className="shrink-0 text-[11px] text-muted-foreground">
                                        <RelativeTime value={object.history[0].occurredUtc} />
                                      </span>
                                    </>
                                  )}
                                />
                              );
                            })}
                          </TreeNode>
                        ))}
                      </TreeNode>
                    ))}
                  </TreeContext.Provider>
                </div>

                {selected === null
                  ? (
                    <Card className="flex items-center justify-center gap-0 rounded-lg p-8 text-sm text-muted-foreground">
                      Select an object to see every change the snapshots recorded for it.
                    </Card>
                  )
                  : <ObjectHistory branch={selected.branch} database={selected.database} />}
              </div>
            )}

      {/* Full width rather than beside the tree: a side-by-side of two DDL scripts needs the whole row to read
          without wrapping every line. Keyed by the object so switching selection remounts the editor cleanly. */}
      {selected !== null && (
        <ObjectCompare
          key={`${selected.database}/${selected.branch.key}`}
          branch={selected.branch}
          database={selected.database}
          windowLabel={windowLabel}
          since={since}
        />
      )}
    </Page>
  );
}
