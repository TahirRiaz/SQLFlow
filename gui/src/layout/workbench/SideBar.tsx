import { Fragment, useEffect, useState, type FormEvent } from "react";
import { useLocation } from "react-router-dom";
import { SearchInput } from "@/components/SearchInput";
import { NodeLabel, TreeNode, TreeRoot, type TreeState } from "@/components/Tree";
import { cn } from "@/lib/utils";
import { useAuth } from "../../auth/AuthContext";
import { menuCollapsedOnFirstVisit } from "../../modules/registry";
import { navGroups, navListing, selectedNavPath, type NavGroup, type NavItem, type NavSection } from "../nav";
import { menuCollapsedAtStart } from "./sideBarStart";
import { useMenuNavigate } from "./useMenuNavigate";

const SECTIONS_KEY = "sqlflow.workbench.sections";

/** A group's section is kept collapsed under the group's id and its own, apart from the group itself. */
function sectionKey(group: NavGroup, section: NavSection): string {
  return `${group.id}/${section.id}`;
}

function loadCollapsed(): ReadonlySet<string> {
  let stored: string | null;
  try {
    stored = window.localStorage.getItem(SECTIONS_KEY);
  } catch {
    // A browser that keeps nothing for the page reads as a first visit.
    stored = null;
  }

  const all = navGroups.flatMap((group) => [group.id, ...(group.sections ?? []).map((section) => sectionKey(group, section))]);
  return menuCollapsedAtStart(stored, menuCollapsedOnFirstVisit(), all);
}

interface SideBarSectionsProps {
  /** Set by the activity bar: expand this group and scroll it into view. */
  reveal: { id: string; nonce: number } | null;
  onNavigate?: () => void;
}

/**
 * The grouped navigation, as one tree on the workbench's tree primitives: every group a top-level branch
 * (so each nav item stays reachable regardless of the active activity-bar group), a group's own sections
 * branches under it that fold on their own, the entries its leaves, and the longest-prefix entry selected
 * as the page shown. Shared between the desktop side bar and the mobile sheet.
 */
export function SideBarSections({ reveal, onNavigate }: SideBarSectionsProps) {
  const { hasScope } = useAuth();
  const location = useLocation();
  const navigate = useMenuNavigate();
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(loadCollapsed);

  const selected = selectedNavPath(location.pathname);

  useEffect(() => {
    try {
      window.localStorage.setItem(SECTIONS_KEY, JSON.stringify([...collapsed]));
    } catch {
      // A browser that keeps nothing for the page folds the menu for this visit only.
    }
  }, [collapsed]);

  // The activity bar revealed a group: expand it and bring it into view.
  useEffect(() => {
    if (reveal === null) {
      return;
    }

    setCollapsed((current) => {
      if (!current.has(reveal.id)) {
        return current;
      }

      const next = new Set(current);
      next.delete(reveal.id);
      return next;
    });
    document.getElementById(`sidebar-section-${reveal.id}`)?.scrollIntoView({ block: "nearest" });
  }, [reveal]);

  const groups = navGroups.filter((group) => group.requiresScope === undefined || hasScope(group.requiresScope));
  // Only an entry's path is a page; a group's or a section's row folds and unfolds, and is never selected.
  const pages = new Set(groups.flatMap((group) => navListing(group).flatMap((part) => part.items.map((item) => item.to))));
  const branches = groups.flatMap((group) => [group.id, ...(group.sections ?? []).map((section) => sectionKey(group, section))]);

  const setOpen = (id: string, open: boolean) => {
    setCollapsed((current) => {
      if (current.has(id) !== open) {
        return current;
      }

      const next = new Set(current);
      if (open) {
        next.delete(id);
      } else {
        next.add(id);
      }

      return next;
    });
  };

  const tree: TreeState = {
    expanded: new Set(branches.filter((id) => !collapsed.has(id))),
    setOpen,
    toggle: (id) => {
      setCollapsed((current) => {
        const next = new Set(current);
        if (next.has(id)) {
          next.delete(id);
        } else {
          next.add(id);
        }

        return next;
      });
    },
    selectedId: selected,
    select: (id) => {
      if (pages.has(id)) {
        navigate(id);
        onNavigate?.();
      }
    },
    navigation: true,
  };

  const renderItem = (item: NavItem) => (
    <TreeNode
      key={item.to}
      id={item.to}
      testId={item.testId}
      label={<NodeLabel icon={<item.icon className="size-4" />} text={item.label} />}
    />
  );

  // Each group and section sits in an element of its own, so the activity bar can bring a group into view and a
  // suite can find what is under it; the tree's rows and groups inside are the primitives' own.
  const renderGroup = (group: NavGroup) => (
    <div key={group.id} id={`sidebar-section-${group.id}`}>
      <TreeNode id={group.id} label={<span className="min-w-0 flex-1 truncate font-semibold">{group.label}</span>}>
        {navListing(group).map((part) => {
          if (part.section === null) {
            return <Fragment key="">{part.items.map(renderItem)}</Fragment>;
          }

          return (
            <div key={part.section.id} data-testid={`sidebar-part-${group.id}-${part.section.id}`}>
              <TreeNode
                id={sectionKey(group, part.section)}
                label={<span className="min-w-0 flex-1 truncate font-medium">{part.section.label}</span>}
              >
                {part.items.map(renderItem)}
              </TreeNode>
            </div>
          );
        })}
      </TreeNode>
    </div>
  );

  return (
    <TreeRoot label="Pages" state={tree} className="px-1">
      {groups.map(renderGroup)}
    </TreeRoot>
  );
}

/**
 * The global catalog search, the first thing in the navigation (DESIGN.md sections 6 and 7.1): Enter takes
 * the term to the search page. It sits above the scrolling nav so it never scrolls out of reach, and is
 * shared by the desktop side bar and the mobile navigation sheet.
 */
export function SideBarSearch({ className, onNavigate }: { className?: string; onNavigate?: () => void }) {
  const navigate = useMenuNavigate();
  const [term, setTerm] = useState("");

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = term.trim();
    if (trimmed === "") {
      return;
    }

    navigate(`/search?q=${encodeURIComponent(trimmed)}`);
    onNavigate?.();
  };

  return (
    <form onSubmit={submit} className={cn("shrink-0 border-b border-sidebar-border px-2 py-2", className)}>
      <SearchInput
        value={term}
        onChange={setTerm}
        placeholder="Search catalog"
        label="Search objects, columns, and definitions"
        testId="global-search"
        className="sm:w-full"
      />
    </form>
  );
}

/** The desktop side bar surface: the global search over the grouped navigation in its own scroll container. */
export function SideBar({ reveal }: { reveal: { id: string; nonce: number } | null }) {
  return (
    <div className="flex h-full min-h-0 flex-col bg-sidebar">
      <SideBarSearch />
      <nav aria-label="Main navigation" className="min-h-0 flex-1 overflow-y-auto py-1">
        <SideBarSections reveal={reveal} />
      </nav>
    </div>
  );
}
