import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  ChevronRight,
  FileText,
  Layers,
  Loader2,
  MonitorPlay,
  Network,
  Split,
  Table2,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { lineageApi } from "../api/endpoints";

/**
 * What a search hit can open in the lineage graph. An `object` target resolves its repo(s) on demand (the same
 * physical object can appear in several repos' graphs, so the user picks); a `node` target already knows its repo
 * (a flow), so it opens directly; a `file` target resolves the pipeline(s) that ingest the file by matching its
 * name/path against the flow source specs in the catalog (so a file jumps to the flow that feeds it).
 */
export type LineageJumpTarget =
  | { kind: "object"; objectKey: string; objectKind: string; label: string; sublabel?: string }
  | { kind: "node"; repoId: string; repoName: string; focusId: string; label: string; sublabel?: string }
  | { kind: "file"; filePath: string; label: string; sublabel?: string }
  | { kind: "subscriber"; subscriberKey: string; label: string; sublabel?: string };

function TargetIcon({ target }: { target: LineageJumpTarget }) {
  if (target.kind === "node") {
    return <Network className="size-4 text-primary" />;
  }
  if (target.kind === "subscriber") {
    return <MonitorPlay className="size-4 text-primary" />;
  }
  if (target.kind === "file") {
    return <FileText className="size-4 text-primary" />;
  }
  const k = target.objectKind.toLowerCase();
  if (k === "view") {
    return <Layers className="size-4 text-primary" />;
  }
  if (k === "file") {
    return <FileText className="size-4 text-primary" />;
  }
  if (k === "procedure" || k === "function" || k === "trigger") {
    return <Split className="size-4 text-primary" />;
  }
  return <Table2 className="size-4 text-primary" />;
}

/** One picker row: primary and secondary line, an optional flag badge, the chevron affordance. */
function JumpOption({
  primary,
  secondary,
  flag,
  onSelect,
}: {
  primary: string;
  secondary: string;
  flag?: string;
  onSelect: () => void;
}) {
  return (
    <DropdownMenuItem data-testid="lineage-jump-option" onSelect={onSelect} className="items-center gap-2 py-1.5">
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[13px]">{primary}</span>
        <span className="block truncate text-xs text-muted-foreground">{secondary}</span>
      </span>
      {flag !== undefined && <Badge variant="secondary" className="shrink-0 text-[10px]">{flag}</Badge>}
      <ChevronRight className="size-4 shrink-0 text-muted-foreground" />
    </DropdownMenuItem>
  );
}

/**
 * The lineage jump: a labeled action that opens a small picker showing exactly what you are about to open and,
 * for an object that lives in more than one repo, every graph it appears in (the repo whose flow populates it
 * flagged "populates") so you choose which to trace. A single target still shows the picker so the destination
 * is never a surprise. Selecting an option deep-links the graph focused on that node.
 */
