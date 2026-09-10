import {
  useCallback,
  useEffect,
  useMemo,
  useState,
  type FocusEvent,
  type ReactNode,
} from "react";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import {
  CircleAlert,
  Cloud,
  Database,
  FileText,
  Folder,
  FolderGit2,
  FolderSymlink,
  Globe,
  HardDrive,
  Layers,
  Loader2,
  Monitor,
  MonitorPlay,
  Network,
  Package,
  Server,
  BookOpen,
} from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { NodeLabel, TreeContext, TreeNode, type TreeState } from "@/components/Tree";
import { lineageApi, repoApi } from "../../api/endpoints";
import type { FileNode, FileOriginKind, LineageObject, PagedResult, PipelineSummary, Repo, SchemaKindCount, Subscriber } from "../../api/types";
import { fetchAllPipelines } from "../pipelines/fetchAllPipelines";
import { compareKinds, metaForKind } from "./kindMeta";
import { encodeNodeId, UNRESOLVED_LABEL, decodeNodeId, type CatalogNode } from "./nodeIds";

const LEAF_PAGE_SIZE = 200;

const lower = (value: string) => value.toLowerCase();
const byNameCi = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "base" });

// ---- Databases branch (server/db > schema > kind > object) -----------------------------------------------

interface DatabaseBranch { database: string | null; objectCount: number; schemas: SchemaBranch[] }
interface SchemaBranch { schema: string | null; objectCount: number; kinds: KindCount[] }
interface KindCount { kind: string; objectCount: number }

/** Folds the flat per-kind rows into database > schema > kind, merging connection-reference aliases so one
 * database reached via two refs is one node. Files and subscribers are already absent: they belong to no
 * database and the schema endpoint excludes them, so every client sees the database hierarchy alone. */
function foldDatabases(rows: SchemaKindCount[]): DatabaseBranch[] {
  const databases = new Map<string | null, DatabaseBranch>();
  for (const row of rows) {
    let database = databases.get(row.database);
    if (database === undefined) {
      database = { database: row.database, objectCount: 0, schemas: [] };
      databases.set(row.database, database);
    }
    database.objectCount += row.objectCount;
    let schema = database.schemas.find((s) => s.schema === row.schema);
    if (schema === undefined) {
      schema = { schema: row.schema, objectCount: 0, kinds: [] };
      database.schemas.push(schema);
    }
    schema.objectCount += row.objectCount;
    const kind = schema.kinds.find((k) => k.kind === row.kind);
    if (kind === undefined) {
      schema.kinds.push({ kind: row.kind, objectCount: row.objectCount });
    } else {
      kind.objectCount += row.objectCount;
    }
  }

  const branches = [...databases.values()];
  for (const database of branches) {
    database.schemas.sort((a, b) => byNameCi(a.schema ?? "", b.schema ?? ""));
    for (const schema of database.schemas) {
      schema.kinds.sort((a, b) => compareKinds(a.kind, b.kind));
    }
  }
  branches.sort((a, b) => byNameCi(a.database ?? "", b.database ?? ""));
  return branches;
}

/** Paged object leaves under a database kind folder, lazy-loaded on expand. */
function PagedObjectLeaves({
  parentId, queryKey, fetchPage,
}: {
  parentId: string;
  queryKey: readonly unknown[];
  fetchPage: (page: number) => Promise<PagedResult<LineageObject>>;
}) {
  const objects = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam }) => fetchPage(pageParam),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
  });

  if (objects.isPending) {
    return <TreeNode id={`${parentId}#loading`} disabled label={<NodeLabel text="Loading…" />} />;
  }
  if (objects.isError) {
    return (
      <TreeNode id={`${parentId}#error`} disabled
        label={<NodeLabel text={`Could not load objects: ${String(objects.error)}`} />} />
    );
  }

  const rows = objects.data.pages.flatMap((page) => page.items);
  const total = objects.data.pages[0]?.total ?? 0;
  const remaining = total - rows.length;
  return (
    <>
      {rows.length === 0 && <TreeNode id={`${parentId}#empty`} disabled label={<NodeLabel text="No objects" />} />}
      {rows.map((row) => (
        <TreeNode
          key={row.key}
          id={encodeNodeId({ type: "object", objectKey: row.key })}
          label={<NodeLabel icon={metaForKind(row.kind).icon} text={row.name} />}
        />
      ))}
      {remaining > 0 && <LoadMore parentId={parentId} remaining={remaining} loading={objects.isFetchingNextPage} onMore={() => void objects.fetchNextPage()} />}
    </>
  );
}

