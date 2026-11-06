import { useRef, useState, type MouseEvent } from "react";
import { Check, CircleCheck, Loader2, OctagonAlert, SquareTerminal, TriangleAlert, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useTraceTray } from "./TraceTray";
import { digest, isRunning, toneOf, type HeldTrace, type TraceTone } from "./traceTrayState";

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? "" : "s"}`;
}

/** How a held trace is going, in a few words: running, or how it ended and with what. */
function outcomeText(trace: HeldTrace): string {
  const status = trace.status;
  if (status === null || !status.ended) {
    return "running";
  }

  if (status.failed) {
    return "failed";
  }

  const warnings = status.problems - status.errors;
  if (status.errors > 0) {
    return warnings > 0 ? `${plural(status.errors, "error")}, ${plural(warnings, "warning")}` : plural(status.errors, "error");
  }

  return warnings > 0 ? `complete, ${plural(warnings, "warning")}` : "complete";
}

/** The indicator's name for assistive tech and its tooltip's first line. */
function summaryText(traces: readonly HeldTrace[]): string {
  if (traces.length === 1) {
    return `${traces[0].title}: ${outcomeText(traces[0])}`;
  }

  const running = traces.filter(isRunning).length;
  const ended = traces.length - running;
  return [running > 0 ? `${running} running` : null, ended > 0 ? `${ended} ended` : null]
    .filter((part) => part !== null)
    .join(", ");
}

// The title bar is dark chrome in both modes, so every mark drawn on it reads the dark tokens (the `dark` scope on each)
// in both: the light mode's blue and status hues sit too close to its navy to be told apart.

/** The fill and glyph color of the outcome badge: the workbench's status hues. */
const BADGE_TONE: Record<TraceTone, string> = {
  success: "bg-success text-success-foreground",
  warning: "bg-warning text-warning-foreground",
  error: "bg-destructive text-destructive-foreground",
};

/** The border color the rings spread in: the one blue while a trace runs, an outcome's hue once it ends. */
const RING_TONE: Record<TraceTone | "running", string> = {
  running: "border-primary",
  success: "border-success",
  warning: "border-warning",
  error: "border-destructive",
};

/** An exclamation mark drawn as lucide draws its glyphs, which has no bare one: the warning badge's mark. */
function Bang({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={3.5} strokeLinecap="round" className={className}>
      <path d="M12 5v9" />
      <path d="M12 19h.01" />
    </svg>
  );
}

/**
 * The ring around the icon while a trace runs: a faint track, and an arc in the one blue that turns, lengthening and
 * shortening as it goes. Without motion it holds still as a quarter of the ring from twelve o'clock.
 */
function RunningRing() {
  return (
    <svg
      viewBox="0 0 28 28"
      aria-hidden
      className="dark pointer-events-none absolute inset-0 size-full motion-safe:animate-[trace-turn_1.6s_linear_infinite]"
      data-testid="trace-indicator-ring"
    >
      <circle cx="14" cy="14" r="12.5" fill="none" strokeWidth="1.5" className="stroke-title-bar-foreground/15" />
      <circle
        cx="14"
        cy="14"
        r="12.5"
        fill="none"
        strokeWidth="2"
        strokeLinecap="round"
        pathLength={100}
        strokeDasharray="25 200"
        className="origin-center -rotate-90 stroke-primary [transform-box:fill-box] motion-safe:animate-[trace-arc_1.6s_ease-in-out_infinite]"
        style={{ filter: "drop-shadow(0 0 2px var(--primary))" }}
      />
    </svg>
  );
}

/**
 * Two rings spreading from the icon, half a beat apart: without end while a trace runs or a failure waits unread, twice
 * when another outcome arrives. Nothing is drawn without motion; the ring and the badge carry the state then.
 */
function Ripples({ tone, endless }: { tone: TraceTone | "running"; endless: boolean }) {
  const ring = cn(
    "dark pointer-events-none absolute inset-0 rounded-full border opacity-0",
    RING_TONE[tone],
    endless
      ? "motion-safe:animate-[trace-ripple_2.4s_cubic-bezier(0,0,0.2,1)_infinite]"
      : "motion-safe:animate-[trace-ripple_1.2s_cubic-bezier(0,0,0.2,1)_2]",
  );
  return (
    <>
      <span aria-hidden className={ring} />
      <span aria-hidden className={cn(ring, endless ? "[animation-delay:1.2s]" : "[animation-delay:0.6s]")} />
    </>
  );
}

/**
 * The outcome badge on the icon's corner, a shape per tone so it never reads by color alone. Its ring is cut in the
 * title bar's own color, outside the dark scope its fill reads.
 */
function OutcomeBadge({ tone }: { tone: TraceTone }) {
  const glyph = "size-2.5";
  return (
    <span
      aria-hidden
      data-testid="trace-indicator-badge"
      className="pointer-events-none absolute right-0 top-0 rounded-full ring-2 ring-title-bar motion-safe:animate-in motion-safe:fade-in-0 motion-safe:duration-200"
    >
      <span className={cn("dark flex size-3.5 items-center justify-center rounded-full", BADGE_TONE[tone])}>
        {tone === "success"
          ? <Check className={glyph} strokeWidth={4} />
          : tone === "warning" ? <Bang className={glyph} /> : <X className={glyph} strokeWidth={4} />}
      </span>
    </span>
  );
}

/** A held trace's state in the list: turning while it runs, its outcome's glyph once it ends. */
function TraceGlyph({ trace }: { trace: HeldTrace }) {
  const tone = toneOf(trace);
  if (tone === null) {
    return <Loader2 className="size-4 shrink-0 text-primary motion-safe:animate-spin" />;
  }

  if (tone === "error") {
    return <OctagonAlert className="size-4 shrink-0 text-destructive" />;
  }

  return tone === "warning"
    ? <TriangleAlert className="size-4 shrink-0 text-warning" />
    : <CircleCheck className="size-4 shrink-0 text-success" />;
}

/**
 * The title bar's trace indicator: the operation traces the trace tray holds, so an operation started from a page (a
 * sync, a discovery, a run) runs without the bottom panel taking the page's room. While one runs, a ring turns around
 * the icon, the icon breathes and rings spread from it; one that ends unread leaves a badge (a check, a warning mark, a
 * cross) and sends the rings out once, again and again for a failure. A click opens the trace in the bottom panel, or
 * closes it when it is shown; with several held, it lists them, newest first. Drawn only while the tray holds a trace.
 */
export function TraceIndicator() {
  const { traces, shownId, show, hide, dismiss } = useTraceTray();
  const [listOpen, setListOpen] = useState(false);
  // A trace picked from the list opens in the panel; focus going back to the button would raise its tooltip over it.
  const picked = useRef(false);
  const several = traces.length > 1;
  // The list closes when it would hold one trace, so it never springs open again when a second arrives.
  if (listOpen && !several) {
    setListOpen(false);
  }

  if (traces.length === 0) {
    return null;
  }

  const { running, attention } = digest(traces);
  const summary = summaryText(traces);
  // A new unread outcome changes the key, so the rings go out for it.
  const unreadKey = traces.filter((trace) => trace.unseen).map((trace) => trace.id).join("|");

  // One trace is opened or closed straight away; several are listed (the trigger's own toggle).
  const toggleOnly = (event: MouseEvent) => {
    if (several) {
      return;
    }

    event.preventDefault();
    if (shownId === traces[0].id) {
      hide();
    } else {
      show(traces[0].id);
    }
  };

  return (
    <Popover open={listOpen && several} onOpenChange={setListOpen}>
      <Tooltip>
        <TooltipTrigger asChild>
          <PopoverTrigger asChild>
            <button
              type="button"
              data-testid="trace-indicator"
              data-running={running}
              data-attention={attention ?? undefined}
              aria-label={`Traces: ${summary}`}
              aria-pressed={shownId !== null}
              onClick={toggleOnly}
              className={cn(
                "relative flex size-7 shrink-0 items-center justify-center rounded-md text-title-bar-foreground/80 transition-colors hover:bg-title-bar-foreground/10 hover:text-title-bar-foreground",
                "motion-safe:animate-in motion-safe:fade-in-0 motion-safe:duration-200",
                shownId !== null && "bg-title-bar-foreground/10 text-title-bar-foreground",
              )}
            >
              {running > 0 ? <RunningRing /> : null}
              {running > 0
                ? <Ripples tone="running" endless />
                : attention !== null
                  ? <Ripples key={unreadKey} tone={attention} endless={attention === "error"} />
                  : null}
              <SquareTerminal
                className={cn("relative size-4", running > 0 && "motion-safe:animate-[trace-breathe_1.6s_ease-in-out_infinite]")}
              />
              {attention !== null ? <OutcomeBadge tone={attention} /> : null}
            </button>
          </PopoverTrigger>
        </TooltipTrigger>
        <TooltipContent side="bottom" className="max-w-xs">
          <span className="block font-medium">{several ? `Traces: ${summary}` : summary}</span>
          <span className="block opacity-70">
            {several
              ? "Click to list them."
              : shownId === traces[0].id ? "Click to close its trace." : "Click to open its trace."}
          </span>
        </TooltipContent>
      </Tooltip>
      <PopoverContent
        align="end"
        className="w-80 p-0"
        data-testid="trace-tray"
        onCloseAutoFocus={(event) => {
          if (picked.current) {
            picked.current = false;
            event.preventDefault();
          }
        }}
      >
        <div className="flex h-8 items-center border-b border-border px-3 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
          Traces
        </div>
        <ul className="max-h-80 overflow-y-auto py-1">
          {/* Newest on top: the operation just started is the one looked for. */}
          {[...traces].reverse().map((trace) => (
            <li key={trace.id} className="group/trace flex items-center" data-testid="trace-tray-item">
              <button
                type="button"
                onClick={() => {
                  picked.current = true;
                  show(trace.id);
                  setListOpen(false);
                }}
                aria-current={shownId === trace.id ? "true" : undefined}
                className={cn(
                  "flex min-w-0 flex-1 items-center gap-2.5 px-3 py-1.5 text-left hover:bg-accent",
                  shownId === trace.id && "bg-accent/60",
                )}
              >
                <TraceGlyph trace={trace} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[13px] text-foreground">{trace.title}</span>
                  <span className="block truncate text-[11px] text-muted-foreground">{outcomeText(trace)}</span>
                </span>
                {trace.unseen ? (
                  <span className="size-1.5 shrink-0 rounded-full bg-primary" data-testid="trace-tray-unread">
                    <span className="sr-only">not opened yet</span>
                  </span>
                ) : null}
              </button>
              <button
                type="button"
                onClick={() => dismiss(trace.id)}
                aria-label={`Remove ${trace.title} from the traces`}
                data-testid="trace-tray-dismiss"
                className="mr-1.5 shrink-0 rounded-sm p-1 text-muted-foreground opacity-0 hover:bg-accent hover:text-foreground focus-visible:opacity-100 group-hover/trace:opacity-100"
              >
                <X className="size-3.5" />
              </button>
            </li>
          ))}
        </ul>
      </PopoverContent>
    </Popover>
  );
}
