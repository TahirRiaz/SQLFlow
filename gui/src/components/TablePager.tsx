import type { ReactNode } from "react";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { TablePaging } from "../hooks/useTablePaging";
import { PAGE_SIZES, pageSpan } from "../lib/paging";

interface TablePagerProps extends TablePaging {
  /** Every row of the list, on every page. */
  total: number;
  /** The total is as far as the listing counts, not every row there is; the range says so with a "+". */
  capped?: boolean;
  /** What else the foot says about the rows, after the page size: when they were read, say. */
  note?: ReactNode;
  testId?: string;
}

/**
 * The foot of a paged table, inside its DataTable as the footer: how many rows a page shows, which rows of how many are
 * in view, and the previous and next page. The state comes from `useTablePaging` (rows a server pages) or `usePagedRows`
 * (rows the page holds), so every paged list reads and moves the same way.
 */
export function TablePager({ page, pageSize, total, capped = false, onPage, onPageSize, note, testId }: TablePagerProps) {
  const { from, to, lastPage } = pageSpan(total, page, pageSize);
  return (
    <div className="flex items-center justify-between gap-4 border-t border-border px-3 py-1.5" data-testid={testId}>
      <div className="flex min-w-0 items-center gap-2 text-xs text-muted-foreground">
        <span className="shrink-0">Rows per page</span>
        <Select value={String(pageSize)} onValueChange={(value) => onPageSize(Number.parseInt(value, 10))}>
          <SelectTrigger size="sm" className="h-7 w-[72px] shrink-0 text-xs" aria-label="Rows per page">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {PAGE_SIZES.map((size) => (
              <SelectItem key={size} value={String(size)}>{size}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        {note !== undefined && <span className="ml-2 min-w-0 truncate">{note}</span>}
      </div>
      <div className="flex shrink-0 items-center gap-1 text-xs text-muted-foreground">
        <span
          className="font-mono tabular-nums"
          title={capped ? "The listing counts this far; narrow the filter to reach the rest" : undefined}
          data-testid="paged-table-range"
        >
          {from}-{to} of {total}{capped ? "+" : ""}
        </span>
        <Button variant="ghost" size="icon-xs" aria-label="Previous page" disabled={page === 0} onClick={() => onPage(Math.max(0, page - 1))}>
          <ChevronLeft />
        </Button>
        <Button
          variant="ghost"
          size="icon-xs"
          aria-label="Next page"
          disabled={page >= lastPage}
          onClick={() => onPage(Math.min(lastPage, page + 1))}
        >
          <ChevronRight />
        </Button>
      </div>
    </div>
  );
}