function LoadMore({ parentId, remaining, loading, onMore }: { parentId: string; remaining: number; loading: boolean; onMore: () => void }) {
  return (
    <TreeNode
      id={`${parentId}#more`}
      disabled
      label={(
        <Button
          variant="ghost"
          size="xs"
          disabled={loading}
          className="text-primary hover:text-primary"
          onClick={(event) => { event.stopPropagation(); onMore(); }}
        >
          {loading && <Loader2 className="animate-spin" />}
          {`Load more (${remaining} remaining)`}
        </Button>
      )}
    />
  );
}

// ---- Sources branch (file origins: origin > container > folder > file) -----------------------------------

interface FolderTrie {
  folders: Map<string, FolderTrie>;
  files: { key: string; name: string }[];
  count: number;
}

interface OriginGroup {
  origin: string;
  count: number;
  containers: Map<string, FolderTrie>;
  root: FolderTrie;
}

interface ProviderGroup {
  kind: FileOriginKind;
  count: number;
  origins: OriginGroup[];
}

const newTrie = (): FolderTrie => ({ folders: new Map(), files: [], count: 0 });

/** The provider (the logical parent above a storage account): its label, icon, order, and whether it has an
 * origin level. Local files have no distinct origin (one filesystem), so they render folders directly under
 * the provider; every cloud/server provider groups its accounts/buckets/hosts as origins first. */
interface ProviderMeta { label: string; icon: ReactNode; order: number; hasOrigin: boolean }

const PROVIDER_META: Record<FileOriginKind, ProviderMeta> = {
  AzureStorage: { label: "Microsoft Azure", icon: <Cloud className="size-4" />, order: 0, hasOrigin: true },
  AmazonS3: { label: "Amazon S3", icon: <Cloud className="size-4" />, order: 1, hasOrigin: true },
  GoogleCloud: { label: "Google Cloud", icon: <Cloud className="size-4" />, order: 2, hasOrigin: true },
  Sftp: { label: "SFTP / FTP servers", icon: <Server className="size-4" />, order: 3, hasOrigin: true },
  NetworkShare: { label: "Network shares", icon: <FolderSymlink className="size-4" />, order: 4, hasOrigin: true },
  Local: { label: "Local files", icon: <Monitor className="size-4" />, order: 5, hasOrigin: false },
  Other: { label: "Other sources", icon: <Globe className="size-4" />, order: 6, hasOrigin: true },
};

/** Folds the flat file-node list into provider > origin > (container) > folder trie > file, with recursive
 * counts at every level. */
function foldFileProviders(files: FileNode[]): ProviderGroup[] {
  const providers = new Map<FileOriginKind, Map<string, OriginGroup>>();
  for (const file of files) {
    let origins = providers.get(file.originKind);
    if (origins === undefined) {
      origins = new Map();
      providers.set(file.originKind, origins);
    }
    let group = origins.get(file.origin);
    if (group === undefined) {
      group = { origin: file.origin, count: 0, containers: new Map(), root: newTrie() };
      origins.set(file.origin, group);
    }
    group.count++;

    let trie: FolderTrie;
    if (file.container !== null) {
      trie = group.containers.get(file.container) ?? newTrie();
      group.containers.set(file.container, trie);
    } else {
      trie = group.root;
    }

    trie.count++;
    for (const segment of file.path ? file.path.split("/") : []) {
      let child = trie.folders.get(segment);
      if (child === undefined) {
        child = newTrie();
        trie.folders.set(segment, child);
      }
      trie = child;
      trie.count++;
    }
    trie.files.push({ key: file.key, name: file.name });
  }

  return [...providers.entries()]
    .map(([kind, origins]) => {
      const originList = [...origins.values()].sort((a, b) => byNameCi(a.origin, b.origin));
      return { kind, count: originList.reduce((sum, o) => sum + o.count, 0), origins: originList };
    })
    .sort((a, b) => PROVIDER_META[a.kind].order - PROVIDER_META[b.kind].order);
}

