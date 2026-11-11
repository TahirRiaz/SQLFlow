import { pipelineApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";
import { fetchEveryPage, type PageSweep } from "../../lib/fetchEveryPage";

/** The most pipelines a grouped view will pull into the browser at once. Beyond this the tree stays responsive by
 * showing the first slice and asking the user to narrow with filters or search, rather than fetching without bound. */
const FETCH_CAP = 5000;

export type FetchResult = PageSweep<PipelineSummary>;

/**
 * Pull every pipeline matching the server-side filters (repo/kind/active/schedule) by paging until the estate is
 * exhausted or the fetch cap is hit; a free-text search then narrows this set in the browser. Every grouped-by-project
 * view goes through this, because a single page request silently truncates a large repo mid-alphabet: whole project
 * folders would be missing from the tree with nothing on screen to say so.
 */
export function fetchAllPipelines(
  filters: { repoId?: string; kind?: string; active?: boolean; scheduleId?: string },
): Promise<FetchResult> {
  return fetchEveryPage((page, pageSize) => pipelineApi.list({ ...filters, page, pageSize }), FETCH_CAP);
}
