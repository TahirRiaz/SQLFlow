import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { usePanel } from "../../layout/workbench/PanelContext";
import { ActivityTracePanel } from "../activity/ActivityTracePanel";

/** The activity-trace kind a managed git repo sync writes under (mirrors ActivityKinds.RepoSync server-side). */
const REPO_SYNC_KIND = "repo-sync";

/**
 * Opens the workbench bottom trace panel on a repo source's live sync log, keyed by the source id. Shared by the
 * repos list and the repo detail page so both trigger the exact same panel (one code path): pass the source id and
 * the display name; a fresh nonce restarts the stream on each open so a repeat "Sync now" replays from the top, and
 * the terminal frame refreshes the source list so the health badge settles to synced/error.
 */
export function useSyncTracePanel(): (sourceId: string, name: string) => void {
  const panel = usePanel();
  const queryClient = useQueryClient();
  const [nonce, setNonce] = useState(0);

  return (sourceId: string, name: string) => {
    const next = nonce + 1;
    setNonce(next);
    panel.open({
      id: `sync-trace:${sourceId}`,
      title: `Sync · ${name}`,
      node: (
        <ActivityTracePanel
          kind={REPO_SYNC_KIND}
          subject={sourceId}
          nonce={next}
          onEnded={() => {
            void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
            void queryClient.invalidateQueries({ queryKey: ["repos"] });
          }}
        />
      ),
    });
  };
}
