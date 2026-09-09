import { useEffect, useMemo, useRef, useState } from "react";
import {
  Check, CheckCircle2, CircleAlert, Copy, Eraser, Loader2, Maximize2, OctagonAlert, Radio, TriangleAlert,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { prettyPrintSql } from "@/lib/sql";
import { parseUtc } from "../lib/time";

/** One line of a trace, in the terminal-log shape both the run trace and the activity (sync/lineage/...) trace
 * render through. Statement/SQL lines carry `sql`; a failing line carries `error`; `groupKey` draws a separator
 * between successive groups (a run's attempts, an activity's runs). */
export interface TraceLine {
  key: string;
  timestampUtc: string | null;
  /** The short step/kind tag shown in the fixed tag column. */
  tag: string;
  level: "trace" | "debug" | "info" | "warning" | "error";
  message: string;
  sql?: string | null;
  error?: string | null;
  groupKey?: string;
}

/**
 * Message text stays neutral at every level: a trace can be thousands of warnings long, and tinting all of them
 * turns the panel into a wall of one color where nothing stands out. Severity is carried by the leading glyph
 * (see {@link LevelMark}) instead, so a handful of problems read as problems against calm text.
 */
function levelClass(level: TraceLine["level"]): string {
  return level === "trace" || level === "debug" ? "text-muted-foreground" : "text-foreground";
}

/** The shape+color severity glyph for a warning/error line; nothing at all for the ordinary levels. */
function LevelMark({ level }: { level: TraceLine["level"] }) {
  if (level === "error") {
    return <OctagonAlert className="mr-1.5 inline size-3.5 shrink-0 align-[-2px] text-destructive" />;
  }

  if (level === "warning") {
    return <TriangleAlert className="mr-1.5 inline size-3.5 shrink-0 align-[-2px] text-warning" />;
  }

  return null;
}

/** A compact UTC clock stamp (HH:mm:ss.fff); "-" for a line without a timestamp. */
function fmtTime(value: string | null): string {
  return value ? parseUtc(value).toISOString().slice(11, 23) : "-";
}

/** The live/complete/failed pill for a trace panel header. */
function StatusPill({ connected, ended, failed }: { connected: boolean; ended: boolean; failed: boolean }) {
  if (ended) {
    return (
      <span
        className={cn(
          "inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4",
          failed ? "bg-destructive/12 text-destructive" : "bg-success/12 text-success",
        )}
      >
        {failed ? <CircleAlert className="size-3.5 shrink-0" /> : <CheckCircle2 className="size-3.5 shrink-0" />}
        {failed ? "failed" : "complete"}
      </span>
    );
  }

  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4",
        connected ? "bg-success/12 text-success" : "bg-warning/15 text-warning",
      )}
    >
      {connected ? <Radio className="size-3.5 shrink-0" /> : <Loader2 className="size-3.5 shrink-0 animate-spin" />}
      {connected ? "live" : "reconnecting"}
    </span>
  );
}

/** A small ghost icon+label button for the panel header (copy, clear). */
function HeaderButton({
  icon: Icon, label, onClick, disabled, "data-testid": testId,
}: {
  icon: typeof Copy;
  label: string;
  onClick: () => void;
  disabled?: boolean;
  "data-testid"?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      data-testid={testId}
      className="inline-flex items-center gap-1 rounded-sm px-1.5 py-0.5 text-[11px] text-muted-foreground hover:bg-muted hover:text-foreground disabled:pointer-events-none disabled:opacity-40"
    >
      <Icon className="size-3.5 shrink-0" />
      {label}
    </button>
  );
}

/** Flatten any run of whitespace (incl. newlines) to single spaces, for the single-line row preview of a SQL/multi
 * -line message. The untouched original is what the hover card and the copy button carry. */
