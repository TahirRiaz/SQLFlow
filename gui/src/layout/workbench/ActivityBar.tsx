import { Menu, type LucideIcon } from "lucide-react";
import { useLocation } from "react-router-dom";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { useAuth } from "../../auth/AuthContext";
import { navGroups, navPlaceOf, navShortcuts, selectedNavPath, type NavGroup, type NavItem } from "../nav";

interface ActivityBarProps {
  /** The group owning the current route (its icon carries the active indicator). */
  activeGroupId: string;
  onSelectGroup: (group: NavGroup) => void;
  /** Whether the side bar is shown, which the side bar button of a bar of shortcuts says. */
  sideBarShown: boolean;
  onToggleSideBar: () => void;
  /** A shortcut was clicked: open its page and bring its entry into view in the side bar. */
  onOpenShortcut: (item: NavItem) => void;
}

interface ActivityButtonProps {
  label: string;
  /** The tooltip, when it says more than the label. */
  hint?: string;
  icon: LucideIcon;
  testId: string;
  /** Lit with the indicator: the group revealed, or the shortcut whose page is shown. */
  active: boolean;
  /** A shortcut whose page is shown, announced as the current page. */
  current?: boolean;
  /** The side bar button's state: whether the side bar is shown. */
  expanded?: boolean;
  onClick: () => void;
}

function ActivityButton({ label, hint, icon: Icon, testId, active, current, expanded, onClick }: ActivityButtonProps) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          aria-label={label}
          aria-current={current === true ? "page" : undefined}
          aria-expanded={expanded}
          data-testid={testId}
          onClick={onClick}
          className={cn(
            "relative flex h-11 w-12 items-center justify-center transition-colors",
            active || expanded === true
              ? "text-activity-bar-active"
              : "text-activity-bar-foreground hover:text-activity-bar-active",
          )}
        >
          {active && (
            <span className="absolute left-0 top-1/2 h-5 w-0.5 -translate-y-1/2 rounded-r bg-activity-bar-active" />
          )}
          <Icon className="size-5" />
        </button>
      </TooltipTrigger>
      <TooltipContent side="right">{hint ?? label}</TooltipContent>
    </Tooltip>
  );
}

/**
 * The VS Code-style activity bar (DESIGN.md section 6). By default one icon per nav group, Settings pinned at the bottom:
 * clicking reveals the group in the side bar, and clicking the revealed group toggles the side bar. When the menu's
 * layout names shortcuts, it is a button that shows and hides the side bar over one icon per shortcut, each opening its
 * page in one click and lit while that page is shown.
 */
export function ActivityBar({ activeGroupId, onSelectGroup, sideBarShown, onToggleSideBar, onOpenShortcut }: ActivityBarProps) {
  const { hasScope } = useAuth();
  const { pathname } = useLocation();
  const permitted = (group: NavGroup) => group.requiresScope === undefined || hasScope(group.requiresScope);

  if (navShortcuts.length > 0) {
    const selected = selectedNavPath(pathname);
    // A shortcut is shown to whoever its entry's group is shown to.
    const shortcuts = navShortcuts.filter((item) => {
      const place = navPlaceOf(item.to);
      return place !== null && permitted(place.group);
    });

    return (
      <nav aria-label="Shortcuts" className="hidden w-12 shrink-0 flex-col items-center bg-activity-bar py-1 shadow-[inset_-1px_0_0_var(--chrome-border)] md:flex">
        <ActivityButton
          label="Menu"
          hint={sideBarShown ? "Hide the menu" : "Show the menu"}
          icon={Menu}
          testId="activity-menu"
          active={false}
          expanded={sideBarShown}
          onClick={onToggleSideBar}
        />
        <div aria-hidden className="mx-auto my-1 h-px w-6 bg-activity-bar-foreground/30" />
        {shortcuts.map((item) => (
          <ActivityButton
            key={item.to}
            label={item.label}
            icon={item.icon}
            testId={`activity-${item.testId}`}
            active={item.to === selected}
            current={item.to === selected}
            onClick={() => onOpenShortcut(item)}
          />
        ))}
      </nav>
    );
  }

  const visible = navGroups.filter(permitted);
  const top = visible.filter((group) => group.bottom !== true);
  const bottom = visible.filter((group) => group.bottom === true);
  const groupButton = (group: NavGroup) => (
    <ActivityButton
      key={group.id}
      label={group.label}
      icon={group.icon}
      testId={`activity-${group.id}`}
      active={group.id === activeGroupId}
      onClick={() => onSelectGroup(group)}
    />
  );

  return (
    <div className="hidden w-12 shrink-0 flex-col items-center bg-activity-bar py-1 shadow-[inset_-1px_0_0_var(--chrome-border)] md:flex">
      {top.map(groupButton)}
      <div className="flex-1" />
      {bottom.map(groupButton)}
    </div>
  );
}
