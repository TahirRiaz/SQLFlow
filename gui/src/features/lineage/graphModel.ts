import type { ProjectGraph } from "../../api/types.ts";

/**
 * The lineage graph as the page draws it, derived from the one payload the server sends. The payload is the whole
 * scope (a repository, a project, or a node's neighbourhood); what reaches the canvas is that scope after the
 * reductions the reader chose, applied in a fixed order so each control has one predictable effect:
 *
 *   1. level    how much data is drawn beside the flows (none, the objects two flows hand over, everything)
 *   2. kinds    node kinds the reader hid; a hidden node is bridged, so the chain through it stays connected
 *   3. waves    flows outside the chosen waves are dropped (no bridging: a wave range trims the drawing)
 *   4. folders  the flows of a folder draw as one node until the folder is opened (around a traced node they are open)
 *   5. trace    only what is upstream and downstream of one node, within a number of hops
 *   6. stacks   nodes of one kind with the same neighbours draw as one node until the stack is opened
 *
 * Everything here is pure: no React, no DOM, no layout. The canvas lays out and paints the result.
 */

/** How much data is drawn beside the flows. `objects` is the data-only drawing: objects joined by the flows between them. */
export type GraphLevel = "flows" | "handovers" | "all" | "objects";

export type BaseNodeType = "flow" | "object";

/** One node of the scope, as the server sent it. */
export interface BaseNode {
  id: string;
  type: BaseNodeType;
  name: string;
  /** A flow's kind (`ing`), or an object's (`table`, `view`, `file`, a dataset's system). */
  kind: string;
  /** Where it is: a flow's folder in its repository, an object's database and schema. Empty when it has none. */
  place: string;
  /** The id of the folder node a flow collapses into. Null for an object. */
  folderId: string | null;
  /** A flow's execution wave; -1 when lineage gave it none. Null for an object. */
  wave: number | null;
  repoId: string | null;
  repoName: string | null;
  /** The server stopped walking at this object: consumers exist that the payload does not hold. */
  frontier: boolean;
  /** Why this flow's reads and writes are not fully known, when they are not. */
  incompleteReason: string | null;
}

/** A directed graph over base nodes: the mixed drawing (flows and objects) or the data-only one. */
export interface BaseGraph {
  nodes: Map<string, BaseNode>;
  outgoing: Map<string, Set<string>>;
  incoming: Map<string, Set<string>>;
  /** What each edge says, by `source` + EDGE_SEPARATOR + `target`: the flow names moving data, in the data-only drawing. */
  labels: Map<string, string[]>;
}

/** Both drawings of one scope. */
export interface GraphSource {
  mixed: BaseGraph;
  data: BaseGraph;
}

export type TraceDirection = "both" | "upstream" | "downstream";

export interface TraceOptions {
  /** The node traced: a base node's id, or a collapsed folder's. */
  id: string;
  /** How many hops from it are drawn; null draws the whole trace. */
  hops: number | null;
  direction: TraceDirection;
}

export interface ViewOptions {
  level: GraphLevel;
  /** Kind keys (`flow:ing`, `object:table`) the reader hid. */
  hiddenKinds: ReadonlySet<string>;
  /** The waves drawn; null draws every wave. */
  waves: ReadonlySet<number> | null;
  /** Whether a folder's flows draw as one node. */
  grouped: boolean;
  /** The folders the reader opened. */
  openGroups: ReadonlySet<string>;
  trace: TraceOptions | null;
  /** Nodes whose hidden producers (up) or consumers (down) the reader asked back into a trace. */
  revealUp: ReadonlySet<string>;
  revealDown: ReadonlySet<string>;
  /** Whether look-alike nodes draw as one. */
  stacked: boolean;
  /** The stacks the reader opened. */
  openStacks: ReadonlySet<string>;
  /** Base nodes no reduction may hide or stack: the one the reader selected. */
  pinned: ReadonlySet<string>;
}

export type ViewNodeType = "flow" | "object" | "group" | "stack";

/** One node on the canvas: a base node, a folder of flows, or a stack of look-alikes. */
export interface ViewNode {
  id: string;
  type: ViewNodeType;
  /** What a stack holds; `flow` for a folder and for a flow, `object` for an object. */
  memberType: BaseNodeType;
  name: string;
  kind: string;
  place: string;
  /** The base nodes this one stands for; itself for a plain node. */
  members: string[];
  flowCount: number;
  objectCount: number;
  /** The distinct waves of the flows it stands for, ascending. */
  waves: number[];
  /** Objects the level folded into this flow: what it reads and what it writes that no other drawn flow touches. */
  foldedIn: number;
  foldedOut: number;
  /** Direct producers and consumers a trace left out. */
  hiddenUp: number;
  hiddenDown: number;
  /** Frontier objects the server can be asked to walk past, reached through this node. */
  frontierKeys: string[];
  incompleteReasons: string[];
  /** The repository's name, when the node stands for flows of one repository. */
  repoName: string | null;
  repoId: string | null;
  /** True for the node a trace is drawn around. */
  anchor: boolean;
}

