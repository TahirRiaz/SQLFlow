import { ChevronDown, File, Folder, Play } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Table, TableBody, TableCell, TableRow } from "@/components/ui/table";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { PipelineSummary, RepoTreeEntry } from "../../api/types";
import { ActiveBadge } from "../../components/StatusBadge";
import { formatBytes } from "../../lib/time";
import { projectOf } from "../repos/project";

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

/** One project (repo-root folder) of pipelines: a collapsible card with the flows in a table, and under them the
 * folder's other files when the caller read the repository itself. A project that holds a batch flow (kind: batch)
 * can be run as a unit, so "Run project" triggers that wave-ordered run.
 *
 * `filtered` keys the collapsible so it remounts (and springs back open) when a search starts or clears, surfacing a
 * matching flow in a project the user had collapsed. */
export function ProjectGroup({
  project, rows, files = [], repoId, filtered, defaultOpen = false, onOpen, onRunBatch,
}: {
  project: string;
  rows: PipelineSummary[];
  /** The folder's files that are NOT registered pipelines (SQL scripts, docs, excluded flows), from the repo's own
   * content listing. Empty where the caller has only the catalog (the pipelines page), which leaves the card exactly
   * as it was: the flows and nothing else. */
  files?: RepoTreeEntry[];
  repoId: string;
  filtered: boolean;
  /** Whether the flow table starts expanded. Projects stay collapsed so a repo with dozens of them reads as a
   * folder outline the user opens one at a time; a search passes true so every matching project springs open. */
  defaultOpen?: boolean;
  onOpen: (pipelineId: string) => void;
  onRunBatch: (repoId: string, flowName: string) => void;
}) {
  const inactive = rows.filter((r) => !r.active).length;
  // The project's batch flow (if any): the wave-ordered "run the whole project" entry point.
  const batch = rows.find((r) => r.kind === "batch" && r.active);
  return (
    <Collapsible key={`${project}:${filtered ? "filtered" : "all"}`} defaultOpen={defaultOpen}>
      <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="repo-project">
        {/* The trigger spans the row up to the action button, so a nested button never sits inside it. */}
        <div className="flex items-center gap-2 pr-2">
          <CollapsibleTrigger className="group flex min-w-0 flex-1 items-center gap-2 px-3 py-2 text-left hover:bg-accent/50">
            <ChevronDown className="size-4 shrink-0 text-muted-foreground transition-transform duration-120 group-data-[state=closed]:-rotate-90" />
            <Folder className="size-4 shrink-0 text-muted-foreground" />
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
          <Table>
            <TableBody>
              {rows.map((p) => (
                <TableRow
                  key={p.id}
                  className="cursor-pointer hover:bg-accent/50"
                  onClick={() => onOpen(p.id)}
                  data-testid="table-row"
                >
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <span className="font-mono text-[12px] font-medium">{p.name}</span>
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <Badge variant="outline">{p.kind}</Badge>
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                    {p.wave === -1 ? "-" : `wave ${p.wave}`}
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <ActiveBadge active={p.active} />
                  </TableCell>
                  <TableCell className="px-3 py-1.5">
                    <span className="font-mono text-xs text-muted-foreground">{p.relativePath}</span>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          {files.length > 0 && (
            <div className="border-t" data-testid="project-files">
              <Table>
                <TableBody>
                  {files.map((f) => (
                    <TableRow key={f.path} data-testid="project-file-row">
                      <TableCell className="w-6 px-3 py-1.5">
                        <File className="size-3.5 text-muted-foreground" />
                      </TableCell>
                      <TableCell className="px-3 py-1.5">
                        <span className="font-mono text-xs text-muted-foreground">{f.path}</span>
                      </TableCell>
                      <TableCell className="whitespace-nowrap px-3 py-1.5 text-right text-xs text-muted-foreground">
                        {formatBytes(f.sizeBytes)}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </CollapsibleContent>
      </Card>
    </Collapsible>
  );
}