function collapse(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

/** The plain-text a single line copies: its message, then its SQL (formatted the way the card shows it), then its
 * error. Computed on demand so the pretty-print cost is paid only when the button is clicked, not per row. */
function lineText(line: TraceLine): string {
  const parts = [line.message.trimEnd()];
  if (line.sql) {
    parts.push(prettyPrintSql(line.sql).trimEnd());
  }
  if (line.error) {
    parts.push(`!! ${line.error}`);
  }
  return parts.filter((p) => p.length > 0).join("\n\n");
}

/** A small copy-to-clipboard button that flips to a check for a beat, used inside a line's hover card. `getText` is
 * called on click so any formatting work happens only when the user actually copies. */
function CopyLineButton({ getText }: { getText: () => string }) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => {
    if (timer.current) {
      clearTimeout(timer.current);
    }
  }, []);

  const doCopy = async () => {
    try {
      await navigator.clipboard.writeText(getText());
      setCopied(true);
      if (timer.current) {
        clearTimeout(timer.current);
      }
      timer.current = setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard denied (permissions/insecure context): leave the button idle rather than surface a toast here.
    }
  };

  return (
    <button
      type="button"
      onClick={doCopy}
      className="inline-flex items-center gap-1 rounded-sm px-1.5 py-0.5 text-[11px] text-muted-foreground hover:bg-muted hover:text-foreground"
    >
      {copied ? <Check className="size-3.5 shrink-0 text-success" /> : <Copy className="size-3.5 shrink-0" />}
      {copied ? "Copied" : "Copy"}
    </button>
  );
}

/** The pretty-printed SQL block inside a line's hover card. Kept as its own component so the (best-effort) format
 * runs only when a card actually opens, since Radix mounts the card content lazily, never per row up front. */
function SqlBlock({ sql, className }: { sql: string; className?: string }) {
  const pretty = useMemo(() => prettyPrintSql(sql), [sql]);
  return (
    <pre
      className={cn(
        "overflow-x-auto whitespace-pre rounded-sm bg-muted/40 px-2 py-1 text-muted-foreground",
        className,
      )}
    >
      {pretty}
    </pre>
  );
}

/**
 * One trace line as a table row: a timestamp column and a tag column that both size to their content (the tag is
 * never clipped), then a single event column that takes the remaining width and holds the message with an inline,
 * dimmed one-line SQL/error preview, and finally a narrow expand column.
 *
 * Only the event column clips (never wraps, never widens the panel), and the full line lives behind the expand
 * button: a click opens a popover with the message, the SQL pretty-printed, any error, and a copy button. The
 * detail is deliberately click-only. A hover trigger fires on every line the pointer crosses, so scrolling a long
 * trace flashes a string of cards nobody asked for; the button only appears on the row under the pointer (or the
 * focused row) and stays put until it is clicked.
 */
