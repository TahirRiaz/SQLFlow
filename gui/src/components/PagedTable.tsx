import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { type CSSProperties, useState } from "react";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { PagedResult } from "../api/types";
import { isApiError } from "../api/client";
import { pollingInterval } from "../hooks/usePolling";
import { CorrelationError } from "./CorrelationError";
import { DataTable, type Column, type TableGrouping } from "./DataTable";

// Re-exported so the pages keep importing the table types from here (their single table entry point).
export type { Column, TableGrouping } from "./DataTable";

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
  "data-testid"?: string;
}

/**
 * The single paged list-page code path: server-side offset paging against the API's PagedResult on top of
 * the shared DataTable shell (loading skeletons, the shared error and empty rendering, optional tree
 * grouping), with optional polling for live views. Pages that already hold their rows render DataTable
 * directly instead.
 */
export function PagedTable<T>({
  queryKey, fetchPage, columns, rowKey, onRowClick, pollMs, rowSx, emptyMessage, grouping, "data-testid": testId,
}: PagedTableProps<T>) {
  const [page, setPage] = useState(0); // rendered 0-based; the API is 1-based
  const [pageSize, setPageSize] = useState(50);

  // A filter change is a different list, so the offset into the old one is meaningless: narrowing 400 rows to 3 while
  // parked on page 3 would render an empty table over a filter that matched. The key identifies the list (every
  // filter value is in it by contract), and resetting during render means the first request is for page 1, not a
  // discarded fetch of a page past the end.
  const listKey = JSON.stringify(queryKey);
  const [shownList, setShownList] = useState(listKey);
  if (listKey !== shownList) {
    setShownList(listKey);
    setPage(0);
  }

  const query = useQuery({
    queryKey: [...queryKey, page, pageSize],
    queryFn: () => fetchPage(page + 1, pageSize),
    placeholderData: keepPreviousData,
    refetchInterval: pollMs ? pollingInterval(pollMs) : undefined,
  });

  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const result = query.data;
  const total = result?.total ?? 0;
  const from = total === 0 ? 0 : page * pageSize + 1;
  const to = Math.min(total, (page + 1) * pageSize);
  const lastPage = Math.max(0, Math.ceil(total / pageSize) - 1);

  return (
    <DataTable
      columns={columns}
      rows={result?.items}
      rowKey={rowKey}
      onRowClick={onRowClick}
      rowSx={rowSx}
      emptyMessage={emptyMessage}
      grouping={grouping}
      data-testid={testId ?? "paged-table"}
      footer={(
        <div className="flex items-center justify-between gap-4 border-t border-border px-3 py-1.5">
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            Rows per page
            <Select
              value={String(pageSize)}
              onValueChange={(value) => {
                setPageSize(Number.parseInt(value, 10));
                setPage(0);
              }}
            >
              <SelectTrigger size="sm" className="h-7 w-[72px] text-xs" aria-label="Rows per page">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {[25, 50, 100, 200].map((size) => (
                  <SelectItem key={size} value={String(size)}>{size}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="flex items-center gap-1 text-xs text-muted-foreground">
            <span className="font-mono tabular-nums">{from}-{to} of {total}</span>
            <Button
              variant="ghost"
              size="icon-xs"
              aria-label="Previous page"
              disabled={page === 0}
              onClick={() => setPage((current) => Math.max(0, current - 1))}
            >
              <ChevronLeft />
            </Button>
            <Button
              variant="ghost"
              size="icon-xs"
              aria-label="Next page"
              disabled={page >= lastPage}
              onClick={() => setPage((current) => Math.min(lastPage, current + 1))}
            >
              <ChevronRight />
            </Button>
          </div>
        </div>
      )}
    />
  );
}