export interface ViewEdge {
  id: string;
  source: string;
  target: string;
  /** The hidden base nodes the edge passes through, when a reduction bridged them. */
  via: string[];
  labels: string[];
  /** How many base edges it stands for. */
  weight: number;
}

export interface KindCount {
  /** `flow:ing`, `object:table`. */
  key: string;
  type: BaseNodeType;
  kind: string;
  count: number;
}

export interface View {
  nodes: ViewNode[];
  edges: ViewEdge[];
  outgoing: Map<string, string[]>;
  incoming: Map<string, string[]>;
  /** Where each base node is drawn: itself, its folder or its stack. A base node no entry names is not drawn. */
  locate: Map<string, string>;
  /** Base nodes in the scope at this level's drawing (flows and objects, or objects alone). */
  total: number;
  /** Base nodes the drawn nodes stand for. */
  represented: number;
  /** The trace named a node the current reductions do not draw. */
  traceMissing: boolean;
  /** Whether the scope has folders worth collapsing: two or more flows in at least one folder. */
  groupable: boolean;
}

export const EDGE_SEPARATOR = "\u0000";

const GROUP_PREFIX = "group:";
const STACK_PREFIX = "stack:";
/** Fewer look-alikes than this stay drawn one by one: a stack of two hides two names to save one card. */
export const MIN_STACK_SIZE = 3;

export function isGroupId(id: string): boolean {
  return id.startsWith(GROUP_PREFIX);
}

export function isStackId(id: string): boolean {
  return id.startsWith(STACK_PREFIX);
}

export function kindKey(node: { type: BaseNodeType; kind: string }): string {
  return `${node.type}:${node.kind}`;
}

/** The folder a flow's document sits in, relative to its repository; empty at the repository's root. */
export function folderOf(relativePath: string): string {
  const normalized = relativePath.replace(/\\/g, "/");
  const slash = normalized.lastIndexOf("/");
  return slash > 0 ? normalized.slice(0, slash) : "";
}

function addAdjacent(map: Map<string, Set<string>>, from: string, to: string): void {
  const set = map.get(from);
  if (set) {
    set.add(to);
  } else {
    map.set(from, new Set([to]));
  }
}

function emptyBase(): BaseGraph {
  return { nodes: new Map(), outgoing: new Map(), incoming: new Map(), labels: new Map() };
}

function addBaseEdge(graph: BaseGraph, source: string, target: string, label: string): void {
  if (source === target || !graph.nodes.has(source) || !graph.nodes.has(target)) {
    return;
  }
  addAdjacent(graph.outgoing, source, target);
  addAdjacent(graph.incoming, target, source);
  if (label !== "") {
    const key = source + EDGE_SEPARATOR + target;
    const labels = graph.labels.get(key);
    if (!labels) {
      graph.labels.set(key, [label]);
    } else if (!labels.includes(label)) {
      labels.push(label);
    }
  }
}

/**
 * Reads the server's payload into the two drawings. The server already decided what a node is and which way data
 * moves (views wired to their base tables, procedures left out); nothing here re-derives that.
 */
