import { useCallback } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useTraceTray, type TraceSpec } from "../../layout/workbench/TraceTray";
import { ActivityTracePanel } from "../activity/ActivityTracePanel";

/** The activity-trace kind a managed git repo sync writes under (mirrors ActivityKinds.RepoSync server-side). */
const REPO_SYNC_KIND = "repo-sync";

/**
 * A repo source's live sync log as the trace tray holds it, keyed by the source id, so the repos list, the repo detail
 * page and a module's sync control all reach the one trace (one code path). Its end refreshes the source list, so the
 * health badge settles to synced or error.
 */
function useSyncTraceSpec(): (sourceId: string, name: string) => TraceSpec {
  const queryClient = useQueryClient();
  return useCallback((sourceId: string, name: string) => ({
    id: `sync:${sourceId}`,
    title: `Sync · ${name}`,
    node: (
      <ActivityTracePanel
        kind={REPO_SYNC_KIND}
        subject={sourceId}
        onEnded={() => {
          void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
          void queryClient.invalidateQueries({ queryKey: ["repos"] });
        }}
      />
    ),
  }), [queryClient]);
}

/** Opens a repo source's sync trace in the workbench's bottom panel, for a click that asks to see it (a failed sync). */
export function useSyncTracePanel(): (sourceId: string, name: string) => void {
  const { open } = useTraceTray();
  const spec = useSyncTraceSpec();
  return useCallback((sourceId: string, name: string) => open(spec(sourceId, name)), [open, spec]);
}

/**
 * Follows the sync a "Sync now" just asked for from the title bar's trace indicator, leaving the page as it is: the
 * trace streams there while the sync runs and opens in the bottom panel when asked. A sync asked for again starts its
 * trace over.
 */
export function useFollowSyncTrace(): (sourceId: string, name: string) => void {
  const { follow } = useTraceTray();
  const spec = useSyncTraceSpec();
  return useCallback((sourceId: string, name: string) => follow(spec(sourceId, name)), [follow, spec]);
}