function TraceRow({ line, separated }: { line: TraceLine; separated: boolean }) {
  const level = levelClass(line.level);
  // A statement row carries no message (its SQL is the event), so the message is only shown when present, and
  // leading margins are only added between parts that actually render.
  const hasMsg = line.message.trim().length > 0;

  return (
    <>
      {separated ? (
        <tr aria-hidden>
          <td colSpan={4} className="p-0">
            <div className="my-1 border-t border-dashed border-border/60" />
          </td>
        </tr>
      ) : null}
      <tr className={cn("group align-top", line.level === "error" && "bg-destructive/6")}>
        <td className="whitespace-nowrap pr-3 align-top text-muted-foreground tabular-nums">
          {fmtTime(line.timestampUtc)}
        </td>
        <td className="whitespace-nowrap pr-3 align-top uppercase text-muted-foreground">
          {line.tag}
        </td>
        <td className="w-full max-w-0 p-0 align-top">
          <div className="truncate">
            <LevelMark level={line.level} />
            {hasMsg ? <span className={level}>{line.message}</span> : null}
            {line.sql ? (
              <span className={cn(hasMsg && "ml-2", "text-muted-foreground/80")}>{collapse(line.sql)}</span>
            ) : null}
            {line.error ? (
              <span className={cn((hasMsg || line.sql) && "ml-2", "text-destructive")}>!! {collapse(line.error)}</span>
            ) : null}
          </div>
        </td>
        <td className="w-0 whitespace-nowrap pl-2 align-top">
          <Popover>
            <PopoverTrigger asChild>
              <button
                type="button"
                title="Show the full line"
                aria-label="Show the full line"
                data-testid="trace-line-expand"
                className="inline-flex rounded-sm p-0.5 text-muted-foreground opacity-0 hover:bg-muted hover:text-foreground focus-visible:opacity-100 focus-visible:outline-none group-hover:opacity-100 data-[state=open]:bg-muted data-[state=open]:text-foreground data-[state=open]:opacity-100"
              >
                <Maximize2 className="size-3.5 shrink-0" />
              </button>
            </PopoverTrigger>
            <PopoverContent
              align="end"
              side="top"
              collisionPadding={8}
              className="w-[min(90vw,760px)] overflow-hidden p-0"
            >
              <div className="flex items-center justify-between border-b border-border px-3 py-1.5 text-[11px] text-muted-foreground">
                <span className="truncate">
                  <span className="tabular-nums">{fmtTime(line.timestampUtc)}</span>
                  <span className="mx-1.5">·</span>
                  <span className="uppercase">{line.tag}</span>
                </span>
                <CopyLineButton getText={() => lineText(line)} />
              </div>
              <div className="max-h-[50vh] overflow-auto px-3 py-2 font-mono text-[12px] leading-5">
                {hasMsg ? (
                  <p className={cn("whitespace-pre-wrap break-words", level)}>
                    <LevelMark level={line.level} />
                    {line.message}
                  </p>
                ) : null}
                {line.sql ? <SqlBlock sql={line.sql} className={cn(hasMsg && "mt-2")} /> : null}
                {line.error ? (
                  <div
                    className={cn("whitespace-pre-wrap break-words text-destructive", (hasMsg || line.sql) && "mt-2")}
                  >
                    !! {line.error}
                  </div>
                ) : null}
              </div>
            </PopoverContent>
          </Popover>
        </td>
      </tr>
    </>
  );
}

/**
 * The shared bottom-panel trace view: a terminal-style output log for a stream of {@link TraceLine}s, newest line
 * at the bottom, auto-scrolling while it streams. One look for every trace (a pipeline run, a repository sync, a
 * lineage computation): level-tinted lines with a fixed tag column, indented SQL/error blocks, a thin separator
 * between groups, and a header carrying Copy trace, Clear (console-style, hides current lines while the stream
 * keeps running), and a live/complete/failed pill. Copy is delegated to the owner (which decides what the whole
 * trace is); Clear is a view-only action.
 */