export function buildGraphSource(payload: Pick<ProjectGraph, "pipelines" | "objects" | "flowGraph" | "objectGraph">): GraphSource {
  const objects = new Map<string, BaseNode>();
  for (const object of payload.objects) {
    objects.set(object.key, {
      id: object.key,
      type: "object",
      name: object.name,
      kind: object.kind,
      place: object.location ?? "",
      folderId: null,
      wave: null,
      repoId: null,
      repoName: null,
      frontier: object.frontier,
      incompleteReason: null,
    });
  }

  const mixed = emptyBase();
  for (const pipeline of payload.pipelines) {
    if (mixed.nodes.has(pipeline.id)) {
      continue;
    }
    const folder = folderOf(pipeline.relativePath);
    mixed.nodes.set(pipeline.id, {
      id: pipeline.id,
      type: "flow",
      name: pipeline.name,
      kind: pipeline.kind,
      place: folder,
      folderId: `${GROUP_PREFIX}${pipeline.repoId}/${folder}`,
      wave: pipeline.wave,
      repoId: pipeline.repoId,
      repoName: pipeline.repoName,
      frontier: false,
      incompleteReason: pipeline.lineageComplete === false
        ? pipeline.incompleteReason ?? "Its reads and writes could not be derived."
        : null,
    });
  }

  // An object is drawn where an edge names it. One the payload lists but no edge touches would float unattached.
  const ensureObject = (graph: BaseGraph, id: string) => {
    if (graph.nodes.has(id)) {
      return;
    }
    const known = objects.get(id);
    graph.nodes.set(id, known ?? {
      id,
      type: "object",
      name: id,
      kind: "table",
      place: "",
      folderId: null,
      wave: null,
      repoId: null,
      repoName: null,
      frontier: false,
      incompleteReason: null,
    });
  };

  for (const edge of payload.flowGraph) {
    ensureObject(mixed, edge.source);
    ensureObject(mixed, edge.target);
    addBaseEdge(mixed, edge.source, edge.target, "");
  }

  const data = emptyBase();
  for (const edge of payload.objectGraph) {
    ensureObject(data, edge.source);
    ensureObject(data, edge.target);
    addBaseEdge(data, edge.source, edge.target, edge.label);
  }

  return { mixed, data };
}

// ---- The working graph the reductions transform -------------------------------------------------------------------

interface Work {
  nodes: Map<string, ViewNode>;
  edges: Map<string, ViewEdge>;
}

function plainNode(node: BaseNode): ViewNode {
  return {
    id: node.id,
    type: node.type,
    memberType: node.type,
    name: node.name,
    kind: node.kind,
    place: node.place,
    members: [node.id],
    flowCount: node.type === "flow" ? 1 : 0,
    objectCount: node.type === "object" ? 1 : 0,
    waves: node.wave === null ? [] : [node.wave],
    foldedIn: 0,
    foldedOut: 0,
    hiddenUp: 0,
    hiddenDown: 0,
    frontierKeys: node.frontier ? [node.id] : [],
    incompleteReasons: node.incompleteReason === null ? [] : [node.incompleteReason],
    repoName: node.repoName,
    repoId: node.repoId,
    anchor: false,
  };
}

function toWork(base: BaseGraph): Work {
  const nodes = new Map<string, ViewNode>();
  for (const node of base.nodes.values()) {
    nodes.set(node.id, plainNode(node));
  }
  const edges = new Map<string, ViewEdge>();
  for (const [source, targets] of base.outgoing) {
    for (const target of targets) {
      const id = source + EDGE_SEPARATOR + target;
      edges.set(id, { id, source, target, via: [], labels: [...(base.labels.get(id) ?? [])], weight: 1 });
    }
  }
  return { nodes, edges };
}

interface Adjacency {
  outgoing: Map<string, string[]>;
  incoming: Map<string, string[]>;
}

function adjacencyOf(work: Work): Adjacency {
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  for (const id of work.nodes.keys()) {
    outgoing.set(id, []);
    incoming.set(id, []);
  }
  for (const edge of work.edges.values()) {
    outgoing.get(edge.source)?.push(edge.target);
    incoming.get(edge.target)?.push(edge.source);
  }
  return { outgoing, incoming };
}

function union<T>(into: T[], from: readonly T[]): void {
  for (const item of from) {
    if (!into.includes(item)) {
      into.push(item);
    }
  }
}

function mergeEdge(edges: Map<string, ViewEdge>, source: string, target: string, from: Pick<ViewEdge, "via" | "labels" | "weight">): void {
  if (source === target) {
    return;
  }
  const id = source + EDGE_SEPARATOR + target;
  const existing = edges.get(id);
  if (existing) {
    union(existing.via, from.via);
    union(existing.labels, from.labels);
    existing.weight += from.weight;
  } else {
    edges.set(id, { id, source, target, via: [...from.via], labels: [...from.labels], weight: from.weight });
  }
}

/**
 * Takes nodes out of the drawing and joins what they joined: for every remaining node, each remaining node it reaches
 * through hidden ones alone gets an edge, which remembers the hidden nodes on the way. A frontier object hidden this
 * way hands its key to the nodes that reach it, so the server can still be asked to walk past it.
 */
