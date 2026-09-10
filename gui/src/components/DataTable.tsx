import { Fragment, useState, type CSSProperties, type ReactNode } from "react";
import { ChevronDown } from "lucide-react";
import { Card } from "@/components/ui/card";
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
 * the muted header row, loading skeletons, the shared empty state, optional row-click affordance, and
 * optional tree grouping (any number of independently expandable node levels above the leaf rows). PagedTable
 * wraps this with server-side paging and a query; pages holding their own already-fetched rows render it
 * directly, so there is one table code path instead of several hand-rolled shells.
 */
export function DataTable<T>({
  columns, rows, rowKey, onRowClick, rowClickable, rowSx, emptyMessage, grouping, footer, minWidth,
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
      >
        {columns.map((column, i) => (
          <TableCell
            key={column.id}
            className={cn("whitespace-nowrap px-3 py-1.5 text-[13px]", alignClass(column.align))}
            // Only grouped leaves (depth > 0) indent under their node; a flat row keeps the default cell
            // padding so its first column lines up with the header.
            style={i === 0 && depth > 0 ? { paddingLeft: TREE_INDENT * depth + 12 } : undefined}
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
      <TableCell colSpan={columns.length} className="px-3 py-1">
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
    <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid={testId}>
      {/* The ui Table brings its own overflow-x container; minWidth is what actually gives a wide table
          something to scroll, since a w-full table would otherwise just compress its columns to fit. */}
      <Table style={minWidth === undefined ? undefined : { minWidth }}>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {columns.map((column) => (
                <TableHead
                  key={column.id}
                  className={cn(
                    "h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground",
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
                {columns.map((column) => (
                  <TableCell key={column.id} className="px-3 py-2">
                    <Skeleton className="h-4 w-full" />
                  </TableCell>
                ))}
              </TableRow>
            ))}
            {rows !== undefined && rows.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={columns.length} className="border-0 p-0">
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
