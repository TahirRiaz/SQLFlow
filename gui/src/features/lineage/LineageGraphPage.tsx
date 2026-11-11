import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Panel, ReactFlowProvider } from "@xyflow/react";
import { CircleAlert, Download, Loader2, Rows3, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { downloadText } from "@/lib/download";
import { isApiError } from "../../api/client";
import { dataStreamApi, lineageApi } from "../../api/endpoints";
import type { DataStream, RunScope, StreamStatus } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { StreamDetailSheet } from "../datastreams/StreamDetailSheet";
import { statusIcons, statusInk } from "../datastreams/StreamStatusBadge";
import { statusLabels, statusRank } from "../datastreams/streamPresentation";
import { StreamVerdictCard } from "../datastreams/StreamVerdictCard";
import { useLocalStorageState } from "../../hooks/useLocalStorageState";
import { ExplainPageButton } from "../chat/ExplainPageButton";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { useThemeMode } from "../../theme/ThemeModeContext";
import { GraphCanvas, type CenterRequest } from "./GraphCanvas";
import { CompactControl, Field, KindFilter, LevelControl, OrientationControl, TraceControl, WaveRange } from "./GraphControls";
import { GraphDetails } from "./GraphDetails";
import { counted, kindLook } from "./graphKinds";
import { bandedLayout, type BandedLayout } from "./graphBands";
import { layeredLayout } from "./graphLayout";
import { rememberGraphSearch } from "./graphLocation";
import {
  baseOf,
  buildGraphSource,
  closure,
  deriveView,
  isGroupId,
  isStackId,
  kindCounts,
  levelSizes,
  reveal,
  waveCounts,
  type GraphLevel,
  type ViewOptions,
} from "./graphModel";
import { GraphSearch } from "./GraphSearch";
import { GraphStatusLine, type StatusPart } from "./GraphStatusLine";
import { buildLineageSvg } from "./graphSvg";
import {
  groupedBy,
  levelOf,
  readGraphParams,
  scopeOf,
  tracedNode,
  wavesInRange,
  writeGraphParams,
  type GraphParams,
} from "./graphUrl";
import { cardCaption, cardTitle, type CardActions } from "./LineageNodeCard";
import { ProjectSelect, type ScopeChoice } from "./ProjectSelect";
import "@xyflow/react/dist/style.css";

/** The node-key prefix every data subscriber carries (subscribers live on a synthetic server identity), so a
 * consuming leaf can be told from a database object without a second lookup. */
const SUBSCRIBER_KEY_PREFIX = "subscriber|";

/**
 * How many flows one graph may ask the data-stream board about at once. The flows are NAMED in the request
 * (a scope's closure crosses repos, so scoping the sweep by repo would cover some drawn nodes and silently
 * miss others), and a query string holding a hundred and twenty ids is already six kilobytes. Past this the
 * graph says so and falls back to checking one node at a time, which is never wrong, only manual.
 */
const MAX_STREAM_OVERLAY_FLOWS = 120;

const LEVEL_WORDS: Readonly<Record<GraphLevel, string>> = {
  flows: "flows only",
  handovers: "flows and handovers",
  all: "everything",
  objects: "data only",
};

/** The verdicts worth a mark on a node: a healthy stream, and one too new to judge, stay quiet. */
function needsAttention(status: StreamStatus): boolean {
  return status !== "healthy" && status !== "insufficient-history";
}

/** What the reader opened or asked back within one drawing. It belongs to that drawing and is dropped with it. */
interface Unfolded {
  key: string;
  openStacks: ReadonlySet<string>;
  revealUp: ReadonlySet<string>;
  revealDown: ReadonlySet<string>;
}

const NOTHING: ReadonlySet<string> = new Set();
const NO_LAYOUT: BandedLayout = { positions: new Map(), lanes: [], crossStart: 0, crossEnd: 0 };

/**
 * The lineage graph. A repository's lineage is hundreds of nodes, and a reader has one question at a time, so the
 * page never draws everything it knows: it opens on the smallest drawing that still tells the order of things (the
 * flows, wave by wave), and every control narrows or widens that drawing in a way the status line states in words.
 *
 *   - Scope: a whole repository, one of its projects, or the neighbourhood of a node a link landed on.
 *   - Show: flows alone, flows with the objects they hand over, everything, or the data alone.
 *   - Folders and stacks: a folder's flows, and nodes that look alike, draw as one node until opened.
 *   - Waves and kinds: trim the drawing to a range of waves, or hide kinds of node (their chains stay joined).
 *   - Trace: only what is upstream and downstream of one node, within a number of hops.
 *   - Layout: data runs top to bottom, or left to right when the reader prefers it.
 *
 * Whatever is drawn is laid in bands, one per execution wave, in running order: a flow in the band of its wave, what
 * it writes under it, what it reads first above it. The order is the point of the graph, so no control breaks it.
 *
 * A click selects a node (its producers and consumers light up and its details open); a double click opens what
 * it stands for. The whole drawing is in the query string, so it is a link.
 */
export default function LineageGraphPage() {
  const navigate = useNavigate();
  const { mode } = useThemeMode();
  const [searchParams, setSearchParams] = useSearchParams();
  // The address is a render behind anything asked of it, and a control drawn from it is a render behind too: a choice
  // clicked twice in quick succession would be read as "the one already chosen" and be dropped, and two controls
  // clicked one after the other would each start from the address as last drawn. So the page draws from the last
  // drawing ASKED for, kept here, and the address follows it. The address is taken as the truth again when it has
  // caught up, or when it names a drawing nobody asked for here (a link, the back button).
  const addressed = useMemo(() => readGraphParams(searchParams), [searchParams]);
  const [ahead, setAhead] = useState<GraphParams | null>(null);
  const drawingKeyOfAddress = writeGraphParams(ahead ?? addressed).toString();
  // One object per drawing, whichever of the two it came from, so nothing downstream is laid out twice.
  const params = useMemo(() => readGraphParams(new URLSearchParams(drawingKeyOfAddress)), [drawingKeyOfAddress]);
  const scope = scopeOf(params);
  const level = levelOf(params);

  const asked = useRef(addressed);
  const awaited = useRef<string[]>([]);
  useEffect(() => {
    const at = awaited.current.indexOf(writeGraphParams(addressed).toString());
    if (at >= 0) {
      awaited.current = awaited.current.slice(at + 1);
      if (awaited.current.length === 0) {
        setAhead(null);
      }
    } else {
      awaited.current = [];
      asked.current = addressed;
      setAhead(null);
    }
  }, [addressed]);

  const patch = useCallback((change: Partial<GraphParams> | ((current: GraphParams) => Partial<GraphParams>)) => {
    const current = asked.current;
    const next = { ...current, ...(typeof change === "function" ? change(current) : change) };
    const search = writeGraphParams(next);
    asked.current = next;
    awaited.current.push(search.toString());
    setAhead(next);
    setSearchParams(search, { replace: true });
  }, [setSearchParams]);

  // Right-click menu on a node, and the node whose script is open in the sheet. A flow node has two scripts: its
  // authored YAML (the default) and, for an sp flow, the SQL of the procedure it executes (view "object"); an
  // object node has only its SQL.
  const [nodeMenu, setNodeMenu] = useState<{ id: string; x: number; y: number } | null>(null);
  const [scriptTarget, setScriptTarget] = useState<{ key: string; view?: "object" } | null>(null);
  const [runDialog, setRunDialog] = useState<{ flowName: string; scope: RunScope } | null>(null);
  const [streamTarget, setStreamTarget] = useState<{ pipelineId: string; flowName: string } | null>(null);
  const [centerRequest, setCenterRequest] = useState<CenterRequest | null>(null);
  const [previewWaves, setPreviewWaves] = useState<ReadonlySet<number> | null>(null);
  // The data-streams board owns these two preferences; the graph reads them rather than keeping its own, so a
  // verdict shown beside a node is measured over exactly the span the board measured it over.
  const [streamWindowDays] = useLocalStorageState<number>("datastreams.windowDays", 60);
  const [streamIncludeBackfills] = useLocalStorageState<boolean>("datastreams.includeBackfills", false);

  const scriptQuery = useQuery({
    queryKey: ["lineage-node-script", scriptTarget?.key, scriptTarget?.view ?? ""],
    queryFn: () => {
      const target = scriptTarget as { key: string; view?: "object" };
      return lineageApi.script(target.key, target.view);
    },
    enabled: scriptTarget !== null,
  });

  // Every (repo, project) pair, for the scope picker.
  const projectsQuery = useQuery({
    queryKey: ["lineage-projects"],
    queryFn: () => lineageApi.projects(),
  });

  // The graph rewrites its query string in place (replace: true), so history holds nothing to go back to. Remember
  // the drawing's parameters while a scope is set, and forget them once it is cleared, so the object explorer's
  // "Graph view" button returns to this same drawing instead of the bare landing.
  const graphEnabled = scope !== "none";
  const graphSearch = searchParams.toString();
  useEffect(() => {
    rememberGraphSearch(graphEnabled ? graphSearch : "");
  }, [graphEnabled, graphSearch]);

  // The one graph query. A repository or a project is the seed, and frontier nodes the reader walked past ride
  // along; a link onto a node with no repository seeds on the node itself, whose neighbourhood the server walks
  // across every repository.
  const seedNode = scope === "node" ? params.focus : "";
  const expandTokens = useMemo(
    () => (seedNode === "" ? params.expand : [seedNode, ...params.expand]),
    [seedNode, params.expand],
  );
  const projectGraph = useQuery({
    queryKey: ["lineage-project-graph", params.repoId, params.project, seedNode, expandTokens.join("\u0000")],
    enabled: graphEnabled,
    queryFn: () => lineageApi.projectGraph({
      repoId: params.repoId || undefined,
      project: params.project || undefined,
      expand: expandTokens,
    }),
    // Walking past a frontier node grows the same drawing, so it stays up while the larger one loads; another
    // scope is another drawing, and starts from its skeleton.
    placeholderData: (previous, previousQuery) => {
      const key = previousQuery?.queryKey;
      return key !== undefined && key[1] === params.repoId && key[2] === params.project && key[3] === seedNode
        ? previous
        : undefined;
    },
  });

  const payload = projectGraph.data;
  const source = useMemo(() => (payload ? buildGraphSource(payload) : null), [payload]);
  const base = source === null ? null : baseOf(source, level);
  const pipelines = payload?.pipelines;

  const flowById = useMemo(() => new Map((pipelines ?? []).map((pipeline) => [pipeline.id, pipeline])), [pipelines]);
  const sizes = useMemo(() => (source === null ? null : levelSizes(source)), [source]);
  const waveStops = useMemo(() => (source === null ? [] : waveCounts(source)), [source]);
  const kinds = useMemo(() => (source === null ? [] : kindCounts(source, level)), [source, level]);
  const searchOptions = useMemo(
    () => (base === null ? [] : [...base.nodes.values()].map((node) => ({ id: node.id, name: node.name }))
      .sort((a, b) => a.name.localeCompare(b.name))),
    [base],
  );

  // ---- What the reader chose, as the model's options -----------------------------------------------------------
  // What was opened belongs to one level of one scope (a stack at one level is not a stack at another); what is
  // selected belongs to the scope, so a node stays selected while the reader changes how much is drawn around it.
  const scopeKey = `${params.repoId}|${params.project}|${seedNode}`;
  const drawingKey = `${scopeKey}|${level}`;
  const [unfoldedState, setUnfoldedState] = useState<Unfolded>({ key: "", openStacks: NOTHING, revealUp: NOTHING, revealDown: NOTHING });
  const unfolded = useMemo<Unfolded>(
    () => (unfoldedState.key === drawingKey
      ? unfoldedState
      : { key: drawingKey, openStacks: NOTHING, revealUp: NOTHING, revealDown: NOTHING }),
    [unfoldedState, drawingKey],
  );
  // The selection defaults to the node a link landed on, until the reader selects something else in this drawing.
  const [selectionState, setSelectionState] = useState<{ key: string; id: string | null }>({ key: "", id: null });
  const selectedId = selectionState.key === scopeKey ? selectionState.id : params.focus || null;

  const grouped = level !== "objects" && groupedBy(params);
  const traceId = tracedNode(params);
  const waveSet = useMemo(
    () => (level === "objects" ? null : wavesInRange(params.waves, waveStops.map((stop) => stop.wave))),
    [level, params.waves, waveStops],
  );

  const options = useMemo<ViewOptions>(() => ({
    level,
    hiddenKinds: new Set(params.hide),
    waves: waveSet,
    grouped,
    openGroups: new Set(params.open),
    trace: traceId === "" ? null : { id: traceId, hops: params.hops, direction: params.direction },
    revealUp: unfolded.revealUp,
    revealDown: unfolded.revealDown,
    stacked: params.stacked,
    openStacks: unfolded.openStacks,
    pinned: selectedId !== null && base?.nodes.has(selectedId) ? new Set([selectedId]) : NOTHING,
  }), [level, params.hide, params.open, params.hops, params.direction, params.stacked, waveSet, grouped, traceId,
    unfolded, selectedId, base]);

  const view = useMemo(() => (source === null ? null : deriveView(source, options)), [source, options]);
  // A drawing that holds flows is laid in bands, one per wave, so it is followed the way the data moves. The data
  // alone has no waves to go by and is laid by how far each object is from its sources.
  const layout = useMemo<BandedLayout>(() => {
    if (view === null) {
      return NO_LAYOUT;
    }
    if (level === "objects") {
      return { ...NO_LAYOUT, positions: layeredLayout(view.nodes.map((node) => node.id), view.edges, params.orientation) };
    }
    return bandedLayout(
      view.nodes.map((node) => ({ id: node.id, flow: node.memberType === "flow", waves: node.waves })),
      view.edges,
      params.orientation,
    );
  }, [view, level, params.orientation]);
  const positions = layout.positions;
  const laneReach = useMemo(() => ({ start: layout.crossStart, end: layout.crossEnd }), [layout]);
  const selectedNode = view === null || selectedId === null
    ? null
    : view.nodes.find((node) => node.id === selectedId) ?? null;
  const selection = useMemo(() => {
    if (view === null || selectedNode === null) {
      return null;
    }
    return { id: selectedNode.id, ...closure(view, selectedNode.id) };
  }, [view, selectedNode]);

  const nameOf = useCallback(
    (id: string) => source?.mixed.nodes.get(id)?.name ?? source?.data.nodes.get(id)?.name ?? id,
    [source],
  );
  const traceName = traceId === ""
    ? ""
    : view?.nodes.find((node) => node.id === traceId)?.name ?? nameOf(traceId);

  // ---- Actions -------------------------------------------------------------------------------------------------
  const select = useCallback((id: string | null) => {
    setSelectionState({ key: scopeKey, id });
  }, [scopeKey]);

  const center = useCallback((id: string) => {
    setCenterRequest((previous) => ({ id, nonce: (previous?.nonce ?? 0) + 1 }));
  }, []);

  /** Draws and selects a base node picked from a list or the search, opening whatever hides it. */
  const pick = useCallback((id: string) => {
    if (source === null) {
      return;
    }
    const next = reveal(source, options, id);
    const traceDropped = next.trace === null && options.trace !== null;
    if (next.openGroups !== options.openGroups || traceDropped) {
      patch((current) => ({
        open: [...next.openGroups],
        ...(traceDropped
          ? { trace: "", focus: scopeOf(current) === "node" ? current.focus : "", hops: null, direction: "both" as const }
          : {}),
      }));
    }
    setUnfoldedState({
      key: drawingKey,
      openStacks: next.openStacks,
      revealUp: next.revealUp,
      revealDown: next.revealDown,
    });
    select(id);
    center(id);
  }, [source, options, patch, drawingKey, select, center]);

  const stopTracing = useCallback(() => {
    patch((current) => ({
      trace: "",
      focus: scopeOf(current) === "node" ? current.focus : "",
      hops: null,
      direction: "both",
    }));
    setUnfoldedState({ ...unfolded, revealUp: NOTHING, revealDown: NOTHING });
  }, [patch, unfolded]);

  const trace = useCallback((id: string) => {
    patch({ trace: id });
    setUnfoldedState({ ...unfolded, revealUp: NOTHING, revealDown: NOTHING });
    select(id);
  }, [patch, unfolded, select]);

  const openFolder = useCallback((id: string) => {
    patch((current) => ({
      open: current.open.includes(id) ? current.open : [...current.open, id],
      // A folder that was traced as one node no longer exists once it is opened.
      ...(tracedNode(current) === id ? { trace: "", hops: null, direction: "both" as const } : {}),
    }));
  }, [patch]);

  const closeFolder = useCallback((id: string) => {
    patch((current) => ({ open: current.open.filter((folder) => folder !== id) }));
  }, [patch]);

  const openStack = useCallback((id: string) => {
    setUnfoldedState({ ...unfolded, openStacks: new Set([...unfolded.openStacks, id]) });
  }, [unfolded]);

  const openTarget = useCallback((id: string): { label: string; to: string } | null => {
    const node = base?.nodes.get(id);
    if (node === undefined) {
      return null;
    }
    // A subscriber is a consumer, not a catalog object: it opens where its queries and reads live.
    if (id.startsWith(SUBSCRIBER_KEY_PREFIX)) {
      return { label: "Open subscriber", to: `/subscribers?key=${encodeURIComponent(id)}` };
    }
    return node.type === "flow"
      ? { label: "Open pipeline", to: `/pipelines/${id}` }
      : { label: "Open in explorer", to: `/lineage/objects?name=${encodeURIComponent(node.name)}` };
  }, [base]);

  /** A double click opens what the node stands for: a folder's flows, a stack's nodes, or the node's own page. */
  const activate = useCallback((id: string) => {
    if (isGroupId(id)) {
      openFolder(id);
    } else if (isStackId(id)) {
      openStack(id);
    } else {
      const target = openTarget(id);
      if (target !== null) {
        navigate(target.to);
      }
    }
  }, [openFolder, openStack, openTarget, navigate]);

  const cardActions = useMemo<CardActions>(() => ({
    onReveal: (id, side) => {
      setUnfoldedState({
        ...unfolded,
        revealUp: side === "up" ? new Set([...unfolded.revealUp, id]) : unfolded.revealUp,
        revealDown: side === "down" ? new Set([...unfolded.revealDown, id]) : unfolded.revealDown,
      });
    },
    onExpandFrontier: (keys) => {
      patch((current) => ({ expand: [...current.expand, ...keys.filter((key) => !current.expand.includes(key))] }));
    },
  }), [unfolded, patch]);

  const selectScope = useCallback((choice: ScopeChoice | null) => {
    // Another scope is another drawing: nothing of the one being left carries over but how much the reader likes drawn.
    patch({
      repoId: choice?.repoId ?? "",
      project: choice?.project ?? "",
      focus: "",
      expand: [],
      waves: null,
      hide: [],
      grouped: null,
      open: [],
      trace: "",
      hops: null,
      direction: "both",
    });
  }, [patch]);

  // A node found in the catalog rather than on the canvas. A flow is drawn in its own repository, traced, so its
  // whole chain is there on both sides; anything else seeds on itself, since data belongs to no one repository.
  const seedFocus = useCallback((id: string, hitRepoId?: string) => {
    patch({
      repoId: hitRepoId ?? "",
      project: "",
      focus: id,
      expand: [],
      waves: null,
      hide: [],
      open: [],
      trace: "",
      hops: null,
      direction: "both",
      level: null,
    });
  }, [patch]);

  const reset = useCallback(() => {
    patch((current) => ({
      level: null,
      waves: null,
      hide: [],
      grouped: null,
      open: [],
      trace: "",
      focus: scopeOf(current) === "node" ? current.focus : "",
      hops: null,
      direction: "both",
      stacked: true,
      expand: [],
    }));
    setUnfoldedState({ key: drawingKey, openStacks: NOTHING, revealUp: NOTHING, revealDown: NOTHING });
  }, [patch, drawingKey]);

  // ---- Delivery verdicts ------------------------------------------------------------------------------------------
  // Every flow in scope, asked of the data-stream board in one call so the graph can say WHERE data has stopped
  // arriving before the reader clicks a single node. The flows are named explicitly rather than the sweep being
  // scoped by repo, because a scope's closure crosses repos and an overlay that covered some drawn nodes and
  // quietly skipped others would be worse than no overlay at all.
  const scopeFlowIds = useMemo(() => (pipelines ?? []).map((pipeline) => pipeline.id), [pipelines]);
  const streamOverlayTooBig = scopeFlowIds.length > MAX_STREAM_OVERLAY_FLOWS;
  const streamsQuery = useQuery({
    queryKey: ["lineage-graph-streams", scopeFlowIds, streamWindowDays, streamIncludeBackfills],
    enabled: scopeFlowIds.length > 0 && !streamOverlayTooBig,
    queryFn: () => dataStreamApi.list({
      pipelineIds: scopeFlowIds,
      // The caller has already chosen its population, so neither the source/internal split nor schedule
      // membership may narrow it further: a drawn node the board would have filtered out still has to be able
      // to say what it is doing.
      scope: "all",
      includeUnscheduled: true,
      days: streamWindowDays,
      includeBackfills: streamIncludeBackfills,
      limit: MAX_STREAM_OVERLAY_FLOWS,
    }),
  });

  const verdictByFlow = useMemo(() => {
    const map = new Map<string, DataStream>();
    for (const stream of streamsQuery.data?.streams ?? []) {
      map.set(stream.pipelineId, stream);
    }
    return map;
  }, [streamsQuery.data]);

  // A node wears the worst verdict among the flows it stands for, and only one that needs a look: a healthy graph
  // is a quiet one.
  const statuses = useMemo(() => {
    const map = new Map<string, StreamStatus>();
    for (const node of view?.nodes ?? []) {
      let worst: StreamStatus | null = null;
      for (const member of node.members) {
        const status = verdictByFlow.get(member)?.status;
        if (status !== undefined && needsAttention(status) && (worst === null || statusRank(status) < statusRank(worst))) {
          worst = status;
        }
      }
      if (worst !== null) {
        map.set(node.id, worst);
      }
    }
    return map;
  }, [view, verdictByFlow]);

  const troubled = useMemo(
    () => [...verdictByFlow.values()]
      .filter((stream) => needsAttention(stream.status) && flowById.has(stream.pipelineId))
      .sort((a, b) => statusRank(a.status) - statusRank(b.status)),
    [verdictByFlow, flowById],
  );

  // Whose delivery verdict describes the selected node. A flow speaks for itself; a table, view or landed file is
  // spoken for by whatever flow WRITES it, read from the scope's graph rather than from the drawing, so a table
  // selected where no flow is drawn at all still finds its producer.
  const selectedStreams = useMemo(() => {
    if (selectedNode === null || source === null || (selectedNode.type !== "flow" && selectedNode.type !== "object")) {
      return null;
    }
    if (selectedNode.type === "flow") {
      return [{ pipelineId: selectedNode.id, caption: undefined as string | undefined }];
    }
    return [...(source.mixed.incoming.get(selectedNode.id) ?? [])]
      .filter((producer) => flowById.has(producer))
      .map((producer) => ({ pipelineId: producer, caption: flowById.get(producer)!.name as string | undefined }));
  }, [selectedNode, source, flowById]);

  // ---- What the page says about itself ----------------------------------------------------------------------------
  const queryError = [projectsQuery, projectGraph].find((query) => query.isError)?.error;
  const loadingGraph = graphEnabled && projectGraph.isLoading;
  const scopeHasNodes = base !== null && base.nodes.size > 0;
  const hasContent = view !== null && view.nodes.length > 0;
  const incompletePipelines = useMemo(
    () => (pipelines ?? []).filter((pipeline) => pipeline.lineageComplete === false),
    [pipelines]);
  const repoName = pipelines?.find((pipeline) => pipeline.repoId === params.repoId)?.repoName
    ?? projectsQuery.data?.find((item) => item.repoId === params.repoId)?.repoName
    ?? params.repoId;

  const waveIndexes = useMemo<readonly [number, number]>(() => {
    if (waveSet === null || waveStops.length === 0) {
      return [0, Math.max(0, waveStops.length - 1)];
    }
    const drawn = waveStops.map((stop, index) => (waveSet.has(stop.wave) ? index : -1)).filter((index) => index >= 0);
    return [drawn[0], drawn[drawn.length - 1]];
  }, [waveSet, waveStops]);

  const statusParts = useMemo<StatusPart[]>(() => {
    if (view === null) {
      return [];
    }
    const parts: StatusPart[] = [];
    parts.push({
      key: "scope",
      label: scope === "node"
        ? `around ${nameOf(params.focus)}`
        : scope === "project" ? `${repoName} / ${params.project}` : repoName,
    });
    parts.push({ key: "level", label: LEVEL_WORDS[level] });
    const folders = view.nodes.filter((node) => node.type === "group").length;
    if (folders > 0) {
      parts.push({
        key: "folders",
        label: `${counted(folders, "folder")} collapsed`,
        removeHint: "Draw every folder's flows one by one",
        onRemove: () => patch({ grouped: false, open: [] }),
      });
    }
    if (grouped && params.open.length > 0) {
      parts.push({
        key: "open-folders",
        label: `${counted(params.open.length, "folder")} open`,
        removeHint: "Collapse the opened folders again",
        onRemove: () => patch({ open: [] }),
      });
    }
    const stacks = view.nodes.filter((node) => node.type === "stack");
    if (stacks.length > 0) {
      const inStacks = stacks.reduce((sum, node) => sum + node.members.length, 0);
      parts.push({
        key: "stacks",
        label: `${inStacks} look-alikes in ${counted(stacks.length, "stack")}`,
        removeHint: "Draw stacked nodes one by one",
        onRemove: () => patch({ stacked: false }),
      });
    }
    if (waveSet !== null) {
      const first = waveStops[waveIndexes[0]].wave;
      const last = waveStops[waveIndexes[1]].wave;
      const name = (wave: number) => (wave < 0 ? "unwaved" : String(wave));
      parts.push({
        key: "waves",
        label: first === last ? (first < 0 ? "unwaved flows" : `wave ${first}`) : `waves ${name(first)} to ${name(last)}`,
        removeHint: "Draw every wave",
        onRemove: () => patch({ waves: null }),
      });
    }
    const hiddenHere = kinds.filter((kind) => params.hide.includes(kind.key));
    if (hiddenHere.length > 0) {
      parts.push({
        key: "kinds",
        label: `hidden: ${hiddenHere.map((kind) => kindLook(kind.type, kind.kind).label).join(", ")}`,
        removeHint: "Show every kind",
        onRemove: () => patch({ hide: [] }),
      });
    }
    if (traceId !== "" && !view.traceMissing) {
      const reach = params.hops === null ? "whole trace" : counted(params.hops, "hop");
      const side = params.direction === "both" ? "" : `, ${params.direction} only`;
      parts.push({
        key: "trace",
        label: `trace of ${traceName}: ${reach}${side}`,
        removeHint: "Stop tracing and draw the whole scope",
        onRemove: stopTracing,
      });
    }
    if (params.expand.length > 0) {
      parts.push({
        key: "expand",
        label: `walked past ${counted(params.expand.length, "node")}`,
        removeHint: "Drop what was loaded past the first walk",
        onRemove: () => patch({ expand: [] }),
      });
    }
    return parts;
  }, [view, scope, nameOf, params, repoName, level, grouped, waveSet, waveStops, waveIndexes, kinds, traceId, traceName, patch, stopTracing]);

  // Export the drawing as an SVG of the current layout in the active theme, built from the same nodes and
  // positions the canvas shows.
  const downloadLineage = useCallback(() => {
    if (view === null) {
      return;
    }
    const color = (name: string, fallback: string) => {
      const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
      return value !== "" ? value : fallback;
    };
    const svg = buildLineageSvg(view, positions, params.orientation, (node) => ({ title: cardTitle(node), caption: cardCaption(node) }), {
      text: color("--foreground", "#1d2733"),
      textMuted: color("--muted-foreground", "#5b6b7f"),
      paper: color("--card", "#ffffff"),
      border: color("--border", "#d7dee8"),
      edge: color("--muted-foreground", "#5b6b7f"),
    });
    if (svg === "") {
      return;
    }
    const safeName = (repoName || "graph").replace(/[^\w.-]+/g, "_");
    downloadText(`lineage-${safeName}-${level}.svg`, svg, "image/svg+xml");
  }, [view, positions, params.orientation, repoName, level]);

  // The node the right-click menu is open on, and what it may do. Only a flow of the scope's own repository can be
  // run from here; a flow reached in another repository, and data, get the navigation actions only.
  const menuNode = nodeMenu === null ? null : view?.nodes.find((node) => node.id === nodeMenu.id) ?? null;
  const menuFlow = menuNode !== null && menuNode.type === "flow" ? flowById.get(menuNode.id) ?? null : null;
  const menuCanRun = menuFlow !== null && params.repoId !== "" && menuFlow.repoId === params.repoId;
  const menuFolder = menuNode !== null && menuNode.type === "flow" ? base?.nodes.get(menuNode.id)?.folderId ?? null : null;
  const kindOf = (id: string) => flowById.get(id)?.kind;

  const selectedTarget = selectedNode !== null ? openTarget(selectedNode.id) : null;
  const selectedIsFlow = selectedNode?.type === "flow";

  return (
    <div
      data-testid="page-lineage-graph"
      // Full-bleed: negative margins cancel the editor's content padding so the page runs edge to edge, and the
      // height claims the viewport below the fixed chrome (title bar 36px + tab strip 35px + status bar 22px).
      className="relative -m-4 flex h-[calc(100vh-93px)] min-w-0 flex-col overflow-hidden bg-background md:-m-6"
    >
      {/* A new drawing's nodes slide to their places (the canvas sets the class for the length of the move), so a
          change of scope is followed, not re-read. The rule outranks the library's own node rule by naming both. */}
      <style>{`
        .react-flow.sf-lineage-moving .react-flow__node { transition: transform 220ms ease-out; }
        @media (prefers-reduced-motion: reduce) {
          .react-flow.sf-lineage-moving .react-flow__node { transition: none; }
        }
      `}</style>

      {/* The bar reads left to right in the order a drawing is made: what to look at, how much of it, which part. */}
      <div
        data-testid="graph-toolbar"
        className="flex shrink-0 flex-wrap items-end gap-x-4 gap-y-2 border-b border-border bg-card px-3 py-2"
      >
        <Field label="Find">
          <GraphSearch options={searchOptions} onPickNode={pick} onPickCatalog={seedFocus} />
        </Field>
        <Field label="Scope">
          <ProjectSelect
            items={projectsQuery.data ?? []}
            repoId={params.repoId}
            project={params.project}
            onSelect={selectScope}
          />
        </Field>
        {scopeHasNodes && sizes !== null && view !== null && (
          <>
            <Field label="Show">
              <LevelControl value={level} sizes={sizes} onChange={(next) => patch({ level: next })} />
            </Field>
            <Field label="Draw as one">
              <CompactControl
                grouped={grouped}
                groupable={level !== "objects" && view.groupable}
                stacked={params.stacked}
                onGrouped={(value) => patch({ grouped: value })}
                onStacked={(value) => patch({ stacked: value })}
              />
            </Field>
            {level !== "objects" && waveStops.length > 1 && (
              <Field label="Waves">
                <WaveRange
                  stops={waveStops}
                  range={waveIndexes}
                  onPreview={(range) => setPreviewWaves(range === null
                    ? null
                    : new Set(waveStops.slice(range[0], range[1] + 1).map((stop) => stop.wave)))}
                  onCommit={(range) => patch({
                    waves: range[0] === 0 && range[1] === waveStops.length - 1
                      ? null
                      : { from: waveStops[range[0]].wave, to: waveStops[range[1]].wave },
                  })}
                />
              </Field>
            )}
            <Field label="Kinds">
              <KindFilter
                kinds={kinds}
                hidden={options.hiddenKinds}
                onToggle={(key) => patch((current) => ({
                  hide: current.hide.includes(key) ? current.hide.filter((item) => item !== key) : [...current.hide, key],
                }))}
                onShowAll={() => patch({ hide: [] })}
              />
            </Field>
            <Field label="Layout">
              <OrientationControl value={params.orientation} onChange={(orientation) => patch({ orientation })} />
            </Field>
            {traceId !== "" && !view.traceMissing && (
              <Field label="Trace" testId="graph-trace-control">
                <TraceControl
                  name={traceName}
                  hops={params.hops}
                  direction={params.direction}
                  orientation={params.orientation}
                  onHops={(hops) => patch({ trace: traceId, hops })}
                  onDirection={(direction) => patch({ trace: traceId, direction })}
                  onClear={stopTracing}
                />
              </Field>
            )}
          </>
        )}
        <div className="ml-auto flex h-8 items-center gap-1">
          {hasContent && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button variant="ghost" size="icon-sm" onClick={downloadLineage} aria-label="Download SVG" data-testid="download-lineage">
                  <Download />
                </Button>
              </TooltipTrigger>
              <TooltipContent>Download this drawing as an SVG</TooltipContent>
            </Tooltip>
          )}
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-sm"
                onClick={() => navigate("/lineage/objects")}
                aria-label="Open explorer"
                data-testid="open-lineage-objects"
              >
                <Rows3 />
              </Button>
            </TooltipTrigger>
            <TooltipContent>Open the object explorer</TooltipContent>
          </Tooltip>
          {/* The graph's bar is its header: Explain sits at its end, as it sits beside a title elsewhere. */}
          <ExplainPageButton size="icon-sm" />
        </div>
      </div>

      {scopeHasNodes && view !== null && (
        <GraphStatusLine
          drawn={view.nodes.length}
          total={view.total}
          parts={statusParts}
          canReset={params.level !== null || params.grouped !== null || !params.stacked || params.waves !== null
            || params.hide.length > 0 || params.open.length > 0 || traceId !== "" || params.expand.length > 0
            || unfolded.openStacks.size > 0}
          onReset={reset}
          attention={(
            <>
              {view.traceMissing && (
                <span className="inline-flex items-center gap-1.5 text-warning" data-testid="graph-trace-missing">
                  <TriangleAlert aria-hidden className="size-3.5" />
                  The traced node is not in this scope.
                  <Button variant="outline" size="xs" className="h-5" onClick={stopTracing}>Stop tracing</Button>
                </span>
              )}
              {payload?.truncated && (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span className="inline-flex items-center gap-1 text-warning" data-testid="graph-truncated">
                      <TriangleAlert aria-hidden className="size-3.5" />
                      Scope cut short
                    </span>
                  </TooltipTrigger>
                  <TooltipContent>
                    The scope holds more flows than one graph loads, so some are missing. Pick a project, or search
                    for the node you are after.
                  </TooltipContent>
                </Tooltip>
              )}
              {incompletePipelines.length > 0 && (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span className="inline-flex items-center gap-1 text-warning" data-testid="graph-incomplete-lineage">
                      <TriangleAlert aria-hidden className="size-3.5" />
                      Lineage not derived for {counted(incompletePipelines.length, "flow")}
                    </span>
                  </TooltipTrigger>
                  <TooltipContent className="max-w-sm">
                    {incompletePipelines[0].incompleteReason}
                    {incompletePipelines.length > 1 && " The others say why on their own nodes."}
                    {" "}Run a connected sync once the control plane can reach the referenced servers.
                  </TooltipContent>
                </Tooltip>
              )}
              {streamOverlayTooBig && (
                <Tooltip>
                  <TooltipTrigger asChild>
                    <span className="text-muted-foreground" data-testid="graph-stream-unchecked">Streams not checked</span>
                  </TooltipTrigger>
                  <TooltipContent>
                    {scopeFlowIds.length} flows are in scope, too many to check in one sweep. Select a node to check
                    its stream, or narrow the scope to a project.
                  </TooltipContent>
                </Tooltip>
              )}
              {troubled.length > 0 && (
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button variant="ghost" size="xs" className="h-5 gap-1 px-1.5" data-testid="graph-stream-summary">
                      <CircleAlert aria-hidden className={`size-3.5 ${statusInk[troubled[0].status]}`} />
                      {counted(troubled.length, "stream")} need{troubled.length === 1 ? "s" : ""} a look
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent align="end" className="max-h-80 w-80 overflow-y-auto">
                    {troubled.map((stream) => {
                      const Icon = statusIcons[stream.status];
                      return (
                        <DropdownMenuItem key={stream.pipelineId} onSelect={() => pick(stream.pipelineId)} data-testid="graph-stream-troubled">
                          <Icon aria-hidden className={statusInk[stream.status]} />
                          <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{flowById.get(stream.pipelineId)?.name}</span>
                          <span className="text-xs text-muted-foreground">{statusLabels[stream.status]}</span>
                        </DropdownMenuItem>
                      );
                    })}
                  </DropdownMenuContent>
                </DropdownMenu>
              )}
            </>
          )}
        />
      )}

      <div className="relative min-h-0 flex-1">
        {queryError !== undefined && (
          <div className="absolute top-3 right-3 left-3 z-10 max-w-2xl">
            {isApiError(queryError)
              ? <CorrelationError error={queryError} />
              : <p className="text-[13px] text-destructive">{String(queryError)}</p>}
          </div>
        )}

        {!graphEnabled && !projectsQuery.isError && (
          <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-empty">
            <EmptyState
              title="Search for a node, or pick a repository"
              description="Search for any table, view, file or flow to land on it and see what feeds it and what depends on it. Or pick a repository to see how its folders of flows depend on each other, then open the one you are after."
            />
          </div>
        )}

        {loadingGraph && seedNode !== "" && !queryError && (
          <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-resolving-focus-box">
            <div className="flex flex-col items-center gap-3" data-testid="graph-resolving-focus">
              <Loader2 className="size-7 animate-spin text-muted-foreground" />
              <p className="text-[13px] text-muted-foreground">Locating the node in the lineage graph...</p>
            </div>
          </div>
        )}

        {loadingGraph && seedNode === "" && !queryError && (
          <Skeleton className="absolute inset-0 h-full rounded-none" data-testid="graph-loading" />
        )}

        {graphEnabled && !loadingGraph && !queryError && base !== null && !scopeHasNodes && (
          <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-no-lineage">
            {scope === "node" ? (
              <EmptyState
                data-testid="graph-focus-no-repo"
                title="No lineage references this node yet"
                description="This node has no recorded lineage edges, so nothing feeds it and nothing depends on it. Open it in the object explorer to see its definition and columns."
                action={(
                  <Button variant="outline" size="sm" onClick={() => navigate("/lineage/objects")} data-testid="graph-focus-open-explorer">
                    <Rows3 />
                    Open explorer
                  </Button>
                )}
              />
            ) : (
              <EmptyState
                title={level === "objects" ? "No data movement recorded here yet" : "No lineage here yet"}
                description={level === "objects"
                  ? "No flow of this scope is known to move data from one object to another. Sync the repository (a connected sync adds what the database itself tells), then come back."
                  : "This scope has no active flows, or its repository has not been synced. Sync the repository, then come back."}
              />
            )}
          </div>
        )}

        {scopeHasNodes && !hasContent && !loadingGraph && !queryError && (
          <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-nothing-drawn">
            <EmptyState
              title="Nothing is left to draw"
              description="The waves and kinds chosen above leave no node of this scope. Remove one of them from the line above, or reset the drawing."
              action={<Button variant="outline" size="sm" onClick={reset}>Reset the drawing</Button>}
            />
          </div>
        )}

        {view !== null && hasContent && base !== null && !queryError && (
          <ReactFlowProvider>
            <GraphCanvas
              view={view}
              positions={positions}
              orientation={params.orientation}
              lanes={layout.lanes}
              laneReach={laneReach}
              selection={selection}
              statuses={statuses}
              previewWaves={previewWaves}
              nameOf={nameOf}
              colorMode={mode}
              centerRequest={centerRequest}
              actions={cardActions}
              onSelect={select}
              onActivate={activate}
              onNodeContextMenu={(id, position) => setNodeMenu({ id, x: position.x, y: position.y })}
            >
              {selectedNode !== null && selection !== null && (
                <Panel position="top-right" className="!m-2 flex h-[calc(100%-16px)] items-start">
                  <GraphDetails
                    // A fresh panel per node, so a list opened for one node is not left open for the next.
                    key={selectedNode.id}
                    node={selectedNode}
                    base={base}
                    upstream={selection.upstream.size}
                    downstream={selection.downstream.size}
                    traced={traceId === selectedNode.id && !view.traceMissing}
                    openLabel={selectedTarget?.label ?? null}
                    canViewScript={!selectedIsFlow || kindOf(selectedNode.id) === "sp"}
                    canViewYaml={selectedIsFlow}
                    onClose={() => select(null)}
                    onTrace={() => trace(selectedNode.id)}
                    onOpen={() => activate(selectedNode.id)}
                    onUnfold={() => activate(selectedNode.id)}
                    onPick={pick}
                    onViewScript={() => setScriptTarget(selectedIsFlow ? { key: selectedNode.id, view: "object" } : { key: selectedNode.id })}
                    onViewYaml={() => setScriptTarget({ key: selectedNode.id })}
                    stream={selectedStreams === null ? null : (
                      // Is data still arriving here? The question an operator tracing a chain asks at every hop, and
                      // the one the graph alone cannot answer: an arrow says a flow SHOULD fill this table, not that
                      // it still does. The verdict is the board's, unchanged, so the two surfaces cannot disagree.
                      <section className="mt-3" data-testid="graph-focus-stream">
                        <h4 className="mb-1 text-[11px] font-medium tracking-wider text-muted-foreground uppercase">Data stream</h4>
                        {selectedStreams.length === 0 ? (
                          <p className="text-[11px] leading-4 text-muted-foreground">
                            No flow in this scope writes this node, so there is no delivery to judge here.
                          </p>
                        ) : (
                          <div className="flex flex-col gap-2">
                            {selectedStreams.map((producer) => (
                              <StreamVerdictCard
                                key={producer.pipelineId}
                                pipelineId={producer.pipelineId}
                                caption={producer.caption}
                                known={verdictByFlow.get(producer.pipelineId)}
                                windowDays={streamWindowDays}
                                includeBackfills={streamIncludeBackfills}
                                onOpen={(pipelineId, flowName) => setStreamTarget({ pipelineId, flowName })}
                              />
                            ))}
                          </div>
                        )}
                      </section>
                    )}
                  />
                </Panel>
              )}
            </GraphCanvas>
          </ReactFlowProvider>
        )}
      </div>

      <DropdownMenu
        open={nodeMenu !== null}
        onOpenChange={(open) => {
          if (!open) {
            setNodeMenu(null);
          }
        }}
      >
        {/* An invisible anchor pinned to the right-click position, so the menu opens at the pointer. */}
        <DropdownMenuTrigger asChild>
          <span aria-hidden className="fixed size-px" style={{ top: nodeMenu?.y ?? 0, left: nodeMenu?.x ?? 0 }} />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-56">
          {menuNode !== null && menuNode.type !== "stack" && (
            <DropdownMenuItem data-testid="node-menu-trace" onSelect={() => trace(menuNode.id)}>
              Trace upstream / downstream
            </DropdownMenuItem>
          )}
          {menuNode !== null && menuNode.type === "group" && (
            <DropdownMenuItem data-testid="node-menu-open-folder" onSelect={() => openFolder(menuNode.id)}>
              Open folder
            </DropdownMenuItem>
          )}
          {menuNode !== null && menuNode.type === "stack" && (
            <DropdownMenuItem data-testid="node-menu-unstack" onSelect={() => openStack(menuNode.id)}>
              Unstack
            </DropdownMenuItem>
          )}
          {menuFolder !== null && grouped && params.open.includes(menuFolder) && (
            <DropdownMenuItem data-testid="node-menu-close-folder" onSelect={() => closeFolder(menuFolder)}>
              Collapse its folder
            </DropdownMenuItem>
          )}
          {menuNode !== null && menuNode.frontierKeys.length > 0 && (
            <DropdownMenuItem data-testid="node-menu-expand" onSelect={() => cardActions.onExpandFrontier(menuNode.frontierKeys)}>
              Load what is further downstream
            </DropdownMenuItem>
          )}
          {menuNode !== null && (menuNode.type === "object" || (menuNode.type === "flow" && kindOf(menuNode.id) === "sp")) && (
            <DropdownMenuItem
              data-testid="node-menu-view-script"
              onSelect={() => setScriptTarget(menuNode.type === "flow" ? { key: menuNode.id, view: "object" } : { key: menuNode.id })}
            >
              View script
            </DropdownMenuItem>
          )}
          {menuNode !== null && menuNode.type === "flow" && (
            <DropdownMenuItem data-testid="node-menu-view-yaml" onSelect={() => setScriptTarget({ key: menuNode.id })}>
              View YAML
            </DropdownMenuItem>
          )}
          {menuNode !== null && (menuNode.type === "flow" || menuNode.type === "object") && (
            <DropdownMenuItem data-testid="node-menu-open" onSelect={() => activate(menuNode.id)}>
              {openTarget(menuNode.id)?.label ?? "Open details"}
            </DropdownMenuItem>
          )}
          {menuCanRun && menuFlow !== null && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem data-testid="node-menu-run-flow" onSelect={() => setRunDialog({ flowName: menuFlow.name, scope: "flow" })}>
                Run flow
              </DropdownMenuItem>
              <DropdownMenuItem data-testid="node-menu-run-node" onSelect={() => setRunDialog({ flowName: menuFlow.name, scope: "node" })}>
                Run flow + descendants
              </DropdownMenuItem>
              <DropdownMenuItem data-testid="node-menu-run-batch" onSelect={() => setRunDialog({ flowName: menuFlow.name, scope: "batch" })}>
                Run batch
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      <TriggerRunDialog
        open={runDialog !== null}
        onClose={() => setRunDialog(null)}
        repoId={params.repoId || undefined}
        flowName={runDialog?.flowName}
        scope={runDialog?.scope}
      />

      <Sheet
        open={scriptTarget !== null}
        onOpenChange={(open) => {
          if (!open) {
            setScriptTarget(null);
          }
        }}
      >
        {/* Focus stays outside Monaco so Escape reaches the sheet, not the editor. */}
        <SheetContent side="right" className="w-full gap-0 sm:max-w-[760px]" onOpenAutoFocus={(event) => event.preventDefault()}>
          <SheetHeader className="border-b border-border pr-10">
            <SheetTitle className="truncate font-mono text-sm">
              {scriptQuery.data?.name ?? scriptTarget?.key ?? ""}
            </SheetTitle>
            <SheetDescription className="sr-only">
              The captured script behind the selected lineage node.
            </SheetDescription>
            {scriptQuery.data && (
              <div className="flex flex-wrap items-center gap-1.5">
                <Badge>{scriptQuery.data.kind}</Badge>
                <Badge variant="outline">{scriptQuery.data.language.toUpperCase()}</Badge>
                {scriptQuery.data.source && <Badge variant="outline">{scriptQuery.data.source}</Badge>}
              </div>
            )}
          </SheetHeader>
          <div className="min-h-0 flex-1 overflow-y-auto p-4">
            {scriptQuery.isLoading && (
              <div className="flex justify-center py-8">
                <Loader2 className="size-5 animate-spin text-muted-foreground" />
              </div>
            )}
            {scriptQuery.isError && (isApiError(scriptQuery.error)
              ? <CorrelationError error={scriptQuery.error} />
              : <p className="text-[13px] text-destructive">{String(scriptQuery.error)}</p>)}
            {scriptQuery.data && (scriptQuery.data.script
              ? (
                <CodeView
                  value={scriptQuery.data.script}
                  language={scriptQuery.data.language === "yaml" ? "yaml" : "sql"}
                  height="calc(100vh - 190px)"
                  data-testid="node-script-body"
                />
              )
              : <EmptyState title="No script" description="This node has no captured script yet. For database objects, run a connected sync (--connect) so the source is read." />)}
          </div>
        </SheetContent>
      </Sheet>

      {/* The full analysis behind the panel's verdict: the same sheet the data-streams board opens, so a
          stream checked while tracing lineage and one checked from the board are the same page. */}
      <StreamDetailSheet
        pipelineId={streamTarget?.pipelineId ?? null}
        flowName={streamTarget?.flowName ?? null}
        windowDays={streamWindowDays}
        includeBackfills={streamIncludeBackfills}
        onClose={() => setStreamTarget(null)}
      />
    </div>
  );
}
