import { toast } from "sonner";
import type { PagedResult, RepoSource } from "../../api/types";

/** How often a repo-source list refetches while one of its sources is syncing, and otherwise. */
const SYNCING_REFETCH_MS = 2000;
const IDLE_REFETCH_MS = 8000;

const shortSha = (sha: string | null) => (sha === null ? "no synced commit" : sha.slice(0, 12));

/**
 * Says what a sync-now came back with. The control plane answers a sync-now only once the sync has happened, so the
 * commit named here is the one a run started next is pinned to; a sync that outlasted the server's wait is still
 * running, and its source shows as syncing until it finishes.
 */
export function announceSyncOutcome(source: RepoSource): void {
  if (source.syncPending) {
    toast.info(`The sync of ${source.name} is still running; runs stay on ${shortSha(source.lastSyncedSha)} until it finishes.`);
  } else if (source.lastError !== null) {
    toast.error(`The sync of ${source.name} failed: ${source.lastError}`);
  } else {
    toast.success(`${source.name} synced to ${shortSha(source.lastSyncedSha)}.`);
  }
}

/** The refetch interval of a repo-source list: quicker while a source is syncing, so its button stops spinning soon after the sync ends. */
export function repoSourcesRefetchInterval(data: PagedResult<RepoSource> | undefined): number {
  return data?.items.some((source) => source.syncPending) ? SYNCING_REFETCH_MS : IDLE_REFETCH_MS;
}
