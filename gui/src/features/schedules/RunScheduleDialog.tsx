import { useCallback, useMemo, useState } from "react";
import type React from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  Ban,
  ChevronDown,
  CircleCheck,
  CircleMinus,
  CircleX,
  Clock3,
  ExternalLink,
  Hourglass,
  Info,
  Link2,
  Loader2,
  Play,
  SkipForward,
  TriangleAlert,
  Zap,
} from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge, badgeVariants } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { runApi, scheduleApi } from "../../api/endpoints";
import type { RunStatus, RunSummary, Schedule, SchedulePlanMember } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DateRangeCalendar } from "../../components/DateRangeCalendar";
import { RelativeTime } from "../../components/RelativeTime";
import { seriesColor } from "../../theme/branding";
import { useRunDock } from "../runs/RunDockContext";
import { useRunGroupStream } from "../runs/useRunGroupStream";

/** The status a plan member shows on the board: the live run status once a fire is underway, or "pending" before
 * Start (and for the brief window before the group's connect snapshot lands). */
type MemberStatus = RunStatus | "pending";

const TERMINAL: ReadonlySet<RunStatus> = new Set<RunStatus>(["succeeded", "failed", "cancelled", "skipped"]);

/** One wave of the plan: its members, grouped and ordered so the board renders them concurrently under one rail. */
interface PlanWave {
  wave: number;
  members: SchedulePlanMember[];
}

function describeTrigger(cron: string | null, intervalSeconds: number | null): string {
  if (cron !== null && cron.trim() !== "") {
    return `cron ${cron}`;
  }

  if (intervalSeconds !== null) {
    return `every ${intervalSeconds}s`;
  }

  return "manual";
}

/** A translucent wash of a series color for the wave rails' fills, so the accent stays a token-derived value. */
function tint(color: string, percent: number): string {
  return `color-mix(in srgb, ${color} ${percent}%, transparent)`;
}

/** Groups the plan's flat, wave-ordered member list into the waves the board draws, each sorted by flow name so a
 * member keeps its row across live updates. */
function toWaves(members: SchedulePlanMember[]): PlanWave[] {
  const byWave = new Map<number, SchedulePlanMember[]>();
  for (const member of members) {
    const existing = byWave.get(member.wave);
    if (existing) {
      existing.push(member);
    } else {
      byWave.set(member.wave, [member]);
    }
  }

  return [...byWave.entries()]
    .sort(([a], [b]) => a - b)
    .map(([wave, waveMembers]) => ({
      wave,
      members: [...waveMembers].sort((a, b) => (a.flowName < b.flowName ? -1 : a.flowName > b.flowName ? 1 : 0)),
    }));
}

/** The compact status glyph + label for one member row: a synthetic "pending" before Start, then the live run
 * status once the group is streaming. Kept local (not RunStatusBadge) so "pending" has a first-class rest state.
 * Tones follow DESIGN.md 3.2; the icon means color never carries the state alone. */
function MemberStatusChip({ status }: { status: MemberStatus }) {
  const spec: Record<MemberStatus, { label: string; className: string; icon: React.ReactNode }> = {
    pending: { label: "Pending", className: "text-muted-foreground", icon: <Clock3 className="size-3.5" /> },
    queued: { label: "Queued", className: "text-warning", icon: <Hourglass className="size-3.5" /> },
    running: { label: "Running", className: "text-info", icon: <Loader2 className="size-3.5 animate-spin" /> },
    succeeded: { label: "Succeeded", className: "text-success", icon: <CircleCheck className="size-3.5" /> },
    failed: { label: "Failed", className: "text-destructive", icon: <CircleX className="size-3.5" /> },
    cancelled: { label: "Cancelled", className: "text-muted-foreground", icon: <CircleMinus className="size-3.5" /> },
    skipped: { label: "Skipped", className: "text-muted-foreground", icon: <SkipForward className="size-3.5" /> },
  };
  const { label, className, icon } = spec[status];
  return (
    <span className={cn("inline-flex min-w-[108px] items-center justify-end gap-1.5 text-xs font-medium", className)}>
      {icon}
      {label}
    </span>
  );
}