function projectOut(work: Work, hidden: ReadonlySet<string>): Work {
  if (hidden.size === 0) {
    return work;
  }
  const { outgoing } = adjacencyOf(work);
  const nodes = new Map<string, ViewNode>();
  for (const [id, node] of work.nodes) {
    if (!hidden.has(id)) {
      nodes.set(id, node);
    }
  }
  const edges = new Map<string, ViewEdge>();
  for (const edge of work.edges.values()) {
    if (!hidden.has(edge.source) && !hidden.has(edge.target)) {
      mergeEdge(edges, edge.source, edge.target, edge);
    }
  }

  for (const [start, node] of nodes) {
    const firstHidden = (outgoing.get(start) ?? []).filter((next) => hidden.has(next));
    if (firstHidden.length === 0) {
      continue;
    }
    // Breadth first through hidden nodes only; `parent` rebuilds the hidden path to each one reached.
    const parent = new Map<string, string | null>();
    const queue: string[] = [];
    for (const next of firstHidden) {
      if (!parent.has(next)) {
        parent.set(next, null);
        queue.push(next);
      }
    }
    for (let head = 0; head < queue.length; head++) {
      const current = queue[head];
      union(node.frontierKeys, work.nodes.get(current)?.frontierKeys ?? []);
      for (const next of outgoing.get(current) ?? []) {
        if (hidden.has(next)) {
          if (!parent.has(next)) {
            parent.set(next, current);
            queue.push(next);
          }
          continue;
        }
        if (next === start) {
          continue;
        }
        const via: string[] = [];
        for (let step: string | null = current; step !== null; step = parent.get(step) ?? null) {
          via.unshift(step);
        }
        mergeEdge(edges, start, next, { via, labels: [], weight: 1 });
      }
    }
  }
  return { nodes, edges };
}

/** Drops nodes with no bridging, and the objects that are left joined to no flow because of it. */
function remove(work: Work, removed: ReadonlySet<string>, pinned: ReadonlySet<string>): Work {
  if (removed.size === 0) {
    return work;
  }
  const before = adjacencyOf(work);
  const nodes = new Map<string, ViewNode>();
  for (const [id, node] of work.nodes) {
    if (!removed.has(id)) {
      nodes.set(id, node);
    }
  }

  // An object stays while it still reaches a drawn flow through objects (a table behind a view a drawn flow reads).
  const anchored = new Set<string>();
  const queue: string[] = [];
  for (const [id, node] of nodes) {
    if (node.memberType === "flow" || pinned.has(id)) {
      anchored.add(id);
      queue.push(id);
    }
  }
  for (let head = 0; head < queue.length; head++) {
    const current = queue[head];
    for (const next of [...(before.outgoing.get(current) ?? []), ...(before.incoming.get(current) ?? [])]) {
      if (nodes.has(next) && !anchored.has(next) && nodes.get(next)!.memberType === "object") {
        anchored.add(next);
        queue.push(next);
      }
    }
  }
  for (const [id, node] of [...nodes]) {
    const hadNeighbours = (before.outgoing.get(id)?.length ?? 0) + (before.incoming.get(id)?.length ?? 0) > 0;
    if (node.memberType === "object" && hadNeighbours && !anchored.has(id)) {
      nodes.delete(id);
    }
  }

  const edges = new Map<string, ViewEdge>();
  for (const edge of work.edges.values()) {
    if (nodes.has(edge.source) && nodes.has(edge.target)) {
      edges.set(edge.id, edge);
    }
  }
  return { nodes, edges };
}

/** Draws every node as its representative: members merge into one node, their edges into one edge per pair. */
function quotient(
  work: Work,
  representative: ReadonlyMap<string, string>,
  describe: (id: string, members: ViewNode[]) => Pick<ViewNode, "type" | "memberType" | "name" | "kind" | "place">,
): Work {
  if (representative.size === 0) {
    return work;
  }
  const grouped = new Map<string, ViewNode[]>();
  const nodes = new Map<string, ViewNode>();
  for (const [id, node] of work.nodes) {
    const target = representative.get(id);
    if (target === undefined) {
      nodes.set(id, node);
    } else {
      const list = grouped.get(target);
      if (list) {
        list.push(node);
      } else {
        grouped.set(target, [node]);
      }
    }
  }
  for (const [id, members] of grouped) {
    const repoNames = new Set(members.map((member) => member.repoName));
    const repoIds = new Set(members.map((member) => member.repoId));
    const merged: ViewNode = {
      id,
      ...describe(id, members),
      members: [],
      flowCount: 0,
      objectCount: 0,
      waves: [],
      foldedIn: 0,
      foldedOut: 0,
      hiddenUp: 0,
      hiddenDown: 0,
      frontierKeys: [],
      incompleteReasons: [],
      repoName: repoNames.size === 1 ? [...repoNames][0] : null,
      repoId: repoIds.size === 1 ? [...repoIds][0] : null,
      anchor: members.some((member) => member.anchor),
    };
    for (const member of members) {
      union(merged.members, member.members);
      merged.flowCount += member.flowCount;
      merged.objectCount += member.objectCount;
      union(merged.waves, member.waves);
      merged.foldedIn += member.foldedIn;
      merged.foldedOut += member.foldedOut;
      merged.hiddenUp = Math.max(merged.hiddenUp, member.hiddenUp);
      merged.hiddenDown = Math.max(merged.hiddenDown, member.hiddenDown);
      union(merged.frontierKeys, member.frontierKeys);
      union(merged.incompleteReasons, member.incompleteReasons);
    }
    merged.waves.sort((a, b) => a - b);
    nodes.set(id, merged);
  }

  const edges = new Map<string, ViewEdge>();
  for (const edge of work.edges.values()) {
    mergeEdge(edges, representative.get(edge.source) ?? edge.source, representative.get(edge.target) ?? edge.target, edge);
  }
  return { nodes, edges };
}

