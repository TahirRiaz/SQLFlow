import {
  createContext,
  useContext,
  type FocusEvent,
  type KeyboardEvent,
  type ReactNode,
} from "react";
import { ChevronDown } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

/**
 * The workbench tree primitives, shared by every explorer-style view (the catalog tree, the schema-change
 * tree) and by the side bar's menu: a 28px row with depth indentation and a guide line under each ancestor,
 * a rotating expand chevron for branches, hover and selection styling, and roving-focus keyboard navigation
 * (ArrowUp/ArrowDown move, ArrowLeft/ArrowRight collapse and expand, Enter selects) inside one tab stop. The
 * tree itself owns no data: a consumer supplies the expansion state to TreeRoot and renders whatever
 * branches it has.
 */

export interface TreeState {
  /** Every id that is currently expanded (the user's own expansion plus the auto-revealed chains). */
  expanded: ReadonlySet<string>;
  toggle: (id: string) => void;
  setOpen: (id: string, open: boolean) => void;
  selectedId: string | null;
  select: (id: string) => void;
  /**
   * A navigation tree (a menu of pages): the selected row is the page shown, so it is announced as the
   * current page (`aria-current="page"`) as well as selected.
   */
  navigation?: boolean;
}

export const TreeContext = createContext<TreeState | null>(null);
const DepthContext = createContext(0);

export function useTreeState(): TreeState {
  const tree = useContext(TreeContext);
  if (tree === null) {
    throw new Error("TreeNode rendered outside a TreeRoot.");
  }
  return tree;
}

/** Pixels of indentation per tree depth level. */
const TREE_INDENT = 14;
/** Pixels from a row's edge to its depth-0 content (the chevron, or the space a leaf keeps for one). */
const TREE_INSET = 8;
/** Pixels from a level's indent to its guide line: under the middle of that level's 16px chevron. */
const GUIDE_OFFSET = 7;

/** The visible, keyboard-navigable rows of the tree containing `element`, in document order. */
function navigableRows(element: HTMLElement): HTMLElement[] {
  const root = element.closest('[role="tree"]');
  return root === null ? [] : [...root.querySelectorAll<HTMLElement>("[data-tree-row]")];
}

export interface TreeRootProps {
  /** The tree's accessible name. */
  label: string;
  /** The expansion and selection every row of the tree reads. */
  state: TreeState;
  children: ReactNode;
  className?: string;
  testId?: string;
}

/**
 * A tree's root: one tab stop for the whole tree, and the state its rows read. Focus arriving from outside
 * (Tab) moves to the selected row, or to the first one when the selected row is not shown; focus coming back
 * from one of its own rows (Shift+Tab) stays on the root, so the next Shift+Tab leaves the tree rather than
 * being sent back into it.
 */
export function TreeRoot({ label, state, children, className, testId }: TreeRootProps) {
  const enterTree = (event: FocusEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget || event.currentTarget.contains(event.relatedTarget)) {
      return;
    }
    const rows = [...event.currentTarget.querySelectorAll<HTMLElement>("[data-tree-row]")];
    (rows.find((row) => row.dataset.id === state.selectedId) ?? rows[0])?.focus();
  };

  return (
    <div
      role="tree"
      aria-label={label}
      tabIndex={0}
      onFocus={enterTree}
      className={cn("outline-none", className)}
      data-testid={testId}
    >
      <TreeContext.Provider value={state}>{children}</TreeContext.Provider>
    </div>
  );
}

export interface TreeNodeProps {
  id: string;
  label: ReactNode;
  /** Branch content; rendered (and mounted) only while this node is expanded, so lazy leaves stay lazy. */
  children?: ReactNode;
  /** Pseudo rows (loading, empty, load-more): never selectable and skipped by keyboard navigation. */
  disabled?: boolean;
  /** Test hook on the row itself, for a tree whose rows a suite drives by kind (a folder, a flow, a file). */
  testId?: string;
}

/**
 * One tree row plus its (conditionally rendered) children: a 28px row with the depth indent and a hairline
 * guide under each ancestor's chevron (the guides of consecutive rows join, so a branch's extent reads at a
 * glance), the rotating expand chevron for branches, hover and selection styling per the workbench side bar,
 * and roving-focus keyboard navigation (ArrowUp/ArrowDown move, ArrowLeft/ArrowRight collapse/expand, Enter
 * selects).
 */