export interface RunScheduleDialogProps {
  schedule: Schedule;
  onClose: () => void;
}

/**
 * The pre-flight run board for a schedule, as a right-side sheet (DESIGN.md 7.4; the old dialog's testid stays
 * on the sheet content). It reads the same wave-ordered plan a fire enqueues (GET /schedules/{id}/plan), lays the
 * flows out as the batches they run in (a wave is a row of flows that run concurrently; the next wave starts only
 * once the previous is terminal), and gates execution behind Start.
 *
 * On Start it fires the schedule through the existing run-now path (the cadence is untouched). A multi-flow fire
 * becomes a run group, and the board then streams that group live in place: every flow transitions Pending to
 * Queued to Running to its terminal status as the group executes, with a progress rail and a link to the full run
 * group view. A single-flow schedule has no group, so Start hands off straight to that run's detail page.
 */
export function RunScheduleDialog({ schedule, onClose }: RunScheduleDialogProps) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { track } = useRunDock();

  const [groupId, setGroupId] = useState<string | null>(null);
  const [ended, setEnded] = useState(false);
  const [confirmCancelOpen, setConfirmCancelOpen] = useState(false);
  // An empty set means "all batches"; any members carrying a selected batch tag are what the fire (and this board)
  // narrows to. Multiple batches can be selected to run several at once.
  const [selectedBatches, setSelectedBatches] = useState<string[]>([]);
  // An optional backfill window (datetime-local strings). When set, the fire re-processes the source for that range:
  // its integration roots re-land the slice, its silver (relational ingestion) flows re-pull from the source minimum.
  const [backfillFrom, setBackfillFrom] = useState("");
  const [backfillTo, setBackfillTo] = useState("");
  // Whether this manual fire carries the schedules chained behind it. Defaults to true so the manual path matches
  // what the clock does; an operator re-running one region alone turns it off.
  const [runChain, setRunChain] = useState(true);
  const phase: "preview" | "running" = groupId === null ? "preview" : "running";

  const plan = useQuery({
    queryKey: ["schedule-plan", schedule.id],
    queryFn: () => scheduleApi.plan(schedule.id),
  });

  const onEnded = useCallback(() => setEnded(true), []);
  const { members: liveMembers, connected } = useRunGroupStream(groupId ?? "", groupId !== null && !ended, onEnded);
  const liveByFlow = useMemo(() => {
    const map = new Map<string, RunSummary>();
    for (const member of liveMembers) {
      map.set(member.flowName, member);
    }

    return map;
  }, [liveMembers]);

  const trimmedFrom = backfillFrom.trim();
  const trimmedTo = backfillTo.trim();
  // An end date needs a start date, and the range must be ordered; the server validates authoritatively.
  const windowError = trimmedTo !== "" && trimmedFrom === ""
    ? "An end date needs a start date."
    : trimmedFrom !== "" && trimmedTo !== "" && trimmedTo <= trimmedFrom
      ? "The end date must be after the start date."
      : null;

  const run = useMutation({
    mutationFn: () => scheduleApi.runNow(
      schedule.id,
      selectedBatches,
      trimmedFrom === "" ? null : `${trimmedFrom}:00Z`,
      trimmedTo === "" ? null : `${trimmedTo}:00Z`,
      runChain,
    ),
    onSuccess: (accepted) => {
      void queryClient.invalidateQueries({ queryKey: ["schedules"] });
      if (accepted.groupId !== null) {
        // A multi-flow fire streams live in this board; keep the sheet open and switch to the running phase. Also
        // hand the group to the run tray, so closing the sheet or switching tabs never strands the running fire.
        track(accepted.groupId);
        setGroupId(accepted.groupId);
        return;
      }

      // A single-flow schedule enqueues one run with no group to stream, so hand off to that run's detail page.
      toast.success(`Run started for schedule "${schedule.name}"`);
      onClose();
      navigate(`/runs/${accepted.runId}`);
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  // Cancelling the running group: the same unit-cancel the full run group view uses (dequeues the queued members and
  // aborts any running member's in-flight statement). The board keeps streaming, so the rows settle to cancelled live.
  const cancel = useMutation({
    mutationFn: () => runApi.cancelGroup(groupId!),
    onSuccess: () => {
      toast.success("Cancel requested for this run.");
      setConfirmCancelOpen(false);
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
      setConfirmCancelOpen(false);
    },
  });

  const allMembers = useMemo(() => plan.data?.members ?? [], [plan.data]);
  // The distinct batches a fire could span. With more than one, the board offers a filter so an operator can run
  // just one batch's flows ("the nightly, but only the small tables").
  const batches = useMemo(
    () => [...new Set(allMembers.map((m) => m.batch))].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)),
    [allMembers],
  );
  // What the board shows and a Start actually fires: every member, or only those in the selected batches.
  const visibleMembers = useMemo(
    () => (selectedBatches.length === 0 ? allMembers : allMembers.filter((m) => selectedBatches.includes(m.batch))),
    [allMembers, selectedBatches],
  );
  const waves = useMemo(() => toWaves(visibleMembers), [visibleMembers]);
  const memberCount = visibleMembers.length;

  // The chain this fire sets off, in order. Sorted by depth so the list reads as the sequence it will run in
  // rather than the order the API happened to walk it.
  const chainLinks = useMemo(
    () => [...(schedule.triggersSchedules ?? [])].sort((a, b) => a.depth - b.depth),
    [schedule.triggersSchedules],
  );
  const chainFlowCount = useMemo(
    () => chainLinks.reduce((sum, link) => sum + link.memberCount, 0),
    [chainLinks],
  );
  // The first stopped link ends the chain: everything after it is unreachable this fire, so naming it up front
  // saves an operator wondering later why the tail never ran.
  const chainStopsAt = useMemo(
    () => chainLinks.find((link) => !link.enabled || link.paused)?.name ?? null,
    [chainLinks],
  );

  const statusOf = (flowName: string): MemberStatus => {
    if (phase !== "running") {
      return "pending";
    }

    return liveByFlow.get(flowName)?.status ?? "pending";
  };

  const doneCount = phase === "running"
    ? visibleMembers.filter((m) => {
      const s = liveByFlow.get(m.flowName)?.status;
      return s !== undefined && TERMINAL.has(s);
    }).length
    : 0;
  const failedCount = phase === "running"
    ? visibleMembers.filter((m) => liveByFlow.get(m.flowName)?.status === "failed").length
    : 0;
  const progress = memberCount > 0 ? Math.round((doneCount / memberCount) * 100) : 0;

  const nothingToRun = plan.isSuccess && memberCount === 0;
  const canStart = plan.isSuccess && memberCount > 0 && !run.isPending && phase === "preview"
    && windowError === null;
  const closeDisabled = run.isPending;

  const viewFullRun = () => {
    if (groupId !== null) {
      onClose();
      navigate(`/runs/groups/${groupId}`);
    }
  };

  // Jump from a member row straight to that flow's run detail, closing the board on the way out.
  const openMemberRun = (runId: string) => {
    onClose();
    navigate(`/runs/${runId}`);
  };

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !closeDisabled) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full gap-0 sm:max-w-xl" data-testid="run-schedule-dialog">
        <SheetHeader className="border-b">
          <div className="flex items-center gap-3 pr-8">
            <div className="flex size-10 shrink-0 items-center justify-center rounded-md bg-primary/12 text-primary">
              <Zap className="size-5" />
            </div>
            <div className="min-w-0 grow">
              <SheetTitle>Run schedule</SheetTitle>
              <SheetDescription className="truncate font-mono text-[12px]" data-testid="run-schedule-name">
                {schedule.name}
              </SheetDescription>
            </div>
          </div>
        </SheetHeader>

        <div className="flex flex-1 flex-col gap-4 overflow-y-auto p-4">
          {plan.isError && (
            isApiError(plan.error)
              ? <CorrelationError error={plan.error} />
              : <p className="text-[13px] text-destructive">{String(plan.error)}</p>
          )}

          {plan.isLoading && (
            <div className="flex flex-col gap-3" data-testid="run-schedule-loading">
              <Skeleton className="h-16 w-full rounded-lg" />
              <Skeleton className="h-28 w-full rounded-lg" />
              <Skeleton className="h-28 w-full rounded-lg" />
            </div>
          )}

          {plan.isSuccess && (
            <>
              {run.isError && isApiError(run.error) && <CorrelationError error={run.error} />}

              {/* Summary rail: what one fire runs and on what cadence. */}
              <div className="rounded-md border bg-muted/50 p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-[13px] font-medium" data-testid="run-schedule-summary">
                    {memberCount} {memberCount === 1 ? "flow" : "flows"}
                  </span>
                  <span className="text-[13px] text-muted-foreground">·</span>
                  <span className="text-[13px] font-medium">
                    {waves.length} {waves.length === 1 ? "wave" : "waves"}
                  </span>
                  <span className="grow" />
                  <Badge variant="outline" className="font-mono" data-testid="run-schedule-cadence">
                    {describeTrigger(plan.data.cron, plan.data.intervalSeconds)}
                  </Badge>
                  {!schedule.enabled && <Badge variant="outline" className="text-muted-foreground">disabled</Badge>}
                  {schedule.paused && (
                    <Badge className="border-transparent bg-warning/15 text-warning">paused</Badge>
                  )}
                </div>
                <p className="mt-1.5 text-xs text-muted-foreground">
                  {phase === "running"
                    ? `${doneCount} of ${memberCount} done${failedCount > 0 ? `, ${failedCount} failed` : ""}.`
                    : (
                      <>
                        Next scheduled fire <RelativeTime value={plan.data.nextFireUtc} />. Starting now runs it on
                        demand and does not move the schedule.
                      </>
                    )}
                </p>
              </div>

              {/* This schedule is the head of a chain, so starting it does not stop at its own members: each link
                  fires when the one before it finishes. Showing only the 17 flows in this fire would understate a
                  five-link chain by four fifths, which is the difference between "run this region" and "run the
                  whole source". A stopped link is called out rather than hidden: the chain ends there. */}
              {chainLinks.length > 0 && (
                <div className="rounded-md border bg-muted/50 p-3" data-testid="run-schedule-chain">
                  <div className="flex flex-wrap items-center gap-2">
                    <Link2 className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
                    <span className="text-[13px] font-medium">
                      {runChain
                        ? `Then triggers ${chainLinks.length} ${chainLinks.length === 1 ? "schedule" : "schedules"}`
                        : "Chained schedules held back"}
                    </span>
                    <span className="text-[13px] text-muted-foreground">·</span>
                    <span className="text-[13px] text-muted-foreground">
                      {chainFlowCount} more {chainFlowCount === 1 ? "flow" : "flows"}
                    </span>
                    <span className="grow" />
                    {phase === "preview" && (
                      <div className="flex items-center gap-2">
                        <Switch
                          id="run-schedule-chain-toggle"
                          checked={runChain}
                          onCheckedChange={setRunChain}
                          disabled={run.isPending}
                          data-testid="run-schedule-chain-toggle"
                        />
                        <Label htmlFor="run-schedule-chain-toggle" className="text-xs font-normal">
                          Run the chain
                        </Label>
                      </div>
                    )}
                  </div>
                  <ol className={cn("mt-2 space-y-1", !runChain && "opacity-50")}>
                    {chainLinks.map((link) => {
                      const stopped = !link.enabled || link.paused;
                      return (
                        <li key={link.id} className="flex flex-wrap items-center gap-1.5 text-xs">
                          <span className="font-mono text-muted-foreground">
                            {"→".repeat(link.depth)}
                          </span>
                          <span className={cn("font-mono", !runChain && "line-through")}>{link.name}</span>
                          <span className="text-muted-foreground">
                            {link.memberCount} {link.memberCount === 1 ? "flow" : "flows"}
                          </span>
                          {runChain && stopped && (
                            <Badge variant="outline" className="text-muted-foreground">
                              {link.paused ? "paused" : "disabled"}
                            </Badge>
                          )}
                        </li>
                      );
                    })}
                  </ol>
                  <p className="mt-2 text-xs text-muted-foreground">
                    {runChain
                      ? (
                        <>
                          Each runs as its own wave-ordered set, starting only once the one before it has finished.
                          {chainStopsAt !== null && (
                            <> The chain stops at <span className="font-mono">{chainStopsAt}</span>; links after it will not run.</>
                          )}
                        </>
                      )
                      : (
                        <>
                          Only this schedule&apos;s {memberCount} {memberCount === 1 ? "flow" : "flows"} will run. The
                          links behind it stay put, and their own cadence is untouched.
                        </>
                      )}
                  </p>
                </div>
              )}

              {/* A prior fire of this schedule is still executing (the last group has queued/running members). Rather
                  than fire a second overlapping run, point the operator at the live board for the run already going. */}
              {phase === "preview" && schedule.lastGroupActive && schedule.lastGroupId !== null && (
                <Alert data-testid="run-schedule-already-running">
                  <Loader2 className="animate-spin text-info" />
                  <AlertDescription className="flex flex-wrap items-center gap-x-2 gap-y-1">
                    <span>A fire of this schedule is still running.</span>
                    <Button
                      variant="link"
                      size="sm"
                      className="h-auto p-0 text-info"
                      onClick={() => {
                        onClose();
                        navigate(`/runs/groups/${schedule.lastGroupId}`);
                      }}
                      data-testid="run-schedule-view-running"
                    >
                      View the running set
                      <ExternalLink className="size-3.5" />
                    </Button>
                  </AlertDescription>
                </Alert>
              )}

              {(!schedule.enabled || schedule.paused) && phase === "preview" && (
                <Alert data-testid="run-schedule-inactive-note">
                  <Info />
                  <AlertDescription>
                    This schedule is {schedule.paused ? "paused" : "disabled"}, so it will not fire on its own.
                    Starting here runs its flows once, immediately, without changing that.
                  </AlertDescription>
                </Alert>
              )}

              {batches.length > 1 && (
                <div data-testid="run-schedule-batch-filter">
                  <div className="mb-1.5 flex items-baseline gap-2">
                    <span className="text-xs text-muted-foreground">Batches</span>
                    <span className="text-xs text-muted-foreground/70">
                      {selectedBatches.length === 0 ? "all" : `${selectedBatches.length} selected`}
                    </span>
                  </div>
                  <div className="flex flex-wrap gap-1.5">
                    <button
                      type="button"
                      onClick={() => setSelectedBatches([])}
                      disabled={phase === "running"}
                      className={cn(
                        badgeVariants({ variant: selectedBatches.length === 0 ? "default" : "outline" }),
                        "cursor-pointer disabled:cursor-default disabled:opacity-50",
                      )}
                      data-testid="run-schedule-batch-all"
                    >
                      All batches
                    </button>
                    {batches.map((b) => {
                      const on = selectedBatches.includes(b);
                      return (
                        <button
                          key={b}
                          type="button"
                          onClick={() =>
                            setSelectedBatches((prev) =>
                              prev.includes(b) ? prev.filter((x) => x !== b) : [...prev, b])}
                          disabled={phase === "running"}
                          className={cn(
                            badgeVariants({ variant: on ? "default" : "outline" }),
                            "cursor-pointer font-mono disabled:cursor-default",
                            phase === "running" && !on && "opacity-50",
                          )}
                          data-testid={`run-schedule-batch-${b}`}
                        >
                          {b}
                        </button>
                      );
                    })}
                  </div>
                </div>
              )}

              {phase === "preview" && (
                <div className="rounded-md border bg-muted/40 p-3" data-testid="run-schedule-backfill">
                  <div className="text-[13px] font-medium">Backfill window (optional)</div>
                  <p className="mt-1 text-xs text-muted-foreground">
                    Reprocess this source for a date range: the integration flows re-land the files modified in the
                    window and the silver flows re-pull from the source minimum. Leave empty to run normally.
                  </p>
                  <div className="mt-2">
                    <DateRangeCalendar
                      from={backfillFrom}
                      to={backfillTo}
                      onChange={(from, to) => {
                        setBackfillFrom(from);
                        setBackfillTo(to);
                      }}
                      testId="run-schedule-backfill"
                    />
                  </div>
                  {windowError !== null && (
                    <p className="mt-1.5 text-xs font-medium text-destructive" data-testid="run-schedule-backfill-error">
                      {windowError}
                    </p>
                  )}
                </div>
              )}

              {phase === "running" && (
                <div>
                  <Progress
                    value={progress}
                    className={cn(
                      "h-1.5",
                      failedCount > 0
                        ? "[&_[data-slot=progress-indicator]]:bg-destructive"
                        : ended && "[&_[data-slot=progress-indicator]]:bg-success",
                    )}
                    data-testid="run-schedule-progress"
                  />
                  <div className="mt-1.5 flex items-center gap-2">
                    <Badge
                      variant="outline"
                      className={cn(
                        !ended && (connected ? "text-success" : "text-warning"),
                        ended && "text-muted-foreground",
                      )}
                      data-testid="run-schedule-stream-state"
                    >
                      {ended ? "finished" : connected ? "live" : "reconnecting"}
                    </Badge>
                    <span className="text-xs text-muted-foreground">
                      {ended
                        ? failedCount > 0
                          ? `Finished with ${failedCount} failed.`
                          : "All flows finished."
                        : "Executing in dependency order."}
                    </span>
                  </div>
                </div>
              )}

              {nothingToRun ? (
                <Alert data-testid="run-schedule-empty">
                  <TriangleAlert className="text-warning" />
                  <AlertDescription>
                    <p>
                      No runnable flow joins this schedule right now. A flow joins with{" "}
                      <code className="font-mono text-xs">schedule: {schedule.name}</code>, and must be active and
                      not <code className="font-mono text-xs">mode: manual</code>.
                    </p>
                  </AlertDescription>
                </Alert>
              ) : (
                <div className="flex flex-col gap-2">
                  {waves.map((planWave, index) => {
                    // The wave rails wear the chart series palette in fixed slot order (DESIGN.md 3.4): the rail
                    // is categorical (which wave), never a status.
                    const accent = seriesColor(index);
                    const running = phase === "running";
                    const waveDone = running
                      && planWave.members.every((m) => {
                        const s = liveByFlow.get(m.flowName)?.status;
                        return s !== undefined && TERMINAL.has(s);
                      });
                    const waveActive = running
                      && !waveDone
                      && planWave.members.some((m) => {
                        const s = liveByFlow.get(m.flowName)?.status;
                        return s === "running" || s === "queued";
                      });
                    return (
                      <div key={planWave.wave}>
                        {index > 0 && (
                          <div className="my-0.5 flex justify-center text-muted-foreground">
                            <ChevronDown className="size-4" />
                          </div>
                        )}
                        <div
                          className="overflow-hidden rounded-md border"
                          style={{
                            borderLeft: `3px solid ${accent}`,
                            backgroundColor: tint(accent, waveActive ? 8 : 3),
                          }}
                        >
                          <div
                            className="flex items-center gap-2 px-3 py-2"
                            style={{ backgroundColor: tint(accent, 6) }}
                          >
                            <span className="text-[11px] font-medium uppercase tracking-wider">
                              Wave {index + 1}
                            </span>
                            <span className="text-xs text-muted-foreground">
                              {planWave.members.length} {planWave.members.length === 1 ? "flow" : "flows"} · concurrent
                            </span>
                            <span className="grow" />
                            <span className="text-xs text-muted-foreground">
                              {index === 0 ? "runs first" : "after previous"}
                            </span>
                            {waveDone && <CircleCheck className="size-4 text-success" />}
                          </div>
                          <div className="divide-y">
                            {planWave.members.map((member) => {
                              const live = liveByFlow.get(member.flowName);
                              // A member becomes a link once its fire has produced a run; before Start (and for skipped
                              // flows that never ran) there is nothing to open, so the row stays static.
                              const runId = live?.runId ?? null;
                              const openRun = runId === null ? undefined : () => openMemberRun(runId);
                              return (
                                <div
                                  key={member.flowName}
                                  onClick={openRun}
                                  className={cn(
                                    "flex items-center gap-2 px-3 py-2",
                                    openRun !== undefined && "cursor-pointer hover:bg-accent/50",
                                  )}
                                  data-testid="run-schedule-member"
                                >
                                  <div className="min-w-0 grow">
                                    <div className="truncate font-mono text-[12px] font-medium">
                                      {member.flowName}
                                    </div>
                                    <div className="truncate text-xs text-muted-foreground">
                                      {member.flowKind}
                                      {running && live?.lastAction ? ` · ${live.lastAction}` : ""}
                                    </div>
                                  </div>
                                  <MemberStatusChip status={statusOf(member.flowName)} />
                                  {openRun !== undefined && (
                                    <ExternalLink className="size-3.5 shrink-0 text-muted-foreground" />
                                  )}
                                </div>
                              );
                            })}
                          </div>
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </>
          )}
        </div>

        <SheetFooter className="flex-row items-center gap-2 border-t">
          {phase === "preview" ? (
            <>
              <span className="mr-auto text-xs text-muted-foreground">
                {nothingToRun ? "Nothing to run." : "Review the waves, then start."}
              </span>
              <Button variant="ghost" size="sm" onClick={onClose} disabled={closeDisabled}>
                Cancel
              </Button>
              <Button
                size="sm"
                onClick={() => run.mutate()}
                disabled={!canStart}
                data-testid="run-schedule-start"
              >
                {run.isPending ? <Loader2 className="animate-spin" /> : <Play />}
                Start
              </Button>
            </>
          ) : (
            <>
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={viewFullRun}
                    className="mr-auto"
                    data-testid="run-schedule-view-group"
                  >
                    <ExternalLink />
                    View full run
                  </Button>
                </TooltipTrigger>
                <TooltipContent>Open the full run group view</TooltipContent>
              </Tooltip>
              {!ended && (
                <Button
                  variant="outline"
                  size="sm"
                  className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
                  onClick={() => setConfirmCancelOpen(true)}
                  disabled={cancel.isPending}
                  data-testid="run-schedule-cancel-run"
                >
                  {cancel.isPending ? <Loader2 className="animate-spin" /> : <Ban />}
                  Cancel run
                </Button>
              )}
              <Button
                variant={ended ? "default" : "outline"}
                size="sm"
                onClick={onClose}
                data-testid="run-schedule-close"
              >
                {ended ? "Done" : "Close"}
              </Button>
            </>
          )}
        </SheetFooter>
      </SheetContent>

      <ConfirmDialog
        open={confirmCancelOpen}
        title="Cancel this run"
        message="Cancel the running fire? Queued flows are dequeued and any running flow aborts its in-flight statement. Flows that already finished are unaffected."
        confirmLabel="Cancel run"
        danger
        busy={cancel.isPending}
        onConfirm={() => cancel.mutate()}
        onClose={() => setConfirmCancelOpen(false)}
      />
    </Sheet>
  );
}