// ---- The reductions, in order ---------------------------------------------------------------------------------------

/** Step 1 and 2: the level's hidden objects and the hidden kinds, taken out with their chains bridged. */
function applyLevelAndKinds(base: BaseGraph, options: ViewOptions, pinned: ReadonlySet<string>): Work {
  const work = toWork(base);
  const hidden = new Set<string>();

  if (options.level === "flows" || options.level === "handovers") {
    for (const node of base.nodes.values()) {
      if (node.type !== "object" || pinned.has(node.id)) {
        continue;
      }
      const producers = base.incoming.get(node.id)?.size ?? 0;
      const consumers = base.outgoing.get(node.id)?.size ?? 0;
      // A handover is an object something drawn writes and something drawn reads; a frontier object has readers
      // the payload does not hold, so it is one too.
      const handover = producers > 0 && (consumers > 0 || node.frontier);
      if (options.level === "flows" || !handover) {
        hidden.add(node.id);
        for (const producer of base.incoming.get(node.id) ?? []) {
          const flow = work.nodes.get(producer);
          if (flow && flow.type === "flow") {
            flow.foldedOut += 1;
          }
        }
        for (const consumer of base.outgoing.get(node.id) ?? []) {
          const flow = work.nodes.get(consumer);
          if (flow && flow.type === "flow") {
            flow.foldedIn += 1;
          }
        }
      }
    }
  }

  if (options.hiddenKinds.size > 0) {
    for (const node of base.nodes.values()) {
      if (!pinned.has(node.id) && options.hiddenKinds.has(kindKey(node))) {
        hidden.add(node.id);
      }
    }
  }

  return projectOut(work, hidden);
}

/** Step 3: the flows outside the chosen waves. */
function applyWaves(work: Work, base: BaseGraph, options: ViewOptions, pinned: ReadonlySet<string>): Work {
  if (options.waves === null) {
    return work;
  }
  const removed = new Set<string>();
  for (const id of work.nodes.keys()) {
    const node = base.nodes.get(id);
    if (node && node.type === "flow" && node.wave !== null && !options.waves.has(node.wave) && !pinned.has(id)) {
      removed.add(id);
    }
  }
  return remove(work, removed, pinned);
}

/** The folders holding two or more of the given flows: the ones a collapse would shorten. */
function collapsibleFolders(base: BaseGraph, ids: Iterable<string>): Map<string, number> {
  const counts = new Map<string, number>();
  for (const id of ids) {
    const folder = base.nodes.get(id)?.folderId;
    if (folder) {
      counts.set(folder, (counts.get(folder) ?? 0) + 1);
    }
  }
  for (const [folder, count] of [...counts]) {
    if (count < 2) {
      counts.delete(folder);
    }
  }
  return counts;
}