export function LineageJumpButton({
  target, variant = "text", fullLabel = false, iconOnly = false,
}: {
  target: LineageJumpTarget;
  variant?: "text" | "outlined";
  /** When true, the trigger reads "View lineage"; otherwise the compact "Lineage" for dense rows. */
  fullLabel?: boolean;
  /** Drop the word entirely, for a grid dense enough that a secondary row action can only afford its glyph.
   * The label moves to the tooltip, which the aria-label already carried for assistive tech. */
  iconOnly?: boolean;
}) {
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);

  // Resolve which repos' graphs an object appears in (writing repo first), only once the picker is opened.
  const repos = useQuery({
    queryKey: ["lineage-object-repos", target.kind === "object" ? target.objectKey : ""],
    queryFn: () => lineageApi.objectRepos(target.kind === "object" ? target.objectKey : ""),
    enabled: open && target.kind === "object",
  });

  // Resolve which pipeline(s) ingest a file by matching it against the flow source specs in the catalog.
  const filePipelines = useQuery({
    queryKey: ["file-pipelines", target.kind === "file" ? target.filePath : ""],
    queryFn: () => lineageApi.filePipelines(target.kind === "file" ? target.filePath : ""),
    enabled: open && target.kind === "file",
  });

  // repoId is optional: a subscriber's graph walk is repo-agnostic (it seeds on the subscriber's own node key,
  // not a project), so no repo needs choosing for it, unlike an object (which can live in several repos' graphs).
  const go = (focusId: string, repoId?: string) => {
    setOpen(false);
    const params = new URLSearchParams({ focus: focusId });
    if (repoId !== undefined) {
      params.set("repoId", repoId);
    }
    navigate(`/lineage?${params.toString()}`);
  };

  const objectRepos = target.kind === "object" ? (repos.data ?? []) : [];
  const multi = objectRepos.length > 1;

  const trigger = (
    <Button
      variant={variant === "outlined" ? "outline" : "ghost"}
      size={iconOnly ? "icon-xs" : "sm"}
      aria-label="View in lineage graph"
      data-testid="search-open-graph"
      onClick={(event) => event.stopPropagation()}
      className="shrink-0 whitespace-nowrap"
    >
      <Network />
      {!iconOnly && (fullLabel ? "View lineage" : "Lineage")}
    </Button>
  );

  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      {/* The tooltip wraps the trigger, never the other way round: `DropdownMenuTrigger asChild` clones its
          props onto its child, and `Tooltip` is a context provider with no DOM node to receive them, so
          nesting it inside silently drops the menu's own click handling and the button opens nothing. */}
      {iconOnly ? (
        <Tooltip>
          <TooltipTrigger asChild>
            <DropdownMenuTrigger asChild>{trigger}</DropdownMenuTrigger>
          </TooltipTrigger>
          <TooltipContent>View in lineage graph</TooltipContent>
        </Tooltip>
      ) : (
        <DropdownMenuTrigger asChild>{trigger}</DropdownMenuTrigger>
      )}

      <DropdownMenuContent
        align="end"
        className="w-80"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="px-2 py-1.5" data-testid="lineage-jump-menu">
          <div className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
            Open in lineage graph
          </div>
          <div className="mt-1 flex items-center gap-2">
            <TargetIcon target={target} />
            <div className="min-w-0">
              <div className="truncate text-[13px] font-semibold">{target.label}</div>
              {target.sublabel !== undefined && (
                <div className="truncate text-xs text-muted-foreground">{target.sublabel}</div>
              )}
            </div>
          </div>
        </div>
        <DropdownMenuSeparator />

        {target.kind === "node" && (
          <JumpOption
            primary={target.repoName}
            secondary="Trace this flow in the graph"
            onSelect={() => go(target.focusId, target.repoId)}
          />
        )}

        {target.kind === "subscriber" && (
          <JumpOption
            primary={target.label}
            secondary="Trace what it reads and how those tables are populated"
            onSelect={() => go(target.subscriberKey)}
          />
        )}

        {target.kind === "object" && (
          repos.isPending ? (
            <div className="flex items-center gap-2 px-2 py-2 text-[13px] text-muted-foreground">
              <Loader2 className="size-4 animate-spin" /> Finding graphs
            </div>
          ) : objectRepos.length === 0 ? (
            <div className="px-2 py-2" data-testid="lineage-jump-empty">
              <p className="text-[13px] text-muted-foreground">
                This object has no recorded lineage yet, so it is not in any graph.
              </p>
              <button
                type="button"
                className="mt-1 text-[13px] text-primary hover:underline"
                onClick={() => {
                  setOpen(false);
                  navigate("/lineage/objects");
                }}
              >
                Open in the object explorer
              </button>
            </div>
          ) : (
            <>
              {multi && (
                <div className="px-2 py-1 text-xs text-muted-foreground">
                  {`Appears in ${objectRepos.length} repos; choose one`}
                </div>
              )}
              {objectRepos.map((repo) => (
                <JumpOption
                  key={repo.repoId}
                  primary={repo.repoName}
                  secondary={`${repo.edgeCount} lineage reference${repo.edgeCount === 1 ? "" : "s"}`}
                  flag={repo.writes ? "populates" : undefined}
                  onSelect={() => go(target.objectKey, repo.repoId)}
                />
              ))}
            </>
          )
        )}

        {target.kind === "file" && (
          filePipelines.isPending ? (
            <div className="flex items-center gap-2 px-2 py-2 text-[13px] text-muted-foreground">
              <Loader2 className="size-4 animate-spin" /> Matching flows
            </div>
          ) : (filePipelines.data?.length ?? 0) === 0 ? (
            <div className="px-2 py-2" data-testid="lineage-jump-empty">
              <p className="text-[13px] text-muted-foreground">
                No file flow's source pattern matches this file.
              </p>
            </div>
          ) : (
            <>
              {(filePipelines.data?.length ?? 0) > 1 && (
                <div className="px-2 py-1 text-xs text-muted-foreground">
                  {`Ingested by ${filePipelines.data!.length} flows; choose one`}
                </div>
              )}
              {filePipelines.data!.map((match) => (
                <JumpOption
                  key={match.pipelineId}
                  primary={match.pipelineName}
                  secondary={`${match.repoName} matches ${match.pattern}`}
                  flag={match.pathConfirmed ? "path match" : undefined}
                  onSelect={() => go(match.pipelineId, match.repoId)}
                />
              ))}
            </>
          )
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
