import { useState, type FocusEvent } from "react";
import { ChevronDown, File, FileCode, Folder, FolderOpen, Play } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { TreeContext, TreeNode, type TreeState } from "@/components/Tree";
import type { PipelineSummary, RepoTreeEntry } from "../../api/types";
import { ActiveBadge } from "../../components/StatusBadge";
import { formatBytes } from "../../lib/time";
import { projectOf } from "../repos/project";
import { baseName, buildFolderTree, type FolderNode } from "./folderTree";

/** Whether a pipeline matches a free-text search over the fields a user eyeballs to find a flow: its name, path,
 * kind, and the project (folder) it groups under. `needle` must already be lower-cased and trimmed. */
export function pipelineMatches(p: PipelineSummary, needle: string): boolean {
  return p.name.toLowerCase().includes(needle)
    || p.relativePath.toLowerCase().includes(needle)
    || p.kind.toLowerCase().includes(needle)
    || projectOf(p.relativePath).toLowerCase().includes(needle);
}

/** Group pipelines by their project (repo-root folder), each group's rows sorted by name and the groups sorted by
 * project name. */
export function groupByProject(pipelines: PipelineSummary[]): [string, PipelineSummary[]][] {
  const byProject = new Map<string, PipelineSummary[]>();
  for (const p of [...pipelines].sort((a, b) => a.name.localeCompare(b.name))) {
    const key = projectOf(p.relativePath);
    const group = byProject.get(key);
    if (group) {
      group.push(p);
    } else {
      byProject.set(key, [p]);
    }
  }
  return [...byProject.entries()].sort((a, b) => a[0].localeCompare(b[0]));
}

/**
 * Whether a folder shows its contents when nobody has said otherwise: open when a pipeline sits somewhere under it,
 * shut when it holds only files. That is the rule the page is for. The flows are what a reader came to find, while a
 * folder of data or documents is context they open when they want it, and a repository whose source folder carries a
 * drop-off area cannot unfold into thousands of rows on arrival. A search overrides it and opens everything, so a
 * match is never left hidden behind a collapsed folder.
 */
function opensByDefault(folder: FolderNode, filtered: boolean): boolean {
  return filtered || folder.pipelineCount > 0;
}

/** Every folder id currently expanded, which is the shape the tree primitive reads its expansion state in. */
function expandedIds(node: FolderNode, isOpen: (folder: FolderNode) => boolean, into: Set<string>): Set<string> {
  for (const child of node.folders) {
    if (isOpen(child)) {
      into.add(child.path);
    }
    expandedIds(child, isOpen, into);
  }
  return into;
}

/**
 * One folder's rows: its sub-folders first, then the pipelines and the files sitting directly in it. Recursive, so a
 * repository laid out as `<project>/flows`, `<project>/data/<feed>/<well>` reads as the folders it actually has,
 * at whatever depth it has them, instead of one flat list where the structure survives only inside long path strings.
 */
function FolderRows({ node, onOpen }: { node: FolderNode; onOpen: (pipelineId: string) => void }) {
  return (
    <>
      {node.folders.map((child) => (
        <TreeNode
          key={child.path}
          id={child.path}
          testId="repo-folder"
          label={<FolderLabel folder={child} />}
        >
          <FolderRows node={child} onOpen={onOpen} />
        </TreeNode>
      ))}
      {node.pipelines.map((p) => (
        <TreeNode key={p.id} id={p.id} testId="repo-pipeline" label={<PipelineLabel pipeline={p} />} />
      ))}
      {node.files.map((f) => (
        <TreeNode key={f.path} id={`file:${f.path}`} testId="repo-file" label={<FileLabel file={f} />} />
      ))}
    </>
  );
}

