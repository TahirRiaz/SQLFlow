// The GUI module contract: how a module built on top of this GUI adds pages, navigation, per-kind panels, search
// categories, dashboard tiles and branding without editing the pages it extends. A module build calls renderApp with its
// modules; SQLFlow's own entry registers none, so the product renders exactly as it ships. Registration happens once,
// before the first render, and every contribution is validated then, so a conflicting module fails at startup with a
// message that names it instead of rendering a half-wired page.

import type { ComponentType, ReactNode } from "react";
import type { LucideIcon } from "lucide-react";
import type { PipelineDetail, RunDetail, RunSummary, SearchCategory } from "../api/types";
import type { Column } from "../components/DataTable";
import { extendNavigation } from "../layout/nav";
import { installModuleBranding, type BrandingContribution } from "./branding";

/** A page a module adds, rendered inside the signed-in workbench before the catch-all route. */
export interface ModuleRoute {
  /** The router path, absolute (`/reports/:reportId`). */
  path: string;
  /** The page; pass a `lazyRoute(...)` component so the page's code loads with the page. */
  component: ComponentType;
  /** A scope the session must hold; without it the route redirects to the dashboard, like the admin pages. */
  requiredScope?: string;
}

/** A navigation group a module adds to the activity bar, side bar and command palette. */
export interface ModuleNavGroup {
  id: string;
  label: string;
  icon: LucideIcon;
  requiresScope?: string;
  /** Anchored at the bottom of the activity bar, beside Settings. */
  bottom?: boolean;
  /** The id of the group this one follows; omitted, it goes after the last top group. */
  after?: string;
}

/** A navigation entry a module adds to an existing group or to one of its own groups. */
export interface ModuleNavItem {
  /** The id of the group the entry belongs to (`operate`, `workspace`, or a group a module adds). */
  group: string;
  label: string;
  to: string;
  icon: LucideIcon;
  testId: string;
  /** The `to` of the entry this one follows within its group; omitted, it is appended to the group. */
  after?: string;
}

/** The initial tab and title bar title of a detail route, before the page reports a richer one through useTabTitle. */
export interface ModuleDetailTitle {
  pattern: RegExp;
  title: (match: RegExpMatchArray) => string;
}

/** Everything one module adds to the navigation, as the navigation model receives it. */
export interface ModuleNavigation {
  moduleId: string;
  groups: readonly ModuleNavGroup[];
  items: readonly ModuleNavItem[];
  detailTitles: readonly ModuleDetailTitle[];
}

/** A tab a kind adds to the pipeline detail page, ahead of the built-in tabs. */
export interface PipelineTabContribution {
  /** The tab's value, which is also what `?tab=` names. */
  value: string;
  label: string;
  testId: string;
  render: (pipeline: PipelineDetail) => ReactNode;
}

/** A tab a kind adds to the run detail page, after the built-in tabs. */
export interface RunTabContribution {
  value: string;
  label: string;
  testId: string;
  render: (run: RunDetail) => ReactNode;
}

/** What a kind adds to the run detail page of its runs. */
export interface RunDetailContribution {
  /** Buttons beside Trace in the header. */
  headerActions?: (run: RunDetail) => ReactNode;
  /** Id chips after the built-in ones (run, repo, pipeline, group, commit). */
  headerMeta?: (run: RunDetail) => ReactNode;
  /** Detail pairs shown in place of the built-in row counts (rows loaded, inserted, updated, deleted). */
  headerDetails?: (run: RunDetail) => ReactNode;
  /** A card above the tabs: what the run was asked to do and what it reported. */
  card?: (run: RunDetail) => ReactNode;
  tabs?: readonly RunTabContribution[];
  /** Built-in tabs that say nothing about this kind's runs (`files`, `statements`, `surrogate-keys`, `assertions`, `health-metrics`). */
  hiddenTabs?: readonly string[];
}

/** The kind's part of a trigger request, as its fields report it. */
export interface TriggerBodyContribution {
  values?: Record<string, string>;
  payload?: Record<string, unknown>;
  /** The first problem with what the fields hold, which keeps the dialog from submitting; null when they are valid. */
  error: string | null;
}

/** The props the trigger dialog gives a kind's fields. */
export interface TriggerFieldsProps {
  flowKind: string;
  /** The operation picked in the dialog, from the kinds the control plane registers; null when it offers none. */
  operation: string | null;
  /** The values of the run being repeated, or of the launching context; empty for a fresh launch. */
  initialValues: Readonly<Record<string, string>>;
  /** The payload of the run being repeated, or of the launching context; null for a fresh launch. */
  initialPayload: Readonly<Record<string, unknown>> | null;
  /** Reports the kind's part of the request body whenever what the fields hold changes, and once when they mount. */
  onChange: (body: TriggerBodyContribution) => void;
}

/** What a kind adds to the trigger dialog: its fields, which also contribute their part of the request body. */
export interface TriggerContribution {
  Fields: ComponentType<TriggerFieldsProps>;
}

/** Everything a module adds for one flow kind (a `flowType` the control plane registers). */
export interface FlowKindContribution {
  kind: string;
  pipelineTabs?: readonly PipelineTabContribution[];
  /** The tab a pipeline of this kind opens on when the URL names none; the YAML tab otherwise. */
  defaultPipelineTab?: string;
  /** Built-in pipeline tabs that say nothing about this kind (`transforms`, `files`). */
  hiddenPipelineTabs?: readonly string[];
  /** Extra columns on run lists of this kind: the pipeline's Runs tab, and the runs board filtered to the kind. */
  runColumns?: readonly Column<RunSummary>[];
  run?: RunDetailContribution;
  trigger?: TriggerContribution;
}