/** Renders a folder trie's subfolders (recursively) then its files, under the given provider/origin/container. */
function renderTrie(provider: string, origin: string, container: string | null, parentPath: string, trie: FolderTrie): ReactNode[] {
  const folders = [...trie.folders.entries()]
    .sort(([a], [b]) => byNameCi(a, b))
    .map(([name, child]) => {
      const path = parentPath ? `${parentPath}/${name}` : name;
      const id = encodeNodeId({ type: "folder", provider, origin, container, path });
      return (
        <TreeNode key={id} id={id} label={<NodeLabel icon={<Folder className="size-4" />} text={name} count={child.count} />}>
          {renderTrie(provider, origin, container, path, child)}
        </TreeNode>
      );
    });
  const files = [...trie.files]
    .sort((a, b) => byNameCi(a.name, b.name))
    .map((file) => (
      <TreeNode
        key={file.key}
        id={encodeNodeId({ type: "object", objectKey: file.key })}
        label={<NodeLabel icon={<FileText className="size-4" />} text={file.name} />}
      />
    ));
  return [...folders, ...files];
}

function OriginNode({ provider, group }: { provider: FileOriginKind; group: OriginGroup }) {
  const originId = encodeNodeId({ type: "origin", provider, origin: group.origin });
  const containers = [...group.containers.entries()].sort(([a], [b]) => byNameCi(a, b));
  return (
    <TreeNode id={originId} label={<NodeLabel icon={<HardDrive className="size-4" />} text={group.origin} count={group.count} />}>
      {containers.map(([name, trie]) => {
        const id = encodeNodeId({ type: "container", provider, origin: group.origin, container: name });
        return (
          <TreeNode key={id} id={id} label={<NodeLabel icon={<Package className="size-4" />} text={name} count={trie.count} />}>
            {renderTrie(provider, group.origin, name, "", trie)}
          </TreeNode>
        );
      })}
      {renderTrie(provider, group.origin, null, "", group.root)}
    </TreeNode>
  );
}

function ProviderNode({ group }: { group: ProviderGroup }) {
  const meta = PROVIDER_META[group.kind];
  const providerId = encodeNodeId({ type: "provider", provider: group.kind });
  return (
    <TreeNode id={providerId} label={<NodeLabel icon={meta.icon} text={meta.label} count={group.count} />}>
      {meta.hasOrigin
        ? group.origins.map((o) => <OriginNode key={encodeNodeId({ type: "origin", provider: group.kind, origin: o.origin })} provider={group.kind} group={o} />)
        // Local files have no origin level: render the filesystem's folder tree directly under the provider.
        : group.origins.flatMap((o) => renderTrie(group.kind, o.origin, null, "", o.root))}
    </TreeNode>
  );
}

/** The expand chain that reveals a file leaf: its provider, origin, container, and every folder down to it. */
function fileAncestorIds(file: FileNode): string[] {
  const provider = file.originKind;
  const ids = [
    encodeNodeId({ type: "sourcesRoot" }),
    encodeNodeId({ type: "provider", provider }),
  ];
  if (PROVIDER_META[provider].hasOrigin) {
    ids.push(encodeNodeId({ type: "origin", provider, origin: file.origin }));
  }
  if (file.container !== null) {
    ids.push(encodeNodeId({ type: "container", provider, origin: file.origin, container: file.container }));
  }
  if (file.path) {
    let acc = "";
    for (const segment of file.path.split("/")) {
      acc = acc ? `${acc}/${segment}` : segment;
      ids.push(encodeNodeId({ type: "folder", provider, origin: file.origin, container: file.container, path: acc }));
    }
  }
  return ids;
}

// ---- Flows branch (repo > repository folder > batch > flow) ----------------------------------------------

