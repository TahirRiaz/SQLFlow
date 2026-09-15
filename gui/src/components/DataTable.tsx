import { Fragment, useState, type CSSProperties, type ReactNode } from "react";
import { ChevronDown } from "lucide-react";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { cn } from "@/lib/utils";
import { EmptyState } from "./EmptyState";

export interface Column<T> {
  id: string;
  header: string;
  render: (row: T) => ReactNode;
  align?: "left" | "right" | "center";
  width?: number | string;
  /**
   * Takes the width the other columns leave and clips its content to it, instead of pushing the table wider than its
   * panel. Meant for a long free-text column (a kind, a path, a description) whose content clips itself (TruncatedText
   * and the like); several fill columns share what is left. A table with a fill column needs no `minWidth`.
   */
  fill?: boolean;
  /** For a fill column, the width in pixels it never gives up, so a squeezed table keeps enough of it to read. */
  floor?: number;
}

/** One nesting level of a {@link TableGrouping}: CONTIGUOUS rows sharing this level's key nest under one
 * expandable node. */
export interface GroupLevel<T> {
  key: (row: T) => string;
  /** Node content for one cluster at this level; receives every row of the cluster (for aggregates). */
  renderHeader: (rows: T[]) => ReactNode;
  /** Whether this level's nodes start collapsed (a busy top level a user drills into) rather than expanded.
   * A user's expand/collapse still overrides it per node, and that override survives data refreshes. */
  defaultCollapsed?: boolean;
}

/**
 * Tree grouping over the rows given: an ordered list of nesting LEVELS above the leaf rows. At each level,
 * CONTIGUOUS rows sharing that level's key nest under one expandable node, indented one step past its parent;
 * leaf (data) rows sit deepest. Contiguity is the whole contract: rows must already be in display order, so a
 * time-ordered or server-ordered list reconstructs its hierarchy without the table re-sorting it. `transform`
 * runs once over the full row set before grouping, the hook a page uses to sort its rows into that display
 * order (schedule -> batch -> step, say) and to derive per-row grouping the raw rows do not carry. The levels
 * mirror the batch report's batch -> step -> run hierarchy, with any number of levels.
 */
export interface TableGrouping<T> {
  levels: GroupLevel<T>[];
  transform?: (rows: T[]) => T[];
}

/**
 * Row selection for the tables whose rows an action operates on in bulk. The selected set is held by the page,
 * not the table, so it survives paging and filter changes and the page can act on rows that are no longer
 * rendered. Keys are what `rowKey` returns, stringified. Rows `isSelectable` rejects render a disabled box, so a
 * row an action cannot touch says so instead of silently ignoring the click.
 */
export interface RowSelection<T> {
  selected: ReadonlySet<string>;
  onChange: (next: ReadonlySet<string>) => void;
  isSelectable?: (row: T) => boolean;
}

interface DataTableProps<T> {
  columns: Column<T>[];
  /** The rows to render; `undefined` means loading, which draws skeleton rows. */
  rows: T[] | undefined;
  rowKey: (row: T) => string | number;
  onRowClick?: (row: T) => void;
  /** Which rows respond to clicks (hover + pointer + onRowClick); defaults to all when onRowClick is set. */
  rowClickable?: (row: T) => boolean;
  /** Optional per-row style (a failed statement tints red, say); return undefined for the default styling. */
  rowSx?: (row: T) => CSSProperties | undefined;
  emptyMessage: string;
  grouping?: TableGrouping<T>;
  /** Adds a leading checkbox column; the page owns the selected set. Not combined with grouping. */
  selection?: RowSelection<T>;
  /** Rendered inside the bordered surface, above the table: the selection toolbar lives here. */
  toolbar?: ReactNode;
  /** Rendered inside the bordered surface, below the table (the PagedTable pagination lives here). */
  footer?: ReactNode;
  /** The width below which the table SCROLLS instead of compressing, in pixels. The table is w-full by
   * default, so a column set wider than its container has nowhere to go: the columns crush and their content
   * clips rather than the container scrolling. Set this on any table with enough columns to run out of room. */
  minWidth?: number;
  skeletonRows?: number;
  "data-testid"?: string;
}

interface Cluster<T> {
  key: string;
  rows: T[];
}

/** Splits rows into clusters of CONTIGUOUS equal keys, preserving row order. */
function clusterContiguous<T>(rows: T[], key: (row: T) => string): Cluster<T>[] {
  const clusters: Cluster<T>[] = [];
  for (const row of rows) {
    const k = key(row);
    const last = clusters.at(-1);
    if (last !== undefined && last.key === k) {
      last.rows.push(row);
    } else {
      clusters.push({ key: k, rows: [row] });
    }
  }

  return clusters;
}

/** One indentation step per tree depth, in pixels; a leaf sits one step past the deepest node. */
const TREE_INDENT = 28;