/** A folder row: its name, and what it holds counted through everything beneath it. */
function FolderLabel({ folder }: { folder: FolderNode }) {
  return (
    <>
      <span className="inline-flex shrink-0 text-muted-foreground">
        <Folder className="size-4" />
      </span>
      <span className="min-w-0 flex-1 truncate font-medium">{folder.name}</span>
      {folder.pipelineCount > 0 && (
        <Badge variant="outline" className="h-[18px] shrink-0 px-1.5 text-[11px]">
          {folder.pipelineCount} pipeline{folder.pipelineCount === 1 ? "" : "s"}
        </Badge>
      )}
      {folder.fileCount > 0 && (
        <Badge variant="outline" className="h-[18px] shrink-0 px-1.5 text-[11px] text-muted-foreground">
          {folder.fileCount} file{folder.fileCount === 1 ? "" : "s"}
        </Badge>
      )}
      {/* A folder the repository holds but nothing lives in says so, rather than reading as a broken listing. */}
      {folder.pipelineCount === 0 && folder.fileCount === 0 && (
        <Badge variant="outline" className="h-[18px] shrink-0 px-1.5 text-[11px] text-muted-foreground">empty</Badge>
      )}
    </>
  );
}

/** A flow row: the name it runs under, its kind, its lineage wave, and whether it is active. The folders above it
 * already say where its file is, so the path is not repeated here. */
function PipelineLabel({ pipeline }: { pipeline: PipelineSummary }) {
  return (
    <>
      <span className="inline-flex shrink-0 text-muted-foreground">
        <FileCode className="size-4" />
      </span>
      <span className="min-w-0 flex-1 truncate font-mono text-[12px] font-medium">{pipeline.name}</span>
      <Badge variant="outline" className="h-[18px] shrink-0 px-1.5 text-[11px]">{pipeline.kind}</Badge>
      {pipeline.wave !== -1 && (
        <span className="shrink-0 text-[11px] tabular-nums text-muted-foreground">wave {pipeline.wave}</span>
      )}
      <ActiveBadge active={pipeline.active} />
    </>
  );
}

/** A file row: its name and its size. Everything else about where it is, the folders above it already said. */
function FileLabel({ file }: { file: RepoTreeEntry }) {
  return (
    <>
      <span className="inline-flex shrink-0 text-muted-foreground">
        <File className="size-4" />
      </span>
      <span className="min-w-0 flex-1 truncate font-mono text-[12px] text-muted-foreground">{baseName(file.path)}</span>
      <span className="shrink-0 text-[11px] tabular-nums text-muted-foreground">{formatBytes(file.sizeBytes)}</span>
    </>
  );
}

/** One project (repo-root folder): a collapsible card whose body is the project's folder tree, so its flows and its
 * other files appear under the folders that hold them, with the workbench tree's own keyboard navigation. A project
 * that holds a batch flow (kind: batch) can be run as a unit, so "Run project" triggers that wave-ordered run.
 *
 * `filtered` keys the collapsible so it remounts (and springs back open) when a search starts or clears, surfacing a
 * matching flow in a project the user had collapsed. */