/** A category a module adds to the combined search, read from the category of the same key in its result. */
export interface SearchCategoryContribution {
  key: string;
  render: (props: { query: string; category: SearchCategory<unknown> }) => ReactNode;
}

/** A tile a module adds to the dashboard's headline grid. */
export interface DashboardTileContribution {
  id: string;
  component: ComponentType;
}

/** A module: the unit a build registers. */
export interface GuiModule {
  /** Names the module in registration errors. */
  id: string;
  routes?: readonly ModuleRoute[];
  navGroups?: readonly ModuleNavGroup[];
  navItems?: readonly ModuleNavItem[];
  detailTitles?: readonly ModuleDetailTitle[];
  kinds?: readonly FlowKindContribution[];
  searchCategories?: readonly SearchCategoryContribution[];
  dashboardTiles?: readonly DashboardTileContribution[];
  /** The product's branding; at most one registered module may set it. */
  branding?: BrandingContribution;
}

interface Registered {
  routes: readonly ModuleRoute[];
  kinds: ReadonlyMap<string, FlowKindContribution>;
  searchCategories: readonly SearchCategoryContribution[];
  dashboardTiles: readonly DashboardTileContribution[];
}

const NONE: Registered = { routes: [], kinds: new Map(), searchCategories: [], dashboardTiles: [] };

let registered: Registered | null = null;

function claim(seen: Map<string, string>, key: string, moduleId: string, what: string): void {
  const owner = seen.get(key);
  if (owner !== undefined) {
    throw new Error(`GUI module '${moduleId}' adds ${what} '${key}', which module '${owner}' already adds.`);
  }

  seen.set(key, moduleId);
}

/**
 * Installs the modules of this build. Runs once, before the app renders: the navigation, routes and per-kind panels are
 * read while rendering, so a module registered later would never be seen.
 */
export function registerModules(modules: readonly GuiModule[]): void {
  if (registered !== null) {
    throw new Error("GUI modules are already registered; registerModules runs once, before the app renders.");
  }

  const moduleIds = new Set<string>();
  const routePaths = new Map<string, string>();
  const kindOwners = new Map<string, string>();
  const categoryOwners = new Map<string, string>();
  const tileOwners = new Map<string, string>();
  const routes: ModuleRoute[] = [];
  const kinds = new Map<string, FlowKindContribution>();
  const searchCategories: SearchCategoryContribution[] = [];
  const dashboardTiles: DashboardTileContribution[] = [];

  for (const module of modules) {
    if (module.id.trim() === "") {
      throw new Error("A GUI module needs a non-blank id.");
    }

    if (moduleIds.has(module.id)) {
      throw new Error(`The GUI module '${module.id}' is registered twice.`);
    }

    moduleIds.add(module.id);

    for (const route of module.routes ?? []) {
      if (!route.path.startsWith("/")) {
        throw new Error(`GUI module '${module.id}' adds the route '${route.path}', which is not an absolute path.`);
      }

      claim(routePaths, route.path, module.id, "the route");
      routes.push(route);
    }

    for (const kind of module.kinds ?? []) {
      claim(kindOwners, kind.kind, module.id, "the flow kind");
      const tabValues = new Set<string>();
      for (const tab of kind.pipelineTabs ?? []) {
        if (tabValues.has(tab.value)) {
          throw new Error(`GUI module '${module.id}' adds the pipeline tab '${tab.value}' to kind '${kind.kind}' twice.`);
        }

        tabValues.add(tab.value);
      }

      kinds.set(kind.kind, kind);
    }

    for (const category of module.searchCategories ?? []) {
      claim(categoryOwners, category.key, module.id, "the search category");
      searchCategories.push(category);
    }

    for (const tile of module.dashboardTiles ?? []) {
      claim(tileOwners, tile.id, module.id, "the dashboard tile");
      dashboardTiles.push(tile);
    }
  }

  extendNavigation(modules.map((module) => ({
    moduleId: module.id,
    groups: module.navGroups ?? [],
    items: module.navItems ?? [],
    detailTitles: module.detailTitles ?? [],
  })));
  installModuleBranding(modules.flatMap((module) => (
    module.branding === undefined ? [] : [{ moduleId: module.id, branding: module.branding }]
  )));

  registered = { routes, kinds, searchCategories, dashboardTiles };
}

/** The pages the registered modules add. */
export function moduleRoutes(): readonly ModuleRoute[] {
  return (registered ?? NONE).routes;
}

/** What a registered module adds for this flow kind, if any. */
export function kindContribution(kind: string | null | undefined): FlowKindContribution | undefined {
  return kind === null || kind === undefined ? undefined : (registered ?? NONE).kinds.get(kind);
}

/** The flow kinds the registered modules contribute to, in registration order. */
export function contributedKinds(): readonly string[] {
  return [...(registered ?? NONE).kinds.keys()];
}

/** The search categories the registered modules add, in registration order. */
export function moduleSearchCategories(): readonly SearchCategoryContribution[] {
  return (registered ?? NONE).searchCategories;
}

/** The dashboard tiles the registered modules add, in registration order. */
export function moduleDashboardTiles(): readonly DashboardTileContribution[] {
  return (registered ?? NONE).dashboardTiles;
}
