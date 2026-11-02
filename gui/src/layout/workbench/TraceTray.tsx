import {
  createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { usePanel } from "./PanelContext";
import {
  holdAsked, holdStarted, markSeen, report, retireSeen, type HeldTrace, type TraceStatus,
} from "./traceTrayState";

/** The bottom panel content id a held trace is shown under, before the trace's own id. */
const PANEL_PREFIX = "trace:";

/** A trace to hold: an operation's live log, rendered through TraceLog so it reports how it is going. */
export interface TraceSpec {
  /** One operation on one subject is one trace (a repository's sync, a run): asking again for the id reaches it. */
  id: string;
  title: string;
  node: ReactNode;
}

interface HeldEntry extends HeldTrace {
  node: ReactNode;
  /** Which start of the trace the view renders: a new start mounts a new view, so its stream begins again. */
  generation: number;
  /** The element the view renders into, the same one for every start of the trace. */
  element: HTMLDivElement;
}

interface TraceTrayValue {
  traces: readonly HeldTrace[];
  /** The held trace the bottom panel shows, if it shows one. */
  shownId: string | null;
  /** Holds the trace of an operation just started, leaving the page as it is; a held one starts again. */
  follow: (spec: TraceSpec) => void;
  /** Holds a trace someone asked to see and shows it in the bottom panel; a held one is shown as it is. */
  open: (spec: TraceSpec) => void;
  /** Shows a held trace in the bottom panel. */
  show: (id: string) => void;
  /** Closes the bottom panel when it shows a held trace. */
  hide: () => void;
  /** Lets go of a trace, closing the bottom panel when it shows it. */
  dismiss: (id: string) => void;
}

const TraceTrayContext = createContext<TraceTrayValue | null>(null);

/** Where a trace's view reports its status; set for every view the tray holds, unset anywhere else. */
const TraceStatusContext = createContext<((status: TraceStatus) => void) | null>(null);

/** The reporter of the trace a view belongs to, or null when the view is not a held trace. */
export function useTraceStatusReport(): ((status: TraceStatus) => void) | null {
  return useContext(TraceStatusContext);
}

/**
 * Moves a held trace's element into the bottom panel while the panel shows it, and back out when it stops. The view
 * inside stays mounted throughout, so its stream, its scrollback and its filters survive the panel closing.
 */
function TraceMount({ element }: { element: HTMLDivElement }) {
  const host = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const target = host.current;
    if (target === null) {
      return undefined;
    }

    target.appendChild(element);
    return () => {
      if (element.parentNode === target) {
        target.removeChild(element);
      }
    };
  }, [element]);

  return <div ref={host} className="h-full" />;
}

/** One held trace's view, rendered into its element with its reporter around it. */
function HeldTraceView({ entry, onStatus }: { entry: HeldEntry; onStatus: (id: string, status: TraceStatus) => void }) {
  const { id } = entry;
  const reportStatus = useCallback((status: TraceStatus) => onStatus(id, status), [onStatus, id]);
  return createPortal(
    <TraceStatusContext.Provider value={reportStatus}>{entry.node}</TraceStatusContext.Provider>,
    entry.element,
  );
}

/**
 * The trace tray: the operation traces the workbench holds, so starting an operation never takes the page's room.
 * A held trace's view stays mounted and streaming in an element of its own, out of the document until the bottom
 * panel shows it; the title bar's indicator (TraceIndicator) says what the held traces are doing and opens them. A
 * trace that ends unseen asks for a look there; one that has been read and closed leaves the tray.
 */
export function TraceTrayProvider({ children }: { children: ReactNode }) {
  const { content, open: openPanel, close: closePanel } = usePanel();
  const [traces, setTraces] = useState<HeldEntry[]>([]);
  const generations = useRef(0);
  const elements = useRef(new Map<string, HTMLDivElement>());

  const shownId = content !== null && content.id.startsWith(PANEL_PREFIX) ? content.id.slice(PANEL_PREFIX.length) : null;
  // The shown trace as the callbacks below read it, so they stay stable while the panel changes.
  const shown = useRef(shownId);
  useLayoutEffect(() => {
    shown.current = shownId;
  }, [shownId]);

  const elementFor = useCallback((id: string) => {
    let element = elements.current.get(id);
    if (element === undefined) {
      element = document.createElement("div");
      element.className = "h-full";
      elements.current.set(id, element);
    }

    return element;
  }, []);

  const showInPanel = useCallback(
    (id: string, title: string) => openPanel({ id: PANEL_PREFIX + id, title, node: <TraceMount element={elementFor(id)} /> }),
    [openPanel, elementFor],
  );

  const entryFor = useCallback((spec: TraceSpec): HeldEntry => {
    generations.current += 1;
    return { ...spec, status: null, unseen: false, generation: generations.current, element: elementFor(spec.id) };
  }, [elementFor]);

  const follow = useCallback((spec: TraceSpec) => {
    const entry = entryFor(spec);
    setTraces((held) => holdStarted(held, entry));
    // Started again while shown: the panel keeps showing it, under the title this start gave it.
    if (shown.current === spec.id) {
      showInPanel(spec.id, spec.title);
    }
  }, [entryFor, showInPanel]);

  const open = useCallback((spec: TraceSpec) => {
    const entry = entryFor(spec);
    setTraces((held) => holdAsked(held, entry));
    showInPanel(spec.id, spec.title);
  }, [entryFor, showInPanel]);

  const show = useCallback((id: string) => {
    const entry = traces.find((held) => held.id === id);
    if (entry !== undefined) {
      showInPanel(entry.id, entry.title);
    }
  }, [traces, showInPanel]);

  const hide = useCallback(() => {
    if (shown.current !== null) {
      closePanel();
    }
  }, [closePanel]);

  const dismiss = useCallback((id: string) => {
    setTraces((held) => held.filter((entry) => entry.id !== id));
    if (shown.current === id) {
      closePanel();
    }
  }, [closePanel]);

  const onStatus = useCallback((id: string, status: TraceStatus) => {
    setTraces((held) => report(held, id, status, shown.current));
  }, []);

  // Showing a trace is reading it; what the panel stops showing, ended and read, leaves the tray.
  useEffect(() => {
    setTraces((held) => retireSeen(markSeen(held, shownId), shownId));
  }, [shownId]);

  // A trace that left the tray takes its element with it.
  useEffect(() => {
    const held = new Set(traces.map((entry) => entry.id));
    for (const id of elements.current.keys()) {
      if (!held.has(id)) {
        elements.current.delete(id);
      }
    }
  }, [traces]);

  const value = useMemo<TraceTrayValue>(
    () => ({ traces, shownId, follow, open, show, hide, dismiss }),
    [traces, shownId, follow, open, show, hide, dismiss],
  );

  return (
    <TraceTrayContext.Provider value={value}>
      {children}
      {traces.map((entry) => (
        <HeldTraceView key={`${entry.id}:${entry.generation}`} entry={entry} onStatus={onStatus} />
      ))}
    </TraceTrayContext.Provider>
  );
}

export function useTraceTray(): TraceTrayValue {
  const context = useContext(TraceTrayContext);
  if (!context) {
    throw new Error("useTraceTray must be used inside TraceTrayProvider.");
  }

  return context;
}
