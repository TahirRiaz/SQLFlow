import { useEffect, useState, type FormEvent } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { ChevronDown } from "lucide-react";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import { useAuth } from "../../auth/AuthContext";
import { navGroups, selectedNavPath, type NavGroup } from "../nav";

const SECTIONS_KEY = "sqlflow.workbench.sections";

function loadCollapsed(): ReadonlySet<string> {
  try {
    const raw = window.localStorage.getItem(SECTIONS_KEY);
    if (raw === null) {
      return new Set();
    }

    const parsed: unknown = JSON.parse(raw);
    return new Set(Array.isArray(parsed) ? parsed.filter((id): id is string => typeof id === "string") : []);
  } catch {
    // A corrupt store just means every section starts expanded.
    return new Set();
  }
}

interface SideBarSectionsProps {
  /** Set by the activity bar: expand this group and scroll it into view. */
  reveal: { id: string; nonce: number } | null;
  onNavigate?: () => void;
}

/**
 * The grouped navigation: every group as a collapsible section (so each nav item stays reachable
 * regardless of the active activity-bar group), the longest-prefix item highlighted. Shared between
 * the desktop side bar and the mobile sheet.
 */
export function SideBarSections({ reveal, onNavigate }: SideBarSectionsProps) {
  const { hasScope } = useAuth();
  const location = useLocation();
  const navigate = useNavigate();
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(loadCollapsed);

  const selected = selectedNavPath(location.pathname);

  useEffect(() => {
    window.localStorage.setItem(SECTIONS_KEY, JSON.stringify([...collapsed]));
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

  const toggleSection = (id: string) => {
    setCollapsed((current) => {
      const next = new Set(current);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }

      return next;
    });
  };

  const renderGroup = (group: NavGroup) => {
    const isCollapsed = collapsed.has(group.id);
    return (
      <section key={group.id} id={`sidebar-section-${group.id}`}>
        <button
          onClick={() => toggleSection(group.id)}
          aria-expanded={!isCollapsed}
          className="flex h-8 w-full items-center gap-1 px-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground hover:text-foreground"
        >
          <ChevronDown className={cn("size-3.5 transition-transform duration-120", isCollapsed && "-rotate-90")} />
          {group.label}
        </button>
        {!isCollapsed && group.items.map((item) => {
          const isSelected = item.to === selected;
          return (
            <button
              key={item.to}
              data-testid={item.testId}
              onClick={() => {
                navigate(item.to);
                onNavigate?.();
              }}
              className={cn(
                "relative mx-1 flex h-7 w-[calc(100%-8px)] items-center gap-2 rounded-md px-2 text-[13px] transition-colors",
                isSelected
                  ? "bg-sidebar-accent font-medium text-sidebar-accent-foreground"
                  : "text-sidebar-foreground hover:bg-sidebar-accent/60",
              )}
            >
              {isSelected && <span className="absolute -left-1 h-4 w-0.5 rounded-r bg-primary" />}
              <item.icon className={cn("size-4 shrink-0", isSelected ? "opacity-100" : "opacity-75")} />
              <span className="truncate">{item.label}</span>
            </button>
          );
        })}
      </section>
    );
  };

  return (
    <>
      {navGroups
        .filter((group) => group.requiresScope === undefined || hasScope(group.requiresScope))
        .map(renderGroup)}
    </>
  );
}

/**
 * The global catalog search, the first thing in the navigation (DESIGN.md sections 6 and 7.1): Enter takes
 * the term to the search page. It sits above the scrolling nav so it never scrolls out of reach, and is
 * shared by the desktop side bar and the mobile navigation sheet.
 */
export function SideBarSearch({ className, onNavigate }: { className?: string; onNavigate?: () => void }) {
  const navigate = useNavigate();
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
