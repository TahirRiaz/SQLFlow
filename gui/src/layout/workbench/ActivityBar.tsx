import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { useAuth } from "../../auth/AuthContext";
import { navGroups, type NavGroup } from "../nav";

interface ActivityBarProps {
  /** The group owning the current route (its icon carries the active indicator). */
  activeGroupId: string;
  onSelectGroup: (group: NavGroup) => void;
}

function ActivityButton({
  group,
  active,
  onSelect,
}: {
  group: NavGroup;
  active: boolean;
  onSelect: (group: NavGroup) => void;
}) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          aria-label={group.label}
          data-testid={`activity-${group.id}`}
          onClick={() => onSelect(group)}
          className={cn(
            "relative flex h-11 w-12 items-center justify-center transition-colors",
            active
              ? "text-activity-bar-active"
              : "text-activity-bar-foreground hover:text-activity-bar-active",
          )}
        >
          {active && (
            <span className="absolute left-0 top-1/2 h-5 w-0.5 -translate-y-1/2 rounded-r bg-activity-bar-active" />
          )}
          <group.icon className="size-5" />
        </button>
      </TooltipTrigger>
      <TooltipContent side="right">{group.label}</TooltipContent>
    </Tooltip>
  );
}

/**
 * The VS Code-style activity bar (DESIGN.md section 6): one icon per nav group, Settings pinned at the
 * bottom. Clicking reveals the group in the side bar; clicking the revealed group toggles the side bar.
 */
export function ActivityBar({ activeGroupId, onSelectGroup }: ActivityBarProps) {
  const { hasScope } = useAuth();
  const visible = navGroups.filter((group) => group.requiresScope === undefined || hasScope(group.requiresScope));
  const top = visible.filter((group) => group.bottom !== true);
  const bottom = visible.filter((group) => group.bottom === true);

  return (
    <div className="hidden w-12 shrink-0 flex-col items-center bg-activity-bar py-1 md:flex">
      {top.map((group) => (
        <ActivityButton key={group.id} group={group} active={group.id === activeGroupId} onSelect={onSelectGroup} />
      ))}
      <div className="flex-1" />
      {bottom.map((group) => (
        <ActivityButton key={group.id} group={group} active={group.id === activeGroupId} onSelect={onSelectGroup} />
      ))}
    </div>
  );
}