export function TreeNode({ id, label, children, disabled = false, testId }: TreeNodeProps) {
  const tree = useTreeState();
  const depth = useContext(DepthContext);
  const hasChildren = children !== undefined;
  const isExpanded = hasChildren && tree.expanded.has(id);
  // Pseudo rows (loading, empty, load-more) carry a "#" suffix and are not selectable nodes.
  const selectable = !disabled && !id.includes("#");
  const isSelected = selectable && tree.selectedId === id;

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.target !== event.currentTarget) {
      // Keys inside embedded controls (the load-more button) belong to them, not to tree navigation.
      return;
    }
    const rows = navigableRows(event.currentTarget);
    const index = rows.indexOf(event.currentTarget);
    switch (event.key) {
      case "ArrowDown":
        rows[index + 1]?.focus();
        break;
      case "ArrowUp":
        rows[index - 1]?.focus();
        break;
      case "ArrowRight":
        if (hasChildren && !isExpanded) {
          tree.setOpen(id, true);
        } else if (hasChildren) {
          rows[index + 1]?.focus();
        }
        break;
      case "ArrowLeft":
        if (isExpanded) {
          tree.setOpen(id, false);
        } else {
          // Walk to the parent row: the nearest previous row one level shallower.
          for (let i = index - 1; i >= 0; i--) {
            if (Number(rows[i].dataset.depth) < depth) {
              rows[i].focus();
              break;
            }
          }
        }
        break;
      case "Enter":
        if (selectable) {
          tree.select(id);
        }
        break;
      default:
        return;
    }
    event.preventDefault();
  };

  return (
    <>
      <div
        role="treeitem"
        aria-expanded={hasChildren ? isExpanded : undefined}
        aria-selected={selectable ? isSelected : undefined}
        aria-current={tree.navigation === true && isSelected ? "page" : undefined}
        aria-disabled={disabled || undefined}
        tabIndex={disabled ? undefined : -1}
        data-testid={testId}
        data-tree-row={disabled ? undefined : ""}
        data-depth={disabled ? undefined : depth}
        data-id={disabled ? undefined : id}
        onClick={disabled
          ? undefined
          : () => {
            if (hasChildren) {
              tree.toggle(id);
            }
            if (selectable) {
              tree.select(id);
            }
          }}
        onKeyDown={disabled ? undefined : onKeyDown}
        className={cn(
          "relative flex h-7 min-w-0 items-center gap-1.5 rounded-md pr-2 text-[13px] focus:outline-none focus-visible:ring-2 focus-visible:ring-ring/60",
          disabled ? "text-muted-foreground" : "cursor-pointer select-none",
          isSelected
            ? "bg-sidebar-accent font-medium text-sidebar-accent-foreground"
            : !disabled && "hover:bg-sidebar-accent/60",
        )}
        style={{ paddingLeft: TREE_INSET + depth * TREE_INDENT }}
      >
        {Array.from({ length: depth }, (_, level) => (
          <span
            key={level}
            aria-hidden
            className="pointer-events-none absolute inset-y-0 w-px bg-border"
            style={{ left: TREE_INSET + level * TREE_INDENT + GUIDE_OFFSET }}
          />
        ))}
        {isSelected && <span className="absolute left-0 top-1/2 h-4 w-0.5 -translate-y-1/2 rounded-r bg-primary" />}
        {hasChildren
          ? (
            <ChevronDown
              className={cn(
                "size-4 shrink-0 text-muted-foreground transition-transform duration-120",
                !isExpanded && "-rotate-90",
              )}
            />
          )
          : <span className="size-4 shrink-0" />}
        {label}
      </div>
      {isExpanded && (
        <div role="group">
          <DepthContext.Provider value={depth + 1}>{children}</DepthContext.Provider>
        </div>
      )}
    </>
  );
}

/** A tree label row's content: icon, name, an optional right-aligned count, and an optional kind badge. */
export function NodeLabel({ icon, text, count, badge }: { icon?: ReactNode; text: string; count?: number; badge?: string }) {
  return (
    <>
      {icon !== undefined && <span className="inline-flex shrink-0 text-muted-foreground">{icon}</span>}
      <span className="min-w-0 flex-1 truncate" title={text}>{text}</span>
      {count !== undefined && (
        <Badge variant="outline" className="h-[18px] shrink-0 px-1.5 font-mono text-[11px] tabular-nums text-muted-foreground">
          {count}
        </Badge>
      )}
      {badge !== undefined && (
        <Badge variant="outline" className="h-[18px] shrink-0 px-1.5 text-[11px] text-muted-foreground">{badge}</Badge>
      )}
    </>
  );
}

