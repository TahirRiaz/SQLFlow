import { useState } from "react";
import { DEFAULT_PAGE_SIZE, pageOfRows } from "../lib/paging";

/** The page a paged table shows and its size, with how a reader moves to another page or picks another size. */
export interface TablePaging {
  /** The page shown, counted from 0. */
  page: number;
  pageSize: number;
  onPage: (page: number) => void;
  /** Picks another size, which starts the list again on its first page. */
  onPageSize: (pageSize: number) => void;
}

/**
 * The page a paged table shows, for the list `listKey` names: every filter value is in the key, so a filter change is a
 * different list. A different list starts on its first page, since an offset into the old one is meaningless: narrowing
 * 400 rows to 3 while parked on page 3 would show an empty table over a filter that matched. The page resets during
 * render, and the page returned is already the first, so a server-paged table's first request is for page 1, not a
 * discarded fetch of a page past the end.
 */
export function useTablePaging(listKey: string): TablePaging {
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [shownList, setShownList] = useState(listKey);
  const changed = listKey !== shownList;
  if (changed) {
    setShownList(listKey);
    setPage(0);
  }

  return {
    page: changed ? 0 : page,
    pageSize,
    onPage: setPage,
    onPageSize: (size) => {
      setPageSize(size);
      setPage(0);
    },
  };
}

/** A page of a list held in memory: its rows (undefined while the list is read), and the pager's state over the whole list. */
export interface PagedRows<T> {
  rows: T[] | undefined;
  pager: TablePaging & { total: number };
}

/**
 * Pages a list a page already holds (read whole, then searched and filtered in the browser) as a server-paged table
 * pages its rows, so both read and move alike: give `rows` to the DataTable and `pager` to its TablePager. `listKey`
 * names the list as `useTablePaging` says; a list that shrinks under the page shown (a refresh dropped rows) moves to its
 * last page.
 */
export function usePagedRows<T>(rows: readonly T[] | undefined, listKey: string): PagedRows<T> {
  const paging = useTablePaging(listKey);
  if (rows === undefined) {
    return { rows: undefined, pager: { ...paging, total: 0 } };
  }

  // A page the list shrank under is left for good, so the list growing back does not jump to it again.
  const shown = pageOfRows(rows, paging.page, paging.pageSize);
  if (shown.page !== paging.page) {
    paging.onPage(shown.page);
  }

  return { rows: shown.rows, pager: { ...paging, page: shown.page, total: rows.length } };
}
