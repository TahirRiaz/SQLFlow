import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { CircleAlert, FileLock2, FolderGit2, Info } from "lucide-react";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { repoSourceApi } from "../../api/endpoints";
import type { ExternalTrigger, RepoSource } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { NoteRef } from "../../components/NoteRef";
import { RelativeTime } from "../../components/RelativeTime";
import { RichTooltip } from "../../components/RichTooltip";
import { TruncatedText } from "../../components/TruncatedText";
import {
  autoSyncAdvice, heldCommit, intervalText, syncModeDetail, syncModeText, syncsOnRequest, triggersOf,
} from "../repos/autoSync";
import { repoSourcesRefetchInterval } from "../repos/syncOutcome";
import { useSyncTracePanel } from "../repos/useSyncTracePanel";
import { errorText } from "./triggerVisuals";

/** The page the Repos page reads, under the same key: repositories are a handful per control plane. */
const FETCH_CAP = 200;

/** Who last turned a source's auto sync on or off, for its row's note; null when nobody has here. */
function changedNote(source: RepoSource): string | null {
  if (source.autoSyncUpdatedBy === null || source.autoSyncUpdatedBy === undefined) {
    return null;
  }

  const when = source.autoSyncUpdatedUtc ? ` on ${new Date(source.autoSyncUpdatedUtc).toLocaleString()}` : "";
  return `Turned ${source.autoSync === false ? "off" : "on"} by ${source.autoSyncUpdatedBy}${when}.`;
}

/**
 * Whether each repository syncs on its own or only when asked: the switch that keeps a merge from reaching every
 * environment that syncs the same branch. On, a repository reads its branch's tip every interval. Off, it holds the commit
 * it has until a trigger request moves it to the merge the request reports (or a person syncs it now), so each
 * environment takes a merge when its own trigger says. A row says which triggers sync the repository and warns when
 * nothing would; a value the control plane's configuration sets is shown, not changed.
 */