/** Step 4: a folder's flows, and the objects only that folder touches, as one node. */
function applyGroups(
  work: Work,
  base: BaseGraph,
  options: ViewOptions,
  pinned: ReadonlySet<string>,
  forcedOpen: ReadonlySet<string>,
): Work {
  if (!options.grouped) {
    return work;
  }
  const folders = collapsibleFolders(base, work.nodes.keys());
  const representative = new Map<string, string>();
  for (const id of work.nodes.keys()) {
    const folder = base.nodes.get(id)?.folderId;
    if (folder && folders.has(folder) && !options.openGroups.has(folder) && !forcedOpen.has(folder)) {
      representative.set(id, folder);
    }
  }
  if (representative.size === 0) {
    return work;
  }

  // Data belongs to a folder when every flow it touches does. Objects joined to each other (a table and the view over
  // it) are taken together: the view touches only the table and a flow, the table only the view and a flow, and
  // neither is the folder's until both are looked at as one.
  const { outgoing, incoming } = adjacencyOf(work);
  const settled = new Set<string>();
  for (const [start, node] of work.nodes) {
    if (node.memberType !== "object" || settled.has(start)) {
      continue;
    }
    const cluster = [start];
    const owners = new Set<string>();
    let free = false;
    settled.add(start);
    for (let head = 0; head < cluster.length; head++) {
      const id = cluster[head];
      free = free || pinned.has(id);
      for (const neighbour of [...(outgoing.get(id) ?? []), ...(incoming.get(id) ?? [])]) {
        if (work.nodes.get(neighbour)?.memberType === "object") {
          if (!settled.has(neighbour)) {
            settled.add(neighbour);
            cluster.push(neighbour);
          }
        } else {
          owners.add(representative.get(neighbour) ?? neighbour);
        }
      }
    }
    const owner = owners.size === 1 ? [...owners][0] : undefined;
    if (!free && owner !== undefined && isGroupId(owner)) {
      for (const id of cluster) {
        representative.set(id, owner);
      }
    }
  }

  const folded = quotient(work, representative, (id, members) => {
    const flow = members.find((member) => member.memberType === "flow");
    return {
      type: "group",
      memberType: "flow",
      name: flow && flow.place !== "" ? flow.place : "(repository root)",
      kind: "folder",
      place: id,
    };
  });

  // A folder that reads back what it wrote itself (one of its flows fills a table, the next reads it, and a flow of
  // another folder reads it too, so the table stays drawn) would point at the table and be pointed at by it. Its own
  // reading is inside the folder, so that arrow is dropped: the table is drawn as what the folder produces.
  const after = adjacencyOf(folded);
  for (const node of folded.nodes.values()) {
    if (node.type !== "group") {
      continue;
    }
    const produced = new Set<string>();
    const queue = [node.id];
    for (let head = 0; head < queue.length; head++) {
      for (const next of after.outgoing.get(queue[head]) ?? []) {
        if (!produced.has(next) && folded.nodes.get(next)?.memberType === "object") {
          produced.add(next);
          queue.push(next);
        }
      }
    }
    for (const id of produced) {
      folded.edges.delete(id + EDGE_SEPARATOR + node.id);
    }
  }
  return folded;
}

function distances(start: string, adjacency: Map<string, string[]>, limit: number | null): Map<string, number> {
  const seen = new Map<string, number>([[start, 0]]);
  const queue = [start];
  for (let head = 0; head < queue.length; head++) {
    const current = queue[head];
    const distance = seen.get(current)!;
    if (limit !== null && distance >= limit) {
      continue;
    }
    for (const next of adjacency.get(current) ?? []) {
      if (!seen.has(next)) {
        seen.set(next, distance + 1);
        queue.push(next);
      }
    }
  }
  return seen;
}

/** Step 5: what is upstream and downstream of one node. Returns null when the node is not in the drawing. */
function applyTrace(work: Work, options: ViewOptions): Work | null {
  const trace = options.trace;
  if (trace === null) {
    return work;
  }
  if (!work.nodes.has(trace.id)) {
    return null;
  }
  const { outgoing, incoming } = adjacencyOf(work);
  const keep = new Set<string>([trace.id]);
  if (trace.direction !== "downstream") {
    for (const id of distances(trace.id, incoming, trace.hops).keys()) {
      keep.add(id);
    }
  }
  if (trace.direction !== "upstream") {
    for (const id of distances(trace.id, outgoing, trace.hops).keys()) {
      keep.add(id);
    }
  }
  // What the reader asked back in, one hop at a time; a node brought back may itself have been asked to show more.
  for (let changed = true; changed;) {
    changed = false;
    for (const id of [...keep]) {
      const more = [
        ...(options.revealUp.has(id) ? incoming.get(id) ?? [] : []),
        ...(options.revealDown.has(id) ? outgoing.get(id) ?? [] : []),
      ];
      for (const next of more) {
        if (!keep.has(next)) {
          keep.add(next);
          changed = true;
        }
      }
    }
  }

  const nodes = new Map<string, ViewNode>();
  for (const id of keep) {
    const node = work.nodes.get(id)!;
    nodes.set(id, {
      ...node,
      anchor: id === trace.id,
      hiddenUp: (incoming.get(id) ?? []).filter((neighbour) => !keep.has(neighbour)).length,
      hiddenDown: (outgoing.get(id) ?? []).filter((neighbour) => !keep.has(neighbour)).length,
    });
  }
  const edges = new Map<string, ViewEdge>();
  for (const edge of work.edges.values()) {
    if (keep.has(edge.source) && keep.has(edge.target)) {
      edges.set(edge.id, edge);
    }
  }
  return { nodes, edges };
}