interface FlowLeaf { id: string; name: string; kind: string; repoId: string }

/** A folder in a repo's directory structure: its subfolders, the batches of the flows sitting directly in it
 * (batch is the flow's declared YAML attribute), and a recursive flow count. */
interface FlowFolder {
  folders: Map<string, FlowFolder>;
  batches: Map<string, FlowLeaf[]>;
  count: number;
}

const newFlowFolder = (): FlowFolder => ({ folders: new Map(), batches: new Map(), count: 0 });

/** The directory segments of a flow's repo-relative path (the file name dropped): the repository folder
 * structure that organizes the flows. */
function flowFolderSegments(relativePath: string): string[] {
  const normalized = relativePath.replace(/\\/g, "/");
  const slash = normalized.lastIndexOf("/");
  const dir = slash < 0 ? "" : normalized.slice(0, slash);
  return dir.split("/").filter((segment) => segment.length > 0);
}

/** Folds every flow into repo > folder tree > batch > flow, with recursive flow counts at each folder. */
function foldFlows(pipelines: PipelineSummary[]): Map<string, FlowFolder> {
  const repos = new Map<string, FlowFolder>();
  for (const flow of pipelines) {
    let root = repos.get(flow.repoId);
    if (root === undefined) {
      root = newFlowFolder();
      repos.set(flow.repoId, root);
    }
    let folder = root;
    folder.count++;
    for (const segment of flowFolderSegments(flow.relativePath)) {
      let child = folder.folders.get(segment);
      if (child === undefined) {
        child = newFlowFolder();
        folder.folders.set(segment, child);
      }
      folder = child;
      folder.count++;
    }
    const batch = flow.batch ?? "default";
    const list = folder.batches.get(batch) ?? [];
    list.push({ id: flow.id, name: flow.name, kind: flow.kind, repoId: flow.repoId });
    folder.batches.set(batch, list);
  }
  return repos;
}

/** Renders a repo folder's subfolders (recursively) then its batch groups (each with its flow leaves). */
function renderFlowFolder(repoId: string, path: string, folder: FlowFolder): ReactNode[] {
  const folders = [...folder.folders.entries()]
    .sort(([a], [b]) => byNameCi(a, b))
    .map(([name, child]) => {
      const childPath = path ? `${path}/${name}` : name;
      const id = encodeNodeId({ type: "flowFolder", repoId, path: childPath });
      return (
        <TreeNode key={id} id={id} label={<NodeLabel icon={<Folder className="size-4" />} text={name} count={child.count} />}>
          {renderFlowFolder(repoId, childPath, child)}
        </TreeNode>
      );
    });
  const batches = [...folder.batches.entries()]
    .sort(([a], [b]) => byNameCi(a, b))
    .map(([batch, flows]) => {
      const id = encodeNodeId({ type: "batch", repoId, path, batch });
      return (
        <TreeNode key={id} id={id} label={<NodeLabel icon={<Layers className="size-4" />} text={batch} count={flows.length} />}>
          {[...flows].sort((a, b) => byNameCi(a.name, b.name)).map((flow) => (
            <TreeNode
              key={flow.id}
              id={encodeNodeId({ type: "flow", repoId: flow.repoId, pipelineId: flow.id })}
              label={<NodeLabel icon={<Network className="size-4" />} text={flow.name} badge={flow.kind} />}
            />
          ))}
        </TreeNode>
      );
    });
  return [...folders, ...batches];
}

/** Every expandable id under a repo's folder tree (folders + batches), for revealing matches while filtering. */
function collectFlowIds(repoId: string, path: string, folder: FlowFolder): string[] {
  const ids: string[] = [];
  for (const [name, child] of folder.folders) {
    const childPath = path ? `${path}/${name}` : name;
    ids.push(encodeNodeId({ type: "flowFolder", repoId, path: childPath }), ...collectFlowIds(repoId, childPath, child));
  }
  for (const batch of folder.batches.keys()) {
    ids.push(encodeNodeId({ type: "batch", repoId, path, batch }));
  }
  return ids;
}

// ---- Cross-branch object search --------------------------------------------------------------------------

