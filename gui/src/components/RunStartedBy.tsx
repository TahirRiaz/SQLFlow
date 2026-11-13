import type { LucideIcon } from "lucide-react";
import { CalendarClock, CircleHelp, CornerLeftUp, Link2, SquareTerminal, UserRound, Webhook } from "lucide-react";
import { Link } from "react-router-dom";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";
import type { RunCause, RunStartedBy, RunStartedByKind } from "../api/types";
import { startedByFace } from "../features/runs/startedBy";
import { IdChip } from "./IdChip";
import { RichTooltip } from "./RichTooltip";
import { RunStatusBadge } from "./StatusBadge";

/** The glyph for each way a run starts: the schedules' clock, a chain's link, a person, a webhook, a terminal. */
const icons: Record<RunStartedByKind, LucideIcon> = {
  schedule: CalendarClock,
  chain: Link2,
  user: UserRound,
  trigger: Webhook,
  cli: SquareTerminal,
  unknown: CircleHelp,
};

/**
 * What started a run, as a row of a run list shows it: the glyph for how, then who or what in a few words (the schedule,
 * the person, the trigger, the account) and a quiet qualifier. The control plane's whole sentence is the hover. A row
 * whose list does not carry it (a group's live member stream) shows a dash.
 */
export function RunStartedByCell({ startedBy }: { startedBy: RunStartedBy | null }) {
  if (startedBy === null) {
    return <span className="text-muted-foreground">-</span>;
  }

  const face = startedByFace(startedBy);
  const Icon = icons[startedBy.kind];
  return (
    <RichTooltip title="Started by" body={startedBy.summary}>
      <span
        className="inline-flex min-w-0 max-w-64 items-center gap-1.5 text-[13px]"
        data-testid="run-started-by"
        data-kind={startedBy.kind}
      >
        <Icon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
        <span className={cn("truncate", startedBy.kind === "unknown" && "text-muted-foreground")}>{face.primary}</span>
        {face.secondary !== null && <span className="shrink-0 text-muted-foreground">{face.secondary}</span>}
      </span>
    </RichTooltip>
  );
}

/**
 * What started a run or a run group, as its page says it under the title: the glyph and the control plane's sentence,
 * then a link to each thing it names (the schedule's runs, the parent fire it followed, the external trigger's request).
 * A page that lists the chain of fires behind the run (`RunCauseChain`) links the parent fire there, and passes
 * `linkAfterRun={false}` so the header does not repeat it.
 */
export function RunStartedByLine({ startedBy, linkAfterRun = true }: { startedBy: RunStartedBy; linkAfterRun?: boolean }) {
  const Icon = icons[startedBy.kind];
  return (
    <span className="inline-flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1" data-testid="run-started-by-line" data-kind={startedBy.kind}>
      <span className="inline-flex min-w-0 items-center gap-1.5 text-foreground">
        <Icon className="size-4 shrink-0 text-muted-foreground" aria-hidden />
        <span className="font-medium" data-testid="run-started-by-summary">{startedBy.summary}</span>
      </span>
      {startedBy.scheduleId !== null && (
        <IdChip
          label="schedule"
          value={startedBy.scheduleId}
          display={startedBy.scheduleName ?? undefined}
          to={`/runs?scheduleId=${startedBy.scheduleId}`}
          testId="started-by-schedule"
          copyTestId="copy-started-by-schedule"
        />
      )}
      {linkAfterRun && startedBy.afterRunId !== null && (
        <IdChip
          label="after"
          value={startedBy.afterRunId}
          display={startedBy.afterFlowName ?? undefined}
          to={`/runs/${startedBy.afterRunId}`}
          testId="started-by-after-run"
          copyTestId="copy-started-by-after-run"
        />
      )}
      {startedBy.triggerRequestId !== null && (
        <IdChip
          label="request"
          value={startedBy.triggerRequestId}
          to={`/external-triggers?request=${startedBy.triggerRequestId}`}
          testId="started-by-request"
          copyTestId="copy-started-by-request"
        />
      )}
    </span>
  );
}

/**
 * The chain of fires behind a run a chained schedule started, from the parent fire it followed back to the first link,
 * whose line says who or which clock set the whole chain off. Each link names its fire's first run, its state and what
 * started it, and opens that run. Nothing is drawn for a run no parent fire set off.
 */
export function RunCauseChain({ causes }: { causes: RunCause[] }) {
  if (causes.length === 0) {
    return null;
  }

  return (
    <Card className="gap-1.5 rounded-lg px-4 py-2.5" data-testid="run-cause-chain">
      <h2 className="text-[13px] font-medium">
        {causes.length === 1 ? "Fired after" : "Fired after, back to the first link"}
      </h2>
      <ol className="flex flex-col gap-1">
        {causes.map((cause) => {
          const Icon = icons[cause.startedBy.kind];
          return (
            <li key={cause.runId} className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 text-[13px]" data-testid="run-cause">
              <CornerLeftUp className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
              <Link to={`/runs/${cause.runId}`} className="font-mono text-[12px] text-primary hover:underline" data-testid="run-cause-link">
                {cause.flowName}
              </Link>
              <RunStatusBadge status={cause.status} />
              <span className="inline-flex min-w-0 items-center gap-1.5 text-muted-foreground">
                <Icon className="size-3.5 shrink-0" aria-hidden />
                <span className="break-words">{cause.startedBy.summary}</span>
              </span>
            </li>
          );
        })}
      </ol>
    </Card>
  );
}