export function TraceLog({
  lines,
  connected,
  ended,
  failed,
  onCopy,
  emptyLive = "Waiting for the first line…",
  emptyEnded = "No trace to show.",
}: {
  lines: TraceLine[];
  connected: boolean;
  ended: boolean;
  failed: boolean;
  onCopy: () => void | Promise<void>;
  emptyLive?: string;
  emptyEnded?: string;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  // Clear hides every line up to and including this key (console-style). New lines still arrive; a wholesale
  // replacement of the array (the live->at-rest swap) drops the watermark, showing everything again.
  const [hiddenUpTo, setHiddenUpTo] = useState<string | null>(null);
  const [copying, setCopying] = useState(false);
  // Problems-only filter: one click isolates every warning/error line, so a buried failure (a lineage server
  // the sync could not reach, a degraded tier) is caught without scrolling a hundred info lines.
  const [problemsOnly, setProblemsOnly] = useState(false);

  const visible = useMemo(() => {
    if (hiddenUpTo === null) {
      return lines;
    }

    const cut = lines.findIndex((l) => l.key === hiddenUpTo);
    return cut === -1 ? lines : lines.slice(cut + 1);
  }, [lines, hiddenUpTo]);

  const problems = useMemo(
    () => visible.filter((l) => l.level === "warning" || l.level === "error" || l.error),
    [visible],
  );
  const errorCount = useMemo(
    () => problems.filter((l) => l.level === "error" || l.error).length,
    [problems],
  );
  const shown = problemsOnly && problems.length > 0 ? problems : visible;

  // Follow the tail as lines arrive (and on connect), the way a terminal does.
  useEffect(() => {
    const el = scrollRef.current;
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
  }, [shown.length, ended]);

  const doCopy = async () => {
    setCopying(true);
    try {
      await onCopy();
    } finally {
      setCopying(false);
    }
  };

  return (
    <div className="flex h-full flex-col">
      <div className="flex h-7 shrink-0 items-center justify-between border-b border-border px-3">
        <span className="text-[11px] text-muted-foreground">
          {visible.length} line{visible.length === 1 ? "" : "s"}
        </span>
        <div className="flex items-center gap-1">
          {problems.length > 0 ? (
            <button
              type="button"
              onClick={() => setProblemsOnly((v) => !v)}
              data-testid="trace-problems"
              aria-pressed={problemsOnly}
              className={cn(
                "inline-flex items-center gap-1 rounded-sm px-1.5 py-0.5 text-[11px] font-medium",
                errorCount > 0 ? "text-destructive" : "text-warning",
                problemsOnly ? "bg-muted" : "hover:bg-muted",
              )}
            >
              <CircleAlert className="size-3.5 shrink-0" />
              {problems.length} problem{problems.length === 1 ? "" : "s"}
            </button>
          ) : null}
          <HeaderButton
            icon={copying ? Loader2 : Copy}
            label="Copy trace"
            onClick={doCopy}
            disabled={copying || lines.length === 0}
            data-testid="trace-copy"
          />
          <HeaderButton
            icon={Eraser}
            label="Clear"
            onClick={() => setHiddenUpTo(visible.length > 0 ? visible[visible.length - 1].key : hiddenUpTo)}
            disabled={visible.length === 0}
            data-testid="trace-clear"
          />
          <StatusPill connected={connected} ended={ended} failed={failed} />
        </div>
      </div>

      {problems.length > 0 && !problemsOnly ? (
        <button
          type="button"
          onClick={() => setProblemsOnly(true)}
          data-testid="trace-problems-band"
          className={cn(
            "flex w-full shrink-0 items-center gap-2 border-b px-3 py-1 text-left text-[11px]",
            errorCount > 0
              ? "border-destructive/30 bg-destructive/8 text-destructive"
              : "border-warning/30 bg-warning/8 text-warning",
          )}
        >
          <CircleAlert className="size-3.5 shrink-0" />
          <span className="truncate">
            <span className="font-medium">
              {errorCount > 0
                ? `${errorCount} error${errorCount === 1 ? "" : "s"}, ${problems.length - errorCount} warning${problems.length - errorCount === 1 ? "" : "s"}`
                : `${problems.length} warning${problems.length === 1 ? "" : "s"}`}
            </span>
            <span className="mx-1.5 text-muted-foreground">·</span>
            <span className="text-foreground/80">{collapse(problems[0].error ?? problems[0].message)}</span>
          </span>
          <span className="ml-auto shrink-0 text-muted-foreground">show only problems</span>
        </button>
      ) : null}
      {problemsOnly && problems.length > 0 ? (
        <button
          type="button"
          onClick={() => setProblemsOnly(false)}
          data-testid="trace-problems-all"
          className="flex w-full shrink-0 items-center gap-2 border-b border-border bg-muted/40 px-3 py-1 text-left text-[11px] text-muted-foreground hover:text-foreground"
        >
          <CircleAlert className="size-3.5 shrink-0" />
          <span>Showing only the {problems.length} problem line{problems.length === 1 ? "" : "s"}</span>
          <span className="ml-auto shrink-0">show all lines</span>
        </button>
      ) : null}

      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto px-3 py-2 font-mono text-[12px] leading-5">
        {shown.length === 0 ? (
          <p className="text-muted-foreground">{ended ? emptyEnded : emptyLive}</p>
        ) : (
          <table className="w-full border-collapse">
            <tbody>
              {shown.map((line, i) => {
                const newGroup = !problemsOnly && i > 0 && line.groupKey !== undefined
                  && shown[i - 1].groupKey !== line.groupKey;
                return <TraceRow key={line.key} line={line} separated={newGroup} />;
              })}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
