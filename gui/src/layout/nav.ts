import {
  Activity,
  Bell,
  BookOpen,
  BotMessageSquare,
  Cable,
  CalendarClock,
  CalendarRange,
  CirclePlay,
  Compass,
  GitCompare,
  Database,
  FolderGit2,
  Gauge,
  HeartPulse,
  KeyRound,
  KeySquare,
  LayoutDashboard,
  MonitorPlay,
  Network,
  Radar,
  Search,
  Server,
  Settings,
  ShieldCheck,
  Users,
  Workflow,
  Wrench,
  type LucideIcon,
} from "lucide-react";

export interface NavItem {
  label: string;
  to: string;
  icon: LucideIcon;
  testId: string;
}

export interface NavGroup {
  id: string;
  label: string;
  icon: LucideIcon;
  items: NavItem[];
  /** Renders only for sessions holding this scope (the Admin group). */
  requiresScope?: string;
  /** Anchored at the bottom of the activity bar (the Settings group), like VS Code's gear. */
  bottom?: boolean;
}

/** The navigation model: one source of truth for the activity bar, the side bar, the mobile sheet,
 * the command palette, and tab titles. */
export const navGroups: NavGroup[] = [
  {
    id: "operate",
    label: "Operate",
    icon: Activity,
    items: [
      { label: "Dashboard", to: "/", icon: LayoutDashboard, testId: "nav-dashboard" },
      { label: "Assistant", to: "/chat", icon: BotMessageSquare, testId: "nav-chat" },
      { label: "Insights", to: "/insights", icon: Gauge, testId: "nav-insights" },
      { label: "Data streams", to: "/datastreams", icon: HeartPulse, testId: "nav-datastreams" },
      { label: "Runs", to: "/runs", icon: CirclePlay, testId: "nav-runs" },
      { label: "Nodes", to: "/nodes", icon: Server, testId: "nav-nodes" },
    ],
  },
  {
    id: "workspace",
    label: "Workspace",
    icon: FolderGit2,
    items: [
      { label: "Repos", to: "/repos", icon: FolderGit2, testId: "nav-repos" },
      { label: "Pipelines", to: "/pipelines", icon: Workflow, testId: "nav-pipelines" },
      { label: "Schedules", to: "/schedules", icon: CalendarClock, testId: "nav-schedules" },
      { label: "Timeline", to: "/schedules/timeline", icon: CalendarRange, testId: "nav-schedule-timeline" },
      { label: "Subscribers", to: "/subscribers", icon: MonitorPlay, testId: "nav-subscribers" },
    ],
  },
  {
    id: "tools",
    label: "Tools",
    icon: Wrench,
    items: [
      { label: "Datasources", to: "/datasources", icon: Database, testId: "nav-datasources" },
      { label: "Discover", to: "/discover", icon: Radar, testId: "nav-discover" },
      { label: "Integrations", to: "/integrations", icon: Cable, testId: "nav-integrations" },
      { label: "Key detection", to: "/key-detection", icon: KeyRound, testId: "nav-key-detection" },
    ],
  },
  {
    id: "explore",
    label: "Explore",
    icon: Compass,
    items: [
      { label: "Catalog", to: "/catalog", icon: BookOpen, testId: "nav-catalog" },
      { label: "Schema changes", to: "/schema-changes", icon: GitCompare, testId: "nav-schema-changes" },
      { label: "Lineage", to: "/lineage", icon: Network, testId: "nav-lineage" },
      { label: "Search", to: "/search", icon: Search, testId: "nav-search" },
    ],
  },
  {
    id: "admin",
    label: "Admin",
    icon: ShieldCheck,
    requiresScope: "admin",
    items: [
      { label: "Users", to: "/users", icon: Users, testId: "nav-users" },
    ],
  },
  {
    id: "settings",
    label: "Settings",
    icon: Settings,
    bottom: true,
    items: [
      { label: "Access tokens", to: "/settings/tokens", icon: KeySquare, testId: "nav-settings-tokens" },
      { label: "Notifications", to: "/settings/notifications", icon: Bell, testId: "nav-settings-notifications" },
      { label: "Maintenance", to: "/settings/maintenance", icon: Wrench, testId: "nav-settings-maintenance" },
    ],
  },
];

/** Every nav item across the groups, in display order. */
export function allNavItems(): NavItem[] {
  return navGroups.flatMap((group) => group.items);
}

/**
 * Exactly one item matches: the one whose path is the longest prefix of the current location, so a nested
 * route (e.g. /schedules/timeline) owns the highlight instead of also lighting up its parent (/schedules).
 * Returns the empty string when nothing matches (e.g. /login).
 */
export function selectedNavPath(pathname: string): string {
  return allNavItems().reduce((best, item) => {
    const matches = item.to === "/" ? pathname === "/" : pathname.startsWith(item.to);
    return matches && item.to.length > best.length ? item.to : best;
  }, "");
}

/** The group owning the current location (via the longest-prefix item), defaulting to Operate. */
export function groupForPath(pathname: string): NavGroup {
  const selected = selectedNavPath(pathname);
  return navGroups.find((group) => group.items.some((item) => item.to === selected)) ?? navGroups[0];
}

/** Detail routes whose tab title carries the entity, before the page reports a richer one via useTabTitle. */
const detailTitles: Array<[RegExp, (match: RegExpMatchArray) => string]> = [
  [/^\/runs\/groups\/([^/]+)/, (m) => `Group ${m[1].slice(0, 8)}`],
  [/^\/runs\/([^/]+)/, (m) => `Run ${m[1].slice(0, 8)}`],
  [/^\/repos\/[^/]+/, () => "Repository"],
  [/^\/pipelines\/[^/]+/, () => "Pipeline"],
  [/^\/datasources\/browse/, () => "Browse data"],
  [/^\/lineage\/objects/, () => "Lineage objects"],
];

/** The initial title and icon for a tab at this path; detail pages refine the title once their data loads. */
export function routeTitle(pathname: string): { title: string; icon: LucideIcon } {
  for (const [pattern, title] of detailTitles) {
    const match = pathname.match(pattern);
    if (match) {
      return { title: title(match), icon: groupForPath(pathname).icon };
    }
  }

  const selected = selectedNavPath(pathname);
  const item = allNavItems().find((candidate) => candidate.to === selected);
  return item ? { title: item.label, icon: item.icon } : { title: "SQLFlow", icon: LayoutDashboard };
}
