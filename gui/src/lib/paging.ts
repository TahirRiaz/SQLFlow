/** The page sizes a paged table offers. */
export const PAGE_SIZES: readonly number[] = [25, 50, 100, 200];

/** How many rows a paged table shows on a page until a reader picks another size. */
export const DEFAULT_PAGE_SIZE = 50;

/** Where one page of a list sits in it: the rows it shows, counted from 1, and the last page there is. */
export interface PageSpan {
  /** The first row the page shows, counted from 1; 0 when the list is empty. */
  from: number;
  /** The last row the page shows, counted from 1. */
  to: number;
  /** The last page of the list, counted from 0; 0 when the list is empty. */
  lastPage: number;
}

/** Where page `page` (counted from 0) of a list of `total` rows sits, at `pageSize` rows a page. */
export function pageSpan(total: number, page: number, pageSize: number): PageSpan {
  return {
    from: total === 0 ? 0 : page * pageSize + 1,
    to: Math.min(total, (page + 1) * pageSize),
    lastPage: Math.max(0, Math.ceil(total / pageSize) - 1),
  };
}

/**
 * The rows one page of a list held in memory shows, and the page they are. A page past the list's end (the list shrank
 * under it: a refresh dropped rows) shows the last page there is, so a list with rows never shows an empty page.
 */
export function pageOfRows<T>(rows: readonly T[], page: number, pageSize: number): { rows: T[]; page: number } {
  const shown = Math.min(Math.max(0, page), pageSpan(rows.length, 0, pageSize).lastPage);
  return { rows: rows.slice(shown * pageSize, (shown + 1) * pageSize), page: shown };
}