export function ProjectGroup({
  project, rows, files = [], folders = [], repoId, filtered, defaultOpen = false, onOpen, onRunBatch,
}: {
  project: string;
  rows: PipelineSummary[];
  /** The project's files that are NOT registered pipelines (SQL scripts, docs, excluded flows), from the repo's own
   * content listing. Empty where the caller has only the catalog (the pipelines page), which leaves the card with the
   * flows alone, still under the folders that hold them. */
  files?: RepoTreeEntry[];
  /** The project's folder paths as the repository lists them, so a folder holding nothing still shows in the tree. */
  folders?: string[];
  repoId: string;
  filtered: boolean;
  /** Whether the tree starts expanded. Projects stay collapsed so a repo with dozens of them reads as a folder
   * outline the user opens one at a time; a search passes true so every matching project springs open. */
  defaultOpen?: boolean;
  onOpen: (pipelineId: string) => void;
  onRunBatch: (repoId: string, flowName: string) => void;
}) {
  // What the user has explicitly opened or closed, by folder path. Only the overrides are kept, never the whole
  // expansion: the default is derived from the tree on every render, so a folder that arrives with a later query (the
  // repository listing resolves after the catalog's flows) still opens correctly without being re-seeded.
  const [overrides, setOverrides] = useState<Record<string, boolean>>({});
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const inactive = rows.filter((r) => !r.active).length;
  // The project's batch flow (if any): the wave-ordered "run the whole project" entry point.
  const batch = rows.find((r) => r.kind === "batch" && r.active);
  const tree = buildFolderTree(project, rows, files, folders);
  const byId = new Map(rows.map((p) => [p.id, p]));

  const isOpen = (folder: FolderNode) => overrides[folder.path] ?? opensByDefault(folder, filtered);
  const setOpen = (id: string, open: boolean) => setOverrides((current) => ({ ...current, [id]: open }));

  const treeState: TreeState = {
    expanded: expandedIds(tree, isOpen, new Set<string>()),
    setOpen,
    toggle: (id) => setOverrides((current) => {
      const folder = folderAt(tree, id);
      const open = current[id] ?? (folder !== null && opensByDefault(folder, filtered));
      return { ...current, [id]: !open };
    }),
    selectedId,
    // Selecting a flow opens it; selecting a folder only moves the selection, because its own click already toggled it.
    select: (id) => {
      setSelectedId(id);
      if (byId.has(id)) {
        onOpen(id);
      }
    },
  };

  // When the tree itself receives focus (Tab), hand it to the selected row, or the first one.
  const onTreeFocus = (event: FocusEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) {
      return;
    }
    const treeRows = [...event.currentTarget.querySelectorAll<HTMLElement>("[data-tree-row]")];
    (treeRows.find((row) => row.dataset.id === selectedId) ?? treeRows[0])?.focus();
  };

  return (
    <Collapsible key={`${project}:${filtered ? "filtered" : "all"}`} defaultOpen={defaultOpen}>
      <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="repo-project">
        {/* The trigger spans the row up to the action button, so a nested button never sits inside it. */}
        <div className="flex items-center gap-2 pr-2">
          <CollapsibleTrigger className="group flex min-w-0 flex-1 items-center gap-2 px-3 py-2 text-left hover:bg-accent/50">
            <ChevronDown className="size-4 shrink-0 text-muted-foreground transition-transform duration-120 group-data-[state=closed]:-rotate-90" />
            <FolderOpen className="size-4 shrink-0 text-muted-foreground" />
            <span className="truncate text-[13px] font-semibold">{project}</span>
            {/* A folder the sync imported nothing from still belongs in the outline; it says so rather than
                claiming "0 pipelines", which reads as a broken import. */}
            {rows.length === 0 ? (
              <Badge variant="outline" className="text-muted-foreground">no pipelines</Badge>
            ) : (
              <Badge variant="outline">{rows.length} pipeline{rows.length === 1 ? "" : "s"}</Badge>
            )}
            {files.length > 0 && (
              <Badge variant="outline" className="text-muted-foreground">
                {files.length} file{files.length === 1 ? "" : "s"}
              </Badge>
            )}
            {inactive > 0 && (
              <Badge variant="outline" className="border-warning/50 text-warning">
                {inactive} inactive
              </Badge>
            )}
          </CollapsibleTrigger>
          {batch !== undefined && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  variant="outline"
                  size="xs"
                  className="shrink-0"
                  onClick={() => onRunBatch(repoId, batch.name)}
                  data-testid="run-project"
                >
                  <Play />
                  Run project
                </Button>
              </TooltipTrigger>
              <TooltipContent>
                {`Run this project's batch flow '${batch.name}' (members in wave order)`}
              </TooltipContent>
            </Tooltip>
          )}
        </div>
        <CollapsibleContent>
          <div
            role="tree"
            aria-label={`${project} contents`}
            tabIndex={0}
            onFocus={onTreeFocus}
            className="border-t py-1 outline-none"
            data-testid="repo-project-tree"
          >
            <TreeContext.Provider value={treeState}>
              <FolderRows node={tree} onOpen={onOpen} />
            </TreeContext.Provider>
          </div>
        </CollapsibleContent>
      </Card>
    </Collapsible>
  );
}

/** The folder a tree id names, for reading its default open state when the user toggles it the first time. */
function folderAt(node: FolderNode, path: string): FolderNode | null {
  for (const child of node.folders) {
    if (child.path === path) {
      return child;
    }
    const found = folderAt(child, path);
    if (found !== null) {
      return found;
    }
  }
  return null;
}
