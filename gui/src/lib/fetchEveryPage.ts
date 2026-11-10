import type { PagedResult } from "../api/types.ts";

/** One page of a sweep. The API clamps `pageSize` to `PageRequest.MaxPageSize` (200), so asking for more silently
 * yields 200 anyway; matching the server's ceiling keeps the round-trip count honest. */
export const SWEEP_PAGE_SIZE = 200;

/** What a sweep pulled: the rows, how many match on the server, and whether the cap stopped it short of them. */
export interface PageSweep<T> {
  items: T[];
  /** How many rows match on the server, which exceeds `items.length` once the cap is hit. */
  total: number;
  capped: boolean;
}

/**
 * Pull every row a paged list matches, page after page, until the server has none left or `cap` rows are in. A view
 * that groups or offers every row (a folder tree, a dropdown of every schedule) goes through this, because one page
 * request silently truncates the list mid-alphabet and the missing rows leave nothing on screen to say so; `capped`
 * lets the view say it stopped instead. A page that comes back empty ends the sweep, so a total that shrinks while it
 * runs cannot loop it.
 */
export async function fetchEveryPage<T>(
  fetchPage: (page: number, pageSize: number) => Promise<PagedResult<T>>,
  cap: number,
): Promise<PageSweep<T>> {
  const items: T[] = [];
  let total = 0;
  let page = 1;
  for (;;) {
    const res = await fetchPage(page, SWEEP_PAGE_SIZE);
    total = res.total;
    items.push(...res.items);
    if (items.length >= total || res.items.length === 0 || items.length >= cap) {
      break;
    }
    page += 1;
  }
  return { items, total, capped: items.length < total };
}
