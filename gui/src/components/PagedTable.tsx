import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { type CSSProperties, type ReactNode, useEffect } from "react";
import type { PagedResult } from "../api/types";
import { isApiError } from "../api/client";
import { pollingInterval } from "../hooks/usePolling";
import { useTablePaging } from "../hooks/useTablePaging";
import { CorrelationError } from "./CorrelationError";
import { DataTable, type Column, type RowSelection, type TableGrouping } from "./DataTable";
import { TablePager } from "./TablePager";

// Re-exported so the pages keep importing the table types from here (their single table entry point).
export type { Column, RowSelection, TableGrouping } from "./DataTable";

interface PagedTableProps<T> {
  /** Cache identity for the query; include every filter value so a filter change is a new query. */
  queryKey: readonly unknown[];
  fetchPage: (page: number, pageSize: number) => Promise<PagedResult<T>>;
  columns: Column<T>[];
  rowKey: (row: T) => string | number;
  onRowClick?: (row: T) => void;
  /** Poll cadence for live views (runs, nodes); omit for on-demand pages. */
  pollMs?: number;
  /** Optional per-row style (a failed statement tints red, say); return undefined for the default styling. */
  rowSx?: (row: T) => CSSProperties | undefined;
  emptyMessage: string;
  grouping?: TableGrouping<T>;
  /** Adds a leading checkbox column; the page owns the selected set so it survives paging. */
  selection?: RowSelection<T>;
  /** Rendered inside the bordered surface above the table: where a selection toolbar goes. */
  toolbar?: ReactNode;
  /** Called with the rows and total of each page as it arrives, for pages that act on the whole match; a capped total is a floor. */
  onPageLoaded?: (rows: T[], total: number, totalCapped: boolean) => void;
  /** The width below which the table scrolls instead of crushing its columns; see DataTable. */
  minWidth?: number;
  "data-testid"?: string;
}

/**
 * The single paged list-page code path: server-side offset paging against the API's PagedResult on top of
 * the shared DataTable shell (loading skeletons, the shared error and empty rendering, optional selection and
 * tree grouping), with optional polling for live views. Pages that already hold their rows render DataTable
 * directly instead, paging them with `usePagedRows` under the same TablePager.
 */
export function PagedTable<T>({
  queryKey, fetchPage, columns, rowKey, onRowClick, pollMs, rowSx, emptyMessage, grouping, selection, toolbar,
  onPageLoaded, minWidth, "data-testid": testId,
}: PagedTableProps<T>) {
  // The page is counted from 0, the API's from 1. A filter change is a different list (every filter value is in the
  // key by contract), so it starts on its first page.
  const paging = useTablePaging(JSON.stringify(queryKey));
  const { page, pageSize } = paging;

  const query = useQuery({
    queryKey: [...queryKey, page, pageSize],
    queryFn: () => fetchPage(page + 1, pageSize),
    placeholderData: keepPreviousData,
    refetchInterval: pollMs ? pollingInterval(pollMs) : undefined,
  });

  // The page a caller acts on in bulk: reported after render, so a parent can hold the rows it is showing (to
  // select them all, say) without this component owning that state.
  const loaded = query.data;
  useEffect(() => {
    if (loaded !== undefined) {
      onPageLoaded?.(loaded.items, loaded.total, loaded.totalCapped === true);
    }
  }, [loaded, onPageLoaded]);

  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const result = query.data;

  return (
    <DataTable
      columns={columns}
      rows={result?.items}
      rowKey={rowKey}
      onRowClick={onRowClick}
      rowSx={rowSx}
      emptyMessage={emptyMessage}
      grouping={grouping}
      selection={selection}
      toolbar={toolbar}
      minWidth={minWidth}
      data-testid={testId ?? "paged-table"}
      // A capped listing pages only through what it counted; the "+" says there is more past it.
      footer={<TablePager {...paging} total={result?.total ?? 0} capped={result?.totalCapped === true} />}
    />
  );
}
