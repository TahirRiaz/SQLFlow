import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery, type UseQueryResult } from "@tanstack/react-query";
import { ChevronDown, FolderGit2 } from "lucide-react";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { Badge } from "@/components/ui/badge";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { repoApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { FilterBar } from "../../components/FilterBar";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { SearchInput } from "../../components/SearchInput";
import { fetchAllPipelines, type FetchResult } from "./fetchAllPipelines";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { groupByProject, pipelineMatches, ProjectGroup } from "./ProjectGroup";

/** Every flow kind the loader recognises, acquisition-first then transform/utility (see YamlDocumentLoader). */
const kinds = ["file", "ing", "api", "cpy", "sftp", "exp", "trl", "sp", "inv", "hc", "scm", "batch", "cal"];

/** The radix Select cannot carry an empty-string item value, so "all" stands in for the unfiltered choice. */
const ALL = "all";

/** One repo's pipelines: a collapsible section holding the repo's project groups. Rendered only in the all-repos view;
 * when a single repo is selected the page drops this level and shows its projects directly. */
function RepoGroup({
  repoName, repoId, pipelines, filtered, onOpen, onRunBatch,
}: {
  repoName: string;
  repoId: string;
  pipelines: PipelineSummary[];
  filtered: boolean;
  onOpen: (pipelineId: string) => void;
  onRunBatch: (repoId: string, flowName: string) => void;
}) {
  const inactive = pipelines.filter((p) => !p.active).length;
  return (
    // Collapsed by default so the page reads as a repo outline; keying on the search state remounts the section so it
    // springs open when a search starts (surfacing a match inside) and folds shut again when the search clears.
    <Collapsible key={`${repoId}:${filtered ? "filtered" : "all"}`} defaultOpen={filtered}>
      <div className="flex flex-col gap-2" data-testid="pipelines-repo">
        <CollapsibleTrigger className="group flex min-w-0 items-center gap-2 text-left">
          <ChevronDown className="size-4 shrink-0 text-muted-foreground transition-transform duration-120 group-data-[state=closed]:-rotate-90" />
          <FolderGit2 className="size-4 shrink-0 text-muted-foreground" />
          <span className="truncate text-sm font-semibold">{repoName}</span>
          <Badge variant="outline">{pipelines.length} pipeline{pipelines.length === 1 ? "" : "s"}</Badge>
          {inactive > 0 && (
            <Badge variant="outline" className="border-warning/50 text-warning">{inactive} inactive</Badge>
          )}
        </CollapsibleTrigger>
        <CollapsibleContent>
          <div className="flex flex-col gap-2 pl-6" data-testid="repo-projects">
            {groupByProject(pipelines).map(([project, rows]) => (
              <ProjectGroup
                key={project}
                project={project}
                rows={rows}
                repoId={repoId}
                filtered={filtered}
                defaultOpen={filtered}
                onOpen={onOpen}
                onRunBatch={onRunBatch}
              />
            ))}
          </div>
        </CollapsibleContent>
      </div>
    </Collapsible>
  );
}

/** All pipelines across repos, grouped as a repo -> project folder tree. Server-side filters (repo, kind, active) shape
 * the fetched set; a free-text box narrows it in the browser by name, path, kind, or project. */
export default function PipelinesPage() {
  const navigate = useNavigate();
  const [repoFilter, setRepoFilter] = useLocalStorageState("sqlflow.filters.pipelines.repo", "");
  const [kindFilter, setKindFilter] = useLocalStorageState("sqlflow.filters.pipelines.kind", "");
  const [activeFilter, setActiveFilter] = useLocalStorageState("sqlflow.filters.pipelines.active", "");
  const [search, setSearch] = useLocalStorageState("sqlflow.filters.pipelines.name", "");
  const [runBatch, setRunBatch] = useState<{ repoId: string; flowName: string } | null>(null);

  const repos = useQuery({
    queryKey: ["repos", "all-for-pipeline-filter"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
  });
  const repoOptions = repos.data?.items ?? [];
  const repoNameById = new Map(repoOptions.map((r) => [r.id, r.name]));

  const query = useQuery({
    queryKey: ["pipelines", "grouped", repoFilter, kindFilter, activeFilter],
    queryFn: () => fetchAllPipelines({
      repoId: repoFilter === "" ? undefined : repoFilter,
      kind: kindFilter === "" ? undefined : kindFilter,
      active: activeFilter === "" ? undefined : activeFilter === "active",
    }),
  });

  const needle = search.trim().toLowerCase();

  return (
    <Page data-testid="page-pipelines">
      <PageHeader title="Pipelines" />

      <FilterBar>
        {/* Search leads the row: typing a name is the fastest way into a 472-pipeline tree, so it comes before the
            dropdowns that narrow it. */}
        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Search by name, path, kind, project"
          label="Search pipelines"
          testId="filter-name"
        />
        <Select
          value={repoFilter === "" ? ALL : repoFilter}
          onValueChange={(value) => setRepoFilter(value === ALL ? "" : value)}
        >
          <SelectTrigger size="sm" active={repoFilter !== ""} className="h-8 w-52" aria-label="Repo" data-testid="filter-repo">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>all repos</SelectItem>
            {repoOptions.map((repo) => (
              <SelectItem key={repo.id} value={repo.id}>{repo.name}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={kindFilter === "" ? ALL : kindFilter}
          onValueChange={(value) => setKindFilter(value === ALL ? "" : value)}
        >
          <SelectTrigger size="sm" active={kindFilter !== ""} className="h-8 w-32" aria-label="Kind" data-testid="filter-kind">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>all kinds</SelectItem>
            {kinds.map((kind) => (
              <SelectItem key={kind} value={kind}>{kind}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={activeFilter === "" ? ALL : activeFilter}
          onValueChange={(value) => setActiveFilter(value === ALL ? "" : value)}
        >
          <SelectTrigger size="sm" active={activeFilter !== ""} className="h-8 w-32" aria-label="Active" data-testid="filter-active">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>all</SelectItem>
            <SelectItem value="active">active</SelectItem>
            <SelectItem value="inactive">inactive</SelectItem>
          </SelectContent>
        </Select>
      </FilterBar>

      <PipelinesTree
        query={query}
        needle={needle}
        singleRepo={repoFilter !== ""}
        repoNameById={repoNameById}
        onOpen={(id) => navigate(`/pipelines/${id}`)}
        onRunBatch={(repoId, flowName) => setRunBatch({ repoId, flowName })}
      />

      {runBatch !== null && (
        <TriggerRunDialog
          open
          onClose={() => setRunBatch(null)}
          repoId={runBatch.repoId}
          flowName={runBatch.flowName}
        />
      )}
    </Page>
  );
}

/** Renders the fetched pipelines as the grouped tree, plus the load/error/empty/no-match states and the fetch-cap
 * note. Split out so the page component stays about filters and wiring. */
function PipelinesTree({
  query, needle, singleRepo, repoNameById, onOpen, onRunBatch,
}: {
  query: UseQueryResult<FetchResult>;
  needle: string;
  singleRepo: boolean;
  repoNameById: Map<string, string>;
  onOpen: (pipelineId: string) => void;
  onRunBatch: (repoId: string, flowName: string) => void;
}) {
  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const result = query.data;
  if (result === undefined) {
    return <Skeleton className="h-40 w-full rounded-lg" />;
  }

  if (result.items.length === 0) {
    return (
      <EmptyState
        title="No pipelines"
        description="No pipeline matches the current filters. Clear a filter, or sync a repo to import its flows."
        data-testid="pipelines-empty"
      />
    );
  }

  const matches = needle === "" ? result.items : result.items.filter((p) => pipelineMatches(p, needle));

  const cappedNote = result.capped && (
    <p className="text-xs text-warning" data-testid="pipelines-capped">
      Showing the first {result.items.length.toLocaleString()} of {result.total.toLocaleString()} pipelines. Narrow
      with the filters above to see the rest.
    </p>
  );

  if (matches.length === 0) {
    return (
      <div className="flex flex-col gap-2">
        {cappedNote}
        <EmptyState
          title="No matches"
          description={`No pipeline matches "${needle}". Search by name, path, kind, or project.`}
          data-testid="pipelines-no-matches"
        />
      </div>
    );
  }

  // A single repo is selected: drop the repo level and show its project groups directly, like the repo detail view.
  if (singleRepo) {
    return (
      <div className="flex flex-col gap-2" data-testid="repo-projects">
        {cappedNote}
        {groupByProject(matches).map(([project, rows]) => (
          <ProjectGroup
            key={project}
            project={project}
            rows={rows}
            repoId={rows[0].repoId}
            filtered={needle !== ""}
            defaultOpen={needle !== ""}
            onOpen={onOpen}
            onRunBatch={onRunBatch}
          />
        ))}
      </div>
    );
  }

  // All repos: group by repo, each sorted by name, then project groups nested inside.
  const byRepo = new Map<string, PipelineSummary[]>();
  for (const p of matches) {
    const group = byRepo.get(p.repoId);
    if (group) {
      group.push(p);
    } else {
      byRepo.set(p.repoId, [p]);
    }
  }
  const repoGroups = [...byRepo.entries()]
    .map(([repoId, pipelines]) => ({ repoId, pipelines, name: repoNameById.get(repoId) ?? repoId }))
    .sort((a, b) => a.name.localeCompare(b.name));

  return (
    <div className="flex flex-col gap-3" data-testid="pipelines-repos">
      {cappedNote}
      {repoGroups.map(({ repoId, name, pipelines }) => (
        <RepoGroup
          key={repoId}
          repoId={repoId}
          repoName={name}
          pipelines={pipelines}
          filtered={needle !== ""}
          onOpen={onOpen}
          onRunBatch={onRunBatch}
        />
      ))}
    </div>
  );
}
