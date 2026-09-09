import { pipelineApi } from "../../api/endpoints";
import type { PipelineSummary } from "../../api/types";

/** One page of the fetch-all sweep. The API clamps `pageSize` to `PageRequest.MaxPageSize` (200), so asking for more
 * silently yields 200 anyway; matching the server's ceiling keeps the round-trip count honest. */
const PAGE_SIZE = 200;

/** The most pipelines a grouped view will pull into the browser at once. Beyond this the tree stays responsive by
 * showing the first slice and asking the user to narrow with filters or search, rather than fetching without bound. */
const FETCH_CAP = 5000;

export interface FetchResult {
  items: PipelineSummary[];
  /** How many pipelines match on the server, which exceeds `items.length` once the cap is hit. */
  total: number;
  capped: boolean;
}

/**
 * Pull every pipeline matching the server-side filters (repo/kind/active) by paging until the estate is exhausted or
 * the fetch cap is hit; a free-text search then narrows this set in the browser. Every grouped-by-project view goes
 * through this, because a single page request silently truncates a large repo mid-alphabet: whole project folders
 * would be missing from the tree with nothing on screen to say so.
 */
export async function fetchAllPipelines(
  filters: { repoId?: string; kind?: string; active?: boolean },
): Promise<FetchResult> {
  const items: PipelineSummary[] = [];
  let total = 0;
  let page = 1;
  for (;;) {
    const res = await pipelineApi.list({ ...filters, page, pageSize: PAGE_SIZE });
    total = res.total;
    items.push(...res.items);
    if (items.length >= total || res.items.length === 0 || items.length >= FETCH_CAP) {
      break;
    }
    page += 1;
  }
  return { items, total, capped: items.length < total };
}