const alignClass = (align: Column<never>["align"]) =>
  align === "right" ? "text-right" : align === "center" ? "text-center" : undefined;

/**
 * The presentational table shell every list renders through (DESIGN.md 7.2): the bordered card surface,
 * the muted header row, loading skeletons, the shared empty state, optional row-click affordance, optional
 * row selection, and optional tree grouping (any number of independently expandable node levels above the leaf
 * rows). PagedTable wraps this with server-side paging and a query; pages holding their own already-fetched rows
 * render it directly, so there is one table code path instead of several hand-rolled shells.
 */
export function DataTable<T>({
  columns, rows, rowKey, onRowClick, rowClickable, rowSx, emptyMessage, grouping, selection, toolbar, footer, minWidth,
  skeletonRows = 5, "data-testid": testId,
}: DataTableProps<T>) {
  // Node ids the user has FLIPPED from their level's default (expanded or collapsed), keyed by the path of
  // cluster keys from the root, so the state survives refreshes of the same data (a genuinely new node gets a
  // new id and takes its level default, and a flip outlives a new row landing under the node). Every tree level
  // shares this one set; a node's effective collapse is its level default XOR its membership here.
  const [flipped, setFlipped] = useState<ReadonlySet<string>>(new Set());

  const toggleNode = (nodeId: string) => {
    setFlipped((current) => {
      const next = new Set(current);
      if (next.has(nodeId)) {
        next.delete(nodeId);
      } else {
        next.add(nodeId);
      }

      return next;
    });
  };

  const clickable = (row: T) => onRowClick !== undefined && (rowClickable?.(row) ?? true);

  // The rows on this page a selection may take, and how many of them are already in it: what the header box
  // toggles, and what makes it checked, indeterminate or empty.
  const selectableKeys = selection === undefined || rows === undefined
    ? []
    : rows.filter((row) => selection.isSelectable?.(row) ?? true).map((row) => String(rowKey(row)));
  const selectedOnPage = selectableKeys.filter((key) => selection?.selected.has(key)).length;

  const toggleRow = (key: string, checked: boolean) => {
    if (selection === undefined) {
      return;
    }

    const next = new Set(selection.selected);
    if (checked) {
      next.add(key);
    } else {
      next.delete(key);
    }

    selection.onChange(next);
  };

  const togglePage = (checked: boolean) => {
    if (selection === undefined) {
      return;
    }

    const next = new Set(selection.selected);
    for (const key of selectableKeys) {
      if (checked) {
        next.add(key);
      } else {
        next.delete(key);
      }
    }

    selection.onChange(next);
  };

  // The checkbox cell swallows the click so selecting a row never also navigates to it.
  const selectCell = (row: T, rowSelection: RowSelection<T>) => {
    const key = String(rowKey(row));
    const enabled = rowSelection.isSelectable?.(row) ?? true;
    return (
      <TableCell className="w-9 px-3 py-1.5" onClick={(event) => event.stopPropagation()}>
        <Checkbox
          checked={rowSelection.selected.has(key)}
          disabled={!enabled}
          onCheckedChange={(checked) => toggleRow(key, checked === true)}
          aria-label={enabled ? "Select row" : "This row cannot be selected"}
          data-testid="row-select"
        />
      </TableCell>
    );
  };

  // A leaf (data) row: the actual record. Its first cell is indented to `depth` so it nests visibly under its
  // parent node in the tree; the remaining cells align to the grid columns like any flat row.
  const dataRow = (row: T, depth: number) => {
    const canClick = clickable(row);
    return (
      <TableRow
        key={rowKey(row)}
        onClick={canClick ? () => onRowClick!(row) : undefined}
        className={cn(canClick && "cursor-pointer hover:bg-accent/50")}
        style={rowSx?.(row)}
        data-testid="table-row"
        data-selected={selection?.selected.has(String(rowKey(row))) ? "true" : undefined}
      >
        {selection !== undefined && selectCell(row, selection)}
        {columns.map((column, i) => (
          <TableCell
            key={column.id}
            // A fill cell's zero max-width is what lets the column give up width: the table no longer sizes it to its
            // content, so it takes what remains and its content clips.
            className={cn(
              "whitespace-nowrap px-3 py-1.5 text-[13px]",
              column.fill && "w-full max-w-0 overflow-hidden",
              alignClass(column.align),
            )}
            // Only grouped leaves (depth > 0) indent under their node; a flat row keeps the default cell
            // padding so its first column lines up with the header.
            style={{
              paddingLeft: i === 0 && depth > 0 ? TREE_INDENT * depth + 12 : undefined,
              minWidth: column.fill ? column.floor : undefined,
            }}
          >
            {column.render(row)}
          </TableCell>
        ))}
      </TableRow>
    );
  };

  // An interior tree node (schedule, batch or step): an expandable row whose first cell carries the depth
  // indent, the expander chevron, and the node's summary; the summary spans the remaining columns so
  // aggregates read across. The row testid is generic by depth (`group-header-row` at the root,
  // `subgroup-header-row` for every nested level); a level's own semantic testid lives on its rendered header.
  const nodeRow = (
    nodeId: string, depth: number, isCollapsed: boolean, nodeTestId: string, content: ReactNode,
  ) => (
    <TableRow
      onClick={() => toggleNode(nodeId)}
      className="cursor-pointer hover:bg-accent/50"
      data-testid={nodeTestId}
      aria-expanded={!isCollapsed}
    >
      <TableCell colSpan={columns.length + (selection === undefined ? 0 : 1)} className="px-3 py-1">
        <div className="flex items-center gap-1" style={{ paddingLeft: TREE_INDENT * depth }}>
          <ChevronDown
            className={cn(
              "size-4 shrink-0 text-muted-foreground transition-transform duration-120",
              isCollapsed && "-rotate-90",
            )}
          />
          {content}
        </div>
      </TableCell>
    </TableRow>
  );

  // One tree level: cluster the (already display-ordered) rows by this level's key, render each cluster's node
  // row, then recurse into the deeper levels or, at the deepest, emit the leaf rows. `parentId` threads the
  // ancestor keys so every node's collapse id is its full root-to-node path.
  const renderLevel = (
    items: T[], levels: GroupLevel<T>[], depth: number, parentId: string,
  ): ReactNode =>
    clusterContiguous(items, levels[0].key).map((cluster) => {
      const nodeId = parentId === "" ? cluster.key : `${parentId}::${cluster.key}`;
      const isCollapsed = Boolean(levels[0].defaultCollapsed) !== flipped.has(nodeId);
      const deeper = levels.slice(1);
      const testId = depth === 0 ? "group-header-row" : "subgroup-header-row";
      return (
        <Fragment key={nodeId}>
          {nodeRow(nodeId, depth, isCollapsed, testId, levels[0].renderHeader(cluster.rows))}
          {!isCollapsed && (deeper.length > 0
            ? renderLevel(cluster.rows, deeper, depth + 1, nodeId)
            : cluster.rows.map((row) => dataRow(row, depth + 1)))}
        </Fragment>
      );
    });

  const groupedBody = (items: T[], group: TableGrouping<T>) =>
    renderLevel(group.transform ? group.transform(items) : items, group.levels, 0, "");

  return (
    // A named container, so a cell can let a secondary part give way when the table itself is narrow
    // (`@max-3xl/table:sr-only`), whatever the viewport.
    <Card className="@container/table gap-0 overflow-hidden rounded-lg p-0" data-testid={testId}>
      {toolbar}
      {/* The ui Table brings its own overflow-x container; minWidth is what actually gives a wide table
          something to scroll, since a w-full table would otherwise just compress its columns to fit. */}
      <Table style={minWidth === undefined ? undefined : { minWidth }}>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {selection !== undefined && (
                <TableHead className="w-9 px-3">
                  <Checkbox
                    checked={selectedOnPage > 0 && selectedOnPage === selectableKeys.length
                      ? true
                      : selectedOnPage > 0 ? "indeterminate" : false}
                    disabled={selectableKeys.length === 0}
                    onCheckedChange={(checked) => togglePage(checked === true)}
                    aria-label="Select every row on this page"
                    data-testid="select-page"
                  />
                </TableHead>
              )}
              {columns.map((column) => (
                <TableHead
                  key={column.id}
                  className={cn(
                    "h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground",
                    column.fill && "w-full",
                    alignClass(column.align),
                  )}
                  style={{ width: column.width }}
                >
                  {column.header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows === undefined && Array.from({ length: skeletonRows }, (_, i) => (
              <TableRow key={`skeleton-${i}`}>
                {selection !== undefined && <TableCell className="w-9 px-3 py-2"><Skeleton className="size-4" /></TableCell>}
                {columns.map((column) => (
                  <TableCell key={column.id} className="px-3 py-2">
                    <Skeleton className="h-4 w-full" />
                  </TableCell>
                ))}
              </TableRow>
            ))}
            {rows !== undefined && rows.length === 0 && (
              <TableRow className="hover:bg-transparent">
                {/* The empty message wraps: a cell's no-wrap default would make a sentence the table's width. */}
                <TableCell colSpan={columns.length + (selection === undefined ? 0 : 1)} className="whitespace-normal border-0 p-0">
                  <EmptyState title={emptyMessage} data-testid="empty-message" />
                </TableCell>
              </TableRow>
            )}
            {rows !== undefined && (grouping
              ? groupedBody(rows, grouping)
              : rows.map((row) => dataRow(row, 0)))}
          </TableBody>
        </Table>
      {footer}
    </Card>
  );
}