/** The id a set of look-alikes is stacked under: stable while its first member (by id) is. */
export function stackIdOf(memberIds: readonly string[]): string {
  return STACK_PREFIX + [...memberIds].sort()[0];
}

/** Step 6: nodes of one kind with the same producers and the same consumers, as one node. */
function applyStacks(work: Work, options: ViewOptions, pinned: ReadonlySet<string>): Work {
  if (!options.stacked) {
    return work;
  }
  const { outgoing, incoming } = adjacencyOf(work);
  const bySignature = new Map<string, string[]>();
  for (const [id, node] of work.nodes) {
    // A node that needs a look of its own stays out: the traced one, the selected one, a folder, and anything with
    // something to say (lineage not derived, more to walk, producers or consumers a trace cut).
    if (node.type === "group" || node.anchor || pinned.has(id) || node.incompleteReasons.length > 0
      || node.frontierKeys.length > 0 || node.hiddenUp > 0 || node.hiddenDown > 0) {
      continue;
    }
    const signature = [
      node.type,
      node.kind,
      [...new Set(incoming.get(id) ?? [])].sort().join(EDGE_SEPARATOR),
      [...new Set(outgoing.get(id) ?? [])].sort().join(EDGE_SEPARATOR),
    ].join("\u0001");
    const list = bySignature.get(signature);
    if (list) {
      list.push(id);
    } else {
      bySignature.set(signature, [id]);
    }
  }

  const representative = new Map<string, string>();
  for (const ids of bySignature.values()) {
    if (ids.length < MIN_STACK_SIZE) {
      continue;
    }
    const stackId = stackIdOf(ids);
    if (options.openStacks.has(stackId)) {
      continue;
    }
    for (const id of ids) {
      representative.set(id, stackId);
    }
  }

  return quotient(work, representative, (_, members) => {
    const names = members.map((member) => member.name).sort((a, b) => a.localeCompare(b));
    const places = new Set(members.map((member) => member.place));
    return {
      type: "stack",
      memberType: members[0].memberType,
      name: names.join(", "),
      kind: members[0].kind,
      place: places.size === 1 ? [...places][0] : "",
    };
  });
}

/**
 * The flows nearest a node on each side: the ones it reads from and writes to, reached through any objects between.
 * The node itself is not among them.
 */
function nearestFlows(base: BaseGraph, id: string): Set<string> {
  const flows = new Set<string>();
  for (const adjacency of [base.incoming, base.outgoing]) {
    const seen = new Set<string>([id]);
    const queue = [id];
    for (let head = 0; head < queue.length; head++) {
      for (const next of adjacency.get(queue[head]) ?? []) {
        if (seen.has(next)) {
          continue;
        }
        seen.add(next);
        if (base.nodes.get(next)?.type === "flow") {
          flows.add(next);
        } else {
          queue.push(next);
        }
      }
    }
  }
  return flows;
}

/** The graph the level draws from: objects joined by flows for the data-only level, flows and objects otherwise. */
export function baseOf(source: GraphSource, level: GraphLevel): BaseGraph {
  return level === "objects" ? source.data : source.mixed;
}

/** Applies the reader's reductions to the scope, in the order the module comment gives. */
export function deriveView(source: GraphSource, options: ViewOptions): View {
  const base = baseOf(source, options.level);
  const pinned = new Set(options.pinned);
  const forcedOpen = new Set<string>();
  const trace = options.trace;
  if (trace !== null && base.nodes.has(trace.id)) {
    // The traced node is drawn whatever else is chosen, and it cannot hide inside its own folder. The folders of
    // the flows next to it are open too: a trace is exact where the reader is looking and coarse further away.
    pinned.add(trace.id);
    for (const id of [trace.id, ...nearestFlows(base, trace.id)]) {
      const folder = base.nodes.get(id)?.folderId;
      if (folder) {
        forcedOpen.add(folder);
      }
    }
  }

  let work = applyLevelAndKinds(base, options, pinned);
  work = applyWaves(work, base, options, pinned);
  const groupable = collapsibleFolders(base, work.nodes.keys()).size > 0;
  work = applyGroups(work, base, options, pinned, forcedOpen);
  const traced = applyTrace(work, options);
  work = applyStacks(traced ?? work, options, pinned);

  const nodes = [...work.nodes.values()];
  const edges = [...work.edges.values()];
  const { outgoing, incoming } = adjacencyOf(work);
  const locate = new Map<string, string>();
  let represented = 0;
  for (const node of nodes) {
    represented += node.members.length;
    for (const member of node.members) {
      locate.set(member, node.id);
    }
  }

  return {
    nodes,
    edges,
    outgoing,
    incoming,
    locate,
    total: base.nodes.size,
    represented,
    traceMissing: trace !== null && traced === null,
    groupable,
  };
}