export function RepositoriesTab({ triggers }: { triggers: ExternalTrigger[] | undefined }) {
  const queryClient = useQueryClient();
  const openSyncTrace = useSyncTracePanel();
  const [turningOn, setTurningOn] = useState<RepoSource | null>(null);

  const sources = useQuery({
    queryKey: ["repo-sources", "list", FETCH_CAP],
    queryFn: () => repoSourceApi.list({ page: 1, pageSize: FETCH_CAP }),
    refetchInterval: (query) => repoSourcesRefetchInterval(query.state.data),
  });

  const setAutoSync = useMutation({
    mutationFn: ({ source, autoSync }: { source: RepoSource; autoSync: boolean }) => repoSourceApi.setAutoSync(source.id, autoSync),
    onSuccess: (source) => {
      toast.success(source.autoSync === false
        ? `${source.name} syncs only when asked now; it holds ${heldCommit(source) ?? "no commit yet"} until a trigger or a person moves it.`
        : `${source.name} syncs on its own now, every ${intervalText(source.syncIntervalSeconds)}; it is catching up with ${source.branch}.`);
      setTurningOn(null);
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
    },
    onError: (error) => {
      toast.error(errorText(error));
      setTurningOn(null);
    },
  });

  const all = triggers ?? [];
  const columns: Column<RepoSource>[] = [
    {
      id: "name",
      header: "Repository",
      render: (s) => (
        <span className="inline-flex items-center gap-1.5">
          <span className="font-mono text-[13px] font-medium">{s.name}</span>
          <span className="text-[12px] text-muted-foreground">{s.branch}</span>
        </span>
      ),
    },
    {
      id: "auto",
      header: "Auto sync",
      width: 96,
      render: (s) => {
        const pinned = s.autoSyncConfiguredBy ?? null;
        const on = !syncsOnRequest(s);
        const toggle = (
          <Switch
            checked={on}
            disabled={pinned !== null || setAutoSync.isPending}
            onCheckedChange={(next) => {
              if (next) {
                setTurningOn(s);
              } else {
                setAutoSync.mutate({ source: s, autoSync: false });
              }
            }}
            aria-label={`Auto sync of ${s.name}`}
            data-testid="repo-auto-sync"
          />
        );
        return (
          <span className="inline-flex items-center gap-1.5">
            {pinned === null ? toggle : (
              <Tooltip>
                <TooltipTrigger asChild>
                  <span className="inline-flex items-center gap-1.5" data-testid="repo-auto-sync-configured">
                    {toggle}
                    <FileLock2 className="size-3.5 text-muted-foreground" aria-hidden />
                  </span>
                </TooltipTrigger>
                <TooltipContent className="max-w-xs">
                  Set by the control plane&apos;s configuration, <span className="font-mono">{pinned}</span>; change it there, or
                  remove it there to set it here.
                </TooltipContent>
              </Tooltip>
            )}
            <NoteRef note={changedNote(s)} title="Changed" placeholder="" />
          </span>
        );
      },
    },
    {
      id: "syncs",
      header: "Syncs",
      fill: true,
      floor: 160,
      render: (s) => {
        const advice = autoSyncAdvice(s, all);
        return (
          <span className="inline-flex min-w-0 items-center gap-1.5">
            <RichTooltip title="How it syncs" body={syncModeDetail(s)}>
              <span className="truncate text-[13px]" data-testid="repo-sync-mode">{syncModeText(s)}</span>
            </RichTooltip>
            {advice !== null && (
              <RichTooltip title={advice.tone === "warning" ? "Nothing syncs it on a merge" : "Its triggers do not gate it"} body={advice.text}>
                <span
                  className={cn("inline-flex", advice.tone === "warning" ? "text-warning" : "text-muted-foreground")}
                  data-testid={`repo-sync-advice-${advice.tone}`}
                >
                  {advice.tone === "warning" ? <CircleAlert className="size-3.5" /> : <Info className="size-3.5" />}
                  <span className="sr-only">{advice.text}</span>
                </span>
              </RichTooltip>
            )}
          </span>
        );
      },
    },
    {
      id: "holds",
      header: "Synced at",
      render: (s) => (
        <button
          type="button"
          className="inline-flex items-center gap-1.5 rounded-sm text-[12px] hover:underline"
          aria-label={`Open the sync trace of ${s.name}`}
          onClick={() => openSyncTrace(s.id, s.name)}
          data-testid="repo-sync-trace"
        >
          <span className={cn("font-mono", s.lastError !== null && "text-destructive")}>{heldCommit(s) ?? (s.lastSyncedSha === null ? "not synced" : "directory")}</span>
          <RelativeTime value={s.lastSyncUtc} />
        </button>
      ),
    },
    {
      id: "triggers",
      header: "Synced by",
      render: (s) => {
        const names = triggersOf(s, all).map((t) => t.name);
        return names.length === 0
          ? <span className="text-[13px] text-muted-foreground">no trigger</span>
          : <TruncatedText text={names.join(", ")} mono maxWidth={220} />;
      },
    },
  ];

  if (sources.isError) {
    return isApiError(sources.error)
      ? <CorrelationError error={sources.error} />
      : <p className="text-[13px] text-destructive">{errorText(sources.error)}</p>;
  }

  if (sources.data !== undefined && sources.data.items.length === 0) {
    return (
      <div className="rounded-lg border border-border" data-testid="repositories-empty">
        <EmptyState
          icon={<FolderGit2 />}
          title="No repositories yet"
          description="Register a repository on the Repos page; whether it syncs on its own or only when a trigger asks is set here."
        />
      </div>
    );
  }

  return (
    <>
      <p className="text-[13px] text-muted-foreground">
        With auto sync on, a repository reads its branch every interval, so a merge reaches this control plane on its own. Off,
        it holds the commit it has until a trigger request moves it to the merge it reports, or someone syncs it now.
      </p>
      <DataTable
        columns={columns}
        rows={sources.data?.items}
        rowKey={(s) => s.id}
        emptyMessage="No repositories."
        skeletonRows={3}
        data-testid="repositories-table"
      />

      <ConfirmDialog
        open={turningOn !== null}
        title="Turn auto sync on"
        message={turningOn === null ? "" : `${turningOn.name} syncs now, moving from ${heldCommit(turningOn) ?? "what it holds"} to the tip of `
          + `${turningOn.branch} with everything merged there since, and then every ${intervalText(turningOn.syncIntervalSeconds)}, `
          + "whatever its triggers do."}
        confirmLabel="Turn on"
        busy={setAutoSync.isPending}
        onConfirm={() => { if (turningOn !== null) setAutoSync.mutate({ source: turningOn, autoSync: true }); }}
        onClose={() => setTurningOn(null)}
      />
    </>
  );
}
