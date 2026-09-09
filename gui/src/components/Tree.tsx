import {
  createContext,
  useContext,
  type KeyboardEvent,
  type ReactNode,
} from "react";
import { ChevronDown } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";

/**
 * The workbench tree primitives, shared by every explorer-style view (the catalog tree, the schema-change
 * tree): a 28px row with depth indentation, a rotating expand chevron for branches, hover and selection
 * styling, and roving-focus keyboard navigation (ArrowUp/ArrowDown move, ArrowLeft/ArrowRight collapse and
 * expand, Enter selects). The tree itself owns no data: a consumer supplies the expansion state through
 * TreeContext and renders whatever branches it has.
 */

export interface TreeState {
  /** Every id that is currently expanded (the user's own expansion plus the auto-revealed chains). */
  expanded: ReadonlySet<string>;
  toggle: (id: string) => void;
  setOpen: (id: string, open: boolean) => void;
  selectedId: string | null;
  select: (id: string) => void;
}

export const TreeContext = createContext<TreeState | null>(null);
const DepthContext = createContext(0);

export function useTreeState(): TreeState {
  const tree = useContext(TreeContext);
  if (tree === null) {
    throw new Error("TreeNode rendered outside the catalog tree provider.");
  }
  return tree;
}

/** Pixels of indentation per tree depth level. */
const TREE_INDENT = 14;

/** The visible, keyboard-navigable rows of the tree containing `element`, in document order. */
function navigableRows(element: HTMLElement): HTMLElement[] {
  const root = element.closest('[role="tree"]');
  return root === null ? [] : [...root.querySelectorAll<HTMLElement>("[data-tree-row]")];
}

export interface TreeNodeProps {
  id: string;
  label: ReactNode;
  /** Branch content; rendered (and mounted) only while this node is expanded, so lazy leaves stay lazy. */
  children?: ReactNode;
  /** Pseudo rows (loading, empty, load-more): never selectable and skipped by keyboard navigation. */
  disabled?: boolean;
}

/**
 * One tree row plus its (conditionally rendered) children: a 28px row with the depth indent, the rotating
 * expand chevron for branches, hover and selection styling per the workbench side bar, and roving-focus
 * keyboard navigation (ArrowUp/ArrowDown move, ArrowLeft/ArrowRight collapse/expand, Enter selects).
 */
export function TreeNode({ id, label, children, disabled = false }: TreeNodeProps) {
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
        aria-disabled={disabled || undefined}
        tabIndex={disabled ? undefined : -1}
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
            : !disabled && "hover:bg-accent/60",
        )}
        style={{ paddingLeft: 8 + depth * TREE_INDENT }}
      >
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
      <span className="min-w-0 flex-1 truncate">{text}</span>
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