/** The flat "Object matches" list under the filter box: server-side name search across every object leaf
 * (tables, views, AND files, whose name is their path), so a leaf not yet expanded in the tree is findable. */
function ObjectMatches({ filter, onSelect }: { filter: string; onSelect: (id: string) => void }) {
  const matches = useQuery({
    queryKey: ["catalog-object-matches", filter],
    queryFn: () => lineageApi.objects({ name: filter, pageSize: 20 }),
  });

  if (matches.isPending) {
    return <Skeleton className="h-8 w-full" data-testid="catalog-matches-loading" />;
  }
  if (matches.isError) {
    return (
      <Alert variant="destructive">
        <CircleAlert />
        <AlertTitle>Object search failed</AlertTitle>
        <AlertDescription>{String(matches.error)}</AlertDescription>
      </Alert>
    );
  }
  if (matches.data.items.length === 0) {
    return null;
  }
  return (
    <div data-testid="catalog-object-matches">
      <div className="px-1 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Object matches</div>
      <div className="mt-1 flex flex-col">
        {matches.data.items.map((row) => (
          <button
            key={row.key}
            type="button"
            // A subscriber is in the object registry (it is a graph node) but is not a database object: send it
            // to its own node so the panel shows what it consumes, not an empty table overview.
            onClick={() => onSelect(row.kind === "Subscriber"
              ? encodeNodeId({ type: "subscriber", key: row.key })
              : encodeNodeId({ type: "object", objectKey: row.key }))}
            className="flex w-full items-center gap-2 rounded-md px-2 py-1 text-left hover:bg-accent/60"
          >
            <span className="inline-flex shrink-0 text-muted-foreground">{metaForKind(row.kind).icon}</span>
            <span className="min-w-0">
              <span className="block truncate font-mono text-[12px]">{row.name}</span>
              <span className="block truncate text-xs text-muted-foreground">
                {[row.database, row.schema].filter((part) => part !== null).join(".") || row.serverRef}
              </span>
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}

/** Local debounce so the server-side object search fires after typing pauses, not per keystroke. */
function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

// ---- The tree --------------------------------------------------------------------------------------------

export interface CatalogTreeProps {
  selectedId: string | null;
  onSelect: (id: string) => void;
  /** Ids to start expanded (the deep-linked node's ancestor chain plus the roots). */
  initialExpanded: string[];
}

/**
 * The explorer tree over the catalog, in three perspectives: Databases (the SQL estate: database > schema >
 * kind > object, merged across connection references, files excluded), Sources (the file estate by canonical
 * origin: storage account / SFTP server / filesystem > container > folder > file), and Flows (the pipeline
 * estate: repo > batch > flow). The Databases and Sources skeletons arrive in bounded calls and fold client
 * side; database object leaves and flow leaves load lazily on expand. The filter narrows every branch client
 * side and searches every object leaf server side.
 */
export function CatalogTree({ selectedId, onSelect, initialExpanded }: CatalogTreeProps) {
  const [expanded, setExpanded] = useState<string[]>(initialExpanded);
  const [filter, setFilter] = useLocalStorageState("sqlflow.filters.catalog.tree", "");
  const debouncedFilter = useDebounced(filter.trim(), 350);
  const needle = lower(debouncedFilter);

  const schemaKinds = useQuery({ queryKey: ["catalog-schema-kinds"], queryFn: () => lineageApi.schemaKinds() });
  const fileTree = useQuery({ queryKey: ["catalog-file-tree"], queryFn: () => lineageApi.fileTree() });
  const repos = useQuery({ queryKey: ["catalog-repos"], queryFn: () => repoApi.list({ pageSize: 200 }) });
  // Every flow, through the shared sweep: the folder tree needs the whole set to render, and flows are bounded by
  // the estate's file count, not its data volume.
  const pipelines = useQuery({
    queryKey: ["catalog-all-pipelines"],
    queryFn: async () => (await fetchAllPipelines({})).items,
  });

  const subscribers = useQuery({ queryKey: ["catalog-subscribers"], queryFn: () => lineageApi.subscribers() });

  const databases = useMemo(() => foldDatabases(schemaKinds.data ?? []), [schemaKinds.data]);

  // Subscribers group by the consuming tool (PowerBI, Tableau, Excel, ...), the one grouping a person browsing
  // "who uses our data" actually reaches for. The filter narrows on the subscriber's name and its owner.
  const subscriberTypes = useMemo(() => {
    const rows = (subscribers.data ?? []).filter((row) => needle === ""
      || lower(row.name).includes(needle)
      || lower(row.owner ?? "").includes(needle)
      || lower(row.type).includes(needle));
    const byType = new Map<string, Subscriber[]>();
    for (const row of rows) {
      const group = byType.get(row.type);
      if (group === undefined) {
        byType.set(row.type, [row]);
      } else {
        group.push(row);
      }
    }
    return [...byType.entries()]
      .map(([type, group]) => ({ type, rows: group.sort((a, b) => a.name.localeCompare(b.name)) }))
      .sort((a, b) => a.type.localeCompare(b.type));
  }, [subscribers.data, needle]);

  const filteredDatabases = useMemo(() => {
    if (needle === "") {
      return databases;
    }
    const match = (label: string | null) => lower(label ?? UNRESOLVED_LABEL).includes(needle);
    return databases
      .map((database) => {
        if (match(database.database)) {
          return database;
        }
        const kept = database.schemas.filter((schema) => match(schema.schema));
        return kept.length > 0 ? { ...database, schemas: kept } : null;
      })
      .filter((database): database is DatabaseBranch => database !== null);
  }, [databases, needle]);

  // Filter the flat file list (by origin/container/path/name) BEFORE folding, so a matched leaf keeps its
  // whole provider/origin/container/folder chain.
  const fileProviders = useMemo(() => {
    const files = fileTree.data ?? [];
    const kept = needle === ""
      ? files
      : files.filter((f) =>
        lower(f.origin).includes(needle) || lower(f.container ?? "").includes(needle)
        || lower(f.path ?? "").includes(needle) || lower(f.name).includes(needle));
    return foldFileProviders(kept);
  }, [fileTree.data, needle]);

  const repoList: Repo[] = useMemo(() => (repos.data?.items ?? []).slice().sort((a, b) => byNameCi(a.name, b.name)), [repos.data]);

  // Filter the flat flow list (by repo name, folder path, batch, or flow name) BEFORE folding, so a matched
  // flow keeps its whole repo/folder/batch chain.
  const flowsByRepo = useMemo(() => {
    const all = pipelines.data ?? [];
    const repoNameById = new Map((repos.data?.items ?? []).map((r) => [r.id, r.name]));
    const kept = needle === ""
      ? all
      : all.filter((p) =>
        lower(p.name).includes(needle) || lower(p.relativePath).includes(needle)
        || lower(p.batch ?? "default").includes(needle) || lower(repoNameById.get(p.repoId) ?? "").includes(needle));
    return foldFlows(kept);
  }, [pipelines.data, repos.data, needle]);

  // Reveal the selected file leaf (and, while filtering, the matched upper levels) without hand-expanding.
  const effectiveExpanded = useMemo(() => {
    const open = new Set(expanded);
    const selected = selectedId === null ? null : decodeNodeId(selectedId);
    if (selected?.type === "object") {
      const file = (fileTree.data ?? []).find((f) => f.key === selected.objectKey);
      if (file) {
        for (const id of fileAncestorIds(file)) {
          open.add(id);
        }
      }
    }
    if (needle !== "") {
      open.add(encodeNodeId({ type: "databasesRoot" }));
      open.add(encodeNodeId({ type: "sourcesRoot" }));
      open.add(encodeNodeId({ type: "flowsRoot" }));
      open.add(encodeNodeId({ type: "subscribersRoot" }));
      for (const database of filteredDatabases) {
        open.add(encodeNodeId({ type: "database", database: database.database }));
      }
      for (const provider of fileProviders) {
        open.add(encodeNodeId({ type: "provider", provider: provider.kind }));
        if (PROVIDER_META[provider.kind].hasOrigin) {
          for (const origin of provider.origins) {
            open.add(encodeNodeId({ type: "origin", provider: provider.kind, origin: origin.origin }));
          }
        }
      }
      for (const [repoId, root] of flowsByRepo) {
        open.add(encodeNodeId({ type: "repo", repoId }));
        for (const id of collectFlowIds(repoId, "", root)) {
          open.add(id);
        }
      }
    }
    return open;
  }, [expanded, needle, selectedId, fileTree.data, filteredDatabases, fileProviders, flowsByRepo]);

  const setOpen = useCallback((id: string, open: boolean) => {
    setExpanded((current) => {
      if (open) {
        return current.includes(id) ? current : [...current, id];
      }
      return current.filter((item) => item !== id);
    });
  }, []);

  // When the container itself receives focus (Tab), hand it to the selected row, or the first row.
  const onTreeFocus = (event: FocusEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) {
      return;
    }
    const rows = [...event.currentTarget.querySelectorAll<HTMLElement>("[data-tree-row]")];
    (rows.find((row) => row.dataset.id === selectedId) ?? rows[0])?.focus();
  };

  const skeletonError = [schemaKinds, fileTree, repos, pipelines, subscribers].find((query) => query.isError);
  if (skeletonError !== undefined) {
    return (
      <Alert variant="destructive" data-testid="catalog-tree-error">
        <CircleAlert />
        <AlertTitle>The catalog tree could not load</AlertTitle>
        <AlertDescription>
          <p>{String(skeletonError.error)}</p>
          <Button variant="outline" size="xs" onClick={() => void skeletonError.refetch()}>Retry</Button>
        </AlertDescription>
      </Alert>
    );
  }
  if (schemaKinds.isPending || fileTree.isPending || repos.isPending || pipelines.isPending || subscribers.isPending) {
    return (
      <div className="flex flex-col gap-2" data-testid="catalog-tree-loading">
        {Array.from({ length: 8 }, (_, i) => <Skeleton key={i} className="h-7 w-full" />)}
      </div>
    );
  }

  const treeState: TreeState = {
    expanded: effectiveExpanded,
    setOpen,
    toggle: (id) => setOpen(id, !effectiveExpanded.has(id)),
    selectedId,
    select: onSelect,
  };

  const totalDbObjects = databases.reduce((sum, database) => sum + database.objectCount, 0);
  const totalFiles = (fileTree.data ?? []).length;
  const totalFlows = (pipelines.data ?? []).length;
  const totalSubscribers = (subscribers.data ?? []).length;

  return (
    <div className="flex flex-col gap-3" data-testid="catalog-tree">
      <Input
        className="h-8"
        placeholder="Filter the tree or search objects"
        aria-label="Filter the tree or search objects"
        value={filter}
        onChange={(event) => setFilter(event.target.value)}
        data-testid="catalog-filter"
      />
      {debouncedFilter.length >= 2 && <ObjectMatches filter={debouncedFilter} onSelect={onSelect} />}

      <div role="tree" aria-label="Catalog tree" tabIndex={0} onFocus={onTreeFocus} className="outline-none">
        <TreeContext.Provider value={treeState}>
          {/* Databases */}
          <TreeNode
            id={encodeNodeId({ type: "databasesRoot" })}
            label={<NodeLabel icon={<Database className="size-4" />} text="Databases" count={totalDbObjects} />}
          >
            {filteredDatabases.length === 0 && (
              <TreeNode id="dbs#empty" disabled label={<NodeLabel text="No database objects in the catalog yet" />} />
            )}
            {filteredDatabases.map((database) => {
              const databaseId = encodeNodeId({ type: "database", database: database.database });
              return (
                <TreeNode key={databaseId} id={databaseId}
                  label={<NodeLabel icon={<Database className="size-4" />} text={database.database ?? UNRESOLVED_LABEL} count={database.objectCount} />}>
                  {database.schemas.map((schema) => {
                    const schemaId = encodeNodeId({ type: "schema", database: database.database, schema: schema.schema });
                    return (
                      <TreeNode key={schemaId} id={schemaId}
                        label={<NodeLabel icon={<BookOpen className="size-4" />} text={schema.schema ?? UNRESOLVED_LABEL} count={schema.objectCount} />}>
                        {schema.kinds.map((kindRow) => {
                          const kindNode: CatalogNode = { type: "kind", database: database.database, schema: schema.schema, kind: kindRow.kind };
                          const kindId = encodeNodeId(kindNode);
                          const meta = metaForKind(kindRow.kind);
                          return (
                            <TreeNode key={kindId} id={kindId} label={<NodeLabel icon={meta.icon} text={meta.plural} count={kindRow.objectCount} />}>
                              <PagedObjectLeaves
                                parentId={kindId}
                                queryKey={["catalog-objects", database.database, schema.schema, kindRow.kind]}
                                fetchPage={(page) => lineageApi.objects({
                                  database: database.database ?? undefined,
                                  schema: schema.schema ?? undefined,
                                  kind: kindRow.kind,
                                  page,
                                  pageSize: LEAF_PAGE_SIZE,
                                })}
                              />
                            </TreeNode>
                          );
                        })}
                      </TreeNode>
                    );
                  })}
                </TreeNode>
              );
            })}
          </TreeNode>

          {/* Sources (file origins, grouped by provider: Azure / Amazon S3 / Google Cloud / SFTP / ... / Local) */}
          <TreeNode
            id={encodeNodeId({ type: "sourcesRoot" })}
            label={<NodeLabel icon={<Cloud className="size-4" />} text="Sources" count={totalFiles} />}
          >
            {fileProviders.length === 0 && (
              <TreeNode id="sources#empty" disabled label={<NodeLabel text="No file sources in the catalog yet" />} />
            )}
            {fileProviders.map((group) => <ProviderNode key={encodeNodeId({ type: "provider", provider: group.kind })} group={group} />)}
          </TreeNode>

          {/* Flows (repo > repository folder > batch > flow) */}
          <TreeNode
            id={encodeNodeId({ type: "flowsRoot" })}
            label={<NodeLabel icon={<Network className="size-4" />} text="Flows" count={totalFlows} />}
          >
            {flowsByRepo.size === 0 && (
              <TreeNode id="flows#empty" disabled label={<NodeLabel text="No flows in the catalog yet" />} />
            )}
            {repoList.map((repo) => {
              const root = flowsByRepo.get(repo.id);
              if (root === undefined) {
                return null;
              }
              const repoId = encodeNodeId({ type: "repo", repoId: repo.id });
              return (
                <TreeNode key={repoId} id={repoId}
                  label={<NodeLabel icon={<FolderGit2 className="size-4" />} text={repo.name} count={root.count} />}>
                  {renderFlowFolder(repo.id, "", root)}
                </TreeNode>
              );
            })}
          </TreeNode>

          {/* Subscribers (the consumption estate: consuming tool > subscriber) */}
          <TreeNode
            id={encodeNodeId({ type: "subscribersRoot" })}
            label={<NodeLabel icon={<MonitorPlay className="size-4" />} text="Subscribers" count={totalSubscribers} />}
          >
            {subscriberTypes.length === 0 && (
              <TreeNode
                id="subs#empty"
                disabled
                label={<NodeLabel text="No subscribers declared; add a subscribers.yaml to a repo" />}
              />
            )}
            {subscriberTypes.map((group) => {
              const typeId = encodeNodeId({ type: "subscriberType", subscriberType: group.type });
              return (
                <TreeNode key={typeId} id={typeId}
                  label={<NodeLabel icon={<MonitorPlay className="size-4" />} text={group.type} count={group.rows.length} />}>
                  {group.rows.map((row) => (
                    <TreeNode
                      key={encodeNodeId({ type: "subscriber", key: row.key })}
                      id={encodeNodeId({ type: "subscriber", key: row.key })}
                      label={<NodeLabel icon={<MonitorPlay className="size-4" />} text={row.name} count={row.objectCount} />}
                    />
                  ))}
                </TreeNode>
              );
            })}
          </TreeNode>
        </TreeContext.Provider>
      </div>
    </div>
  );
}