/**
 * The options that draw a base node the reader picked from a list or a search: it is pinned, so no level, kind or
 * wave hides it and no stack swallows it; the folder it sits in is opened; and a trace that does not hold it is
 * dropped, since the reader asked for a node outside it.
 */
export function reveal(source: GraphSource, options: ViewOptions, id: string): ViewOptions {
  const base = baseOf(source, options.level);
  if (!base.nodes.has(id)) {
    return options;
  }
  let next: ViewOptions = { ...options, pinned: new Set([id]) };
  for (let attempt = 0; attempt < 4; attempt++) {
    const view = deriveView(source, next);
    const drawnAs = view.locate.get(id);
    if (drawnAs === id) {
      return next;
    }
    if (drawnAs !== undefined && isGroupId(drawnAs)) {
      next = { ...next, openGroups: new Set([...next.openGroups, drawnAs]) };
    } else if (drawnAs !== undefined && isStackId(drawnAs)) {
      next = { ...next, openStacks: new Set([...next.openStacks, drawnAs]) };
    } else if (next.trace !== null) {
      next = { ...next, trace: null, revealUp: new Set(), revealDown: new Set() };
    } else {
      return next;
    }
  }
  return next;
}

// ---- What the controls say about the scope --------------------------------------------------------------------------

/** How many nodes each level draws for the whole scope, before any other reduction: the number its control carries. */
export function levelSizes(source: GraphSource): Record<GraphLevel, number> {
  let flows = 0;
  let objects = 0;
  let handovers = 0;
  for (const node of source.mixed.nodes.values()) {
    if (node.type === "flow") {
      flows += 1;
      continue;
    }
    objects += 1;
    const producers = source.mixed.incoming.get(node.id)?.size ?? 0;
    const consumers = source.mixed.outgoing.get(node.id)?.size ?? 0;
    if (producers > 0 && (consumers > 0 || node.frontier)) {
      handovers += 1;
    }
  }
  return { flows, handovers: flows + handovers, all: flows + objects, objects: source.data.nodes.size };
}

/** The scope's waves in running order (unwaved, -1, last), each with how many flows it holds. */
export function waveCounts(source: GraphSource): { wave: number; count: number }[] {
  const counts = new Map<number, number>();
  for (const node of source.mixed.nodes.values()) {
    if (node.type === "flow" && node.wave !== null) {
      counts.set(node.wave, (counts.get(node.wave) ?? 0) + 1);
    }
  }
  const rank = (wave: number) => (wave < 0 ? Number.MAX_SAFE_INTEGER : wave);
  return [...counts.entries()]
    .map(([wave, count]) => ({ wave, count }))
    .sort((a, b) => rank(a.wave) - rank(b.wave));
}

/** The node kinds the level's drawing holds, flows first, each with its count: the legend, and what can be hidden. */
export function kindCounts(source: GraphSource, level: GraphLevel): KindCount[] {
  const base = baseOf(source, level);
  const counts = new Map<string, KindCount>();
  for (const node of base.nodes.values()) {
    if (level === "flows" && node.type === "object") {
      continue;
    }
    const key = kindKey(node);
    const existing = counts.get(key);
    if (existing) {
      existing.count += 1;
    } else {
      counts.set(key, { key, type: node.type, kind: node.kind, count: 1 });
    }
  }
  return [...counts.values()].sort((a, b) =>
    a.type === b.type ? b.count - a.count || a.kind.localeCompare(b.kind) : a.type === "flow" ? -1 : 1);
}

/** Everything upstream and downstream of a drawn node, for the selection's highlight. */
export function closure(view: View, id: string): { upstream: Set<string>; downstream: Set<string> } {
  const walk = (adjacency: Map<string, string[]>) => {
    const seen = new Set(distances(id, adjacency, null).keys());
    seen.delete(id);
    return seen;
  };
  return { upstream: walk(view.incoming), downstream: walk(view.outgoing) };
}
