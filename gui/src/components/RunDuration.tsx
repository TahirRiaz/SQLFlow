import { format } from "date-fns";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { useNow } from "../hooks/useNow";
import { secondsSince, waitedSeconds } from "../lib/runTiming";
import { formatDurationSeconds, parseUtc } from "../lib/time";

interface RunDurationProps {
  /** How long it took, once it has ended. */
  durationSeconds: number | null | undefined;
  /** When it started. With no duration and no end yet, the time since is how long it has been running, counted live. */
  startUtc?: string | null;
  /** When it ended. */
  endUtc?: string | null;
  /** When it was asked for, so the hover can say how long it waited before it started. */
  enqueuedUtc?: string | null;
  /** Whether it is still executing, for a caller whose rows say so themselves. Left out, a start with no end and no
   * duration reads as running; a row that stopped without recording its end (a failed batch, say) passes false, so it
   * shows a dash rather than counting up for ever. */
  live?: boolean;
  testId?: string;
}

function clock(utc: string): string {
  return format(parseUtc(utc), "MMM d, HH:mm:ss");
}

/**
 * How long a run (or a set of runs) took, or while it executes how long it has run so far, counting up every second.
 * The hover says when it started and ended and how long it waited in the queue before it started, so the one cell
 * answers "how long did this take, and was the wait or the work the long part". A dash when it never started.
 */
export function RunDuration({ durationSeconds, startUtc, endUtc, enqueuedUtc, live, testId }: RunDurationProps) {
  const running = !!startUtc && !endUtc && (live ?? durationSeconds == null);
  const now = useNow(running);

  const seconds = durationSeconds != null
    ? durationSeconds
    : startUtc && endUtc
      ? secondsSince(startUtc, parseUtc(endUtc).getTime())
      : running && startUtc
        ? secondsSince(startUtc, now)
        : null;
  if (seconds === null) {
    return <span className="text-muted-foreground" data-testid={testId}>-</span>;
  }

  const waited = waitedSeconds(enqueuedUtc, startUtc);
  const lines = [
    running ? "Still running." : null,
    startUtc ? `Started ${clock(startUtc)}` : null,
    endUtc ? `Ended ${clock(endUtc)}` : null,
    waited !== null ? `Waited ${formatDurationSeconds(waited)} in the queue before it started` : null,
  ].filter((line): line is string => line !== null);

  const face = (
    <span
      className={cn("font-mono tabular-nums", running && "text-info")}
      data-testid={testId}
      data-running={running ? "true" : undefined}
    >
      {formatDurationSeconds(seconds)}
    </span>
  );

  if (lines.length === 0) {
    return face;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>{face}</TooltipTrigger>
      <TooltipContent className="flex flex-col gap-0.5 font-mono text-[11px]">
        {lines.map((line) => <span key={line}>{line}</span>)}
      </TooltipContent>
    </Tooltip>
  );
}
