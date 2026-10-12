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
  Sparkles,
  Users,
  Workflow,
  Wrench,
  type LucideIcon,
} from "lucide-react";
import { branding } from "../modules/branding";
import type { ModuleNavigation } from "../modules/registry";

export interface NavItem {
  label: string;
  to: string;
  icon: LucideIcon;
  testId: string;
  /** The id of the group's section the entry is listed under; none, it is listed before the group's first section. */
  section?: string;
  /**
   * Paths the entry owns besides its own: pages reached from it rather than from the menu (a tool, an editor), which
   * light the entry while they are shown and take its title until they report one of their own.
   */
  owns?: string[];
}

/** A labelled part of a group, for a group whose entries are of more than one sort. */
export interface NavSection {
  id: string;
  label: string;
}

export interface NavGroup {
  id: string;
  label: string;
  icon: LucideIcon;
  items: NavItem[];
  /** The sections the group's entries are listed under, in this order; none, the entries are one list. */
  sections?: NavSection[];
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
      { label: "AI context", to: "/semantic-layer", icon: Sparkles, testId: "nav-semantic-layer" },
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

/** A group's entries as they are listed: those of no section first, then each section's in the group's order. */
export interface NavListing {
  /** Null for the entries of no section, which are listed without a label. */
  section: NavSection | null;
  items: NavItem[];
}

/** A group's entries part by part, for the side bar and the command palette; a section with no entries is left out. */
export function navListing(group: NavGroup): NavListing[] {
  const parts: NavListing[] = [
    { section: null, items: group.items.filter((item) => item.section === undefined) },
    ...(group.sections ?? []).map((section) => ({ section, items: group.items.filter((item) => item.section === section.id) })),
  ];
  return parts.filter((part) => part.items.length > 0);
}

/** Every path that selects an entry: each entry's own and those it owns. */
function claimedPaths(): string[] {
  return allNavItems().flatMap((item) => [item.to, ...(item.owns ?? [])]);
}

/**
 * Exactly one item matches: the one with the longest path (its own, or one it owns) that prefixes the current location,
 * so a nested route (e.g. /schedules/timeline) owns the highlight instead of also lighting up its parent (/schedules).
 * Returns the matching item's own path, or the empty string when nothing matches (e.g. /login).
 */
export function selectedNavPath(pathname: string): string {
  let selected = "";
  let longest = 0;
  for (const item of allNavItems()) {
    for (const path of [item.to, ...(item.owns ?? [])]) {
      const matches = path === "/" ? pathname === "/" : pathname.startsWith(path);
      if (matches && path.length > longest) {
        selected = item.to;
        longest = path.length;
      }
    }
  }

  return selected;
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
  return item ? { title: item.label, icon: item.icon } : { title: branding().productName, icon: LayoutDashboard };
}

/**
 * Adds the navigation of the registered GUI modules: their groups and the sections they declare, their entries (into
 * existing groups or their own), and the initial titles of their detail routes. Called once by registerModules, before
 * the app renders; a group, section, entry or owned path that already exists, or an anchor or section that does not,
 * fails with a message naming the module.
 */
export function extendNavigation(contributions: readonly ModuleNavigation[]): void {
  for (const contribution of contributions) {
    const fail = (message: string): never => {
      throw new Error(`GUI module '${contribution.moduleId}' ${message}`);
    };

    for (const group of contribution.groups) {
      if (navGroups.some((existing) => existing.id === group.id)) {
        fail(`adds the navigation group '${group.id}', which already exists.`);
      }

      const sections: NavSection[] = [];
      for (const section of group.sections ?? []) {
        if (section.id.trim() === "" || section.label.trim() === "") {
          fail(`declares a section of the navigation group '${group.id}' without an id or a label.`);
        }

        if (sections.some((existing) => existing.id === section.id)) {
          fail(`declares the section '${section.id}' of the navigation group '${group.id}' twice.`);
        }

        sections.push({ id: section.id, label: section.label });
      }

      const entry: NavGroup = {
        id: group.id,
        label: group.label,
        icon: group.icon,
        items: [],
        sections: sections.length > 0 ? sections : undefined,
        requiresScope: group.requiresScope,
        bottom: group.bottom,
      };
      if (group.after !== undefined) {
        const anchor = navGroups.findIndex((existing) => existing.id === group.after);
        if (anchor < 0) {
          fail(`places the navigation group '${group.id}' after '${group.after}', which does not exist.`);
        }

        navGroups.splice(anchor + 1, 0, entry);
      } else {
        // Without an anchor a top group goes after the last top group, so the bottom groups stay anchored at the end.
        const firstBottom = navGroups.findIndex((existing) => existing.bottom === true);
        navGroups.splice(group.bottom === true || firstBottom < 0 ? navGroups.length : firstBottom, 0, entry);
      }
    }

    for (const item of contribution.items) {
      const group = navGroups.find((existing) => existing.id === item.group)
        ?? fail(`adds the navigation entry '${item.to}' to the group '${item.group}', which does not exist.`);
      if (claimedPaths().includes(item.to)) {
        fail(`adds the navigation entry '${item.to}', which already exists.`);
      }

      if (item.section !== undefined && !(group.sections ?? []).some((section) => section.id === item.section)) {
        fail(`places the navigation entry '${item.to}' in the section '${item.section}', which the group '${item.group}' does not declare.`);
      }

      const owns = [...(item.owns ?? [])];
      for (const path of owns) {
        if (!path.startsWith("/") || path === "/") {
          fail(`gives the navigation entry '${item.to}' the path '${path}' to own, which is not a path below the root.`);
        }

        if (path === item.to || claimedPaths().includes(path) || owns.indexOf(path) !== owns.lastIndexOf(path)) {
          fail(`gives the navigation entry '${item.to}' the path '${path}' to own, which an entry already has.`);
        }
      }

      const entry: NavItem = {
        label: item.label,
        to: item.to,
        icon: item.icon,
        testId: item.testId,
        section: item.section,
        owns: owns.length > 0 ? owns : undefined,
      };
      if (item.after !== undefined) {
        const anchor = group.items.findIndex((existing) => existing.to === item.after);
        if (anchor < 0) {
          fail(`places the navigation entry '${item.to}' after '${item.after}', which is not in the group '${item.group}'.`);
        }

        group.items.splice(anchor + 1, 0, entry);
      } else {
        group.items.push(entry);
      }
    }

    for (const detail of contribution.detailTitles) {
      detailTitles.push([detail.pattern, detail.title]);
    }
  }
}
