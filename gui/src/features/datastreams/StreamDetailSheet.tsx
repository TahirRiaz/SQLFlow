import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import {
  Bar,
  CartesianGrid,
  Cell,
  Line,
  ComposedChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  type TooltipProps,
} from "recharts";
import { CircleCheck, CircleSlash, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { dataStreamApi } from "../../api/endpoints";
import type { StreamPoint, StreamSignal } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { LineageJumpButton } from "../../components/LineageJumpButton";
import { RelativeTime } from "../../components/RelativeTime";
import { useChartInk, detectorLabels, detectorMethods, evidence, finding, formatRows, formatDays } from "./streamPresentation";
import { StreamStatusBadge } from "./StreamStatusBadge";

/** The chart's hover readout: what arrived, what was expected, and (when flagged) why. */
function PointTooltip({ active, payload }: TooltipProps<number, string>) {
  if (active !== true || payload === undefined || payload.length === 0) {
    return null;
  }

  const point = payload[0].payload as StreamPoint;
  return (
    <div className="rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs text-popover-foreground shadow-md">
      <div className="font-mono font-medium">{point.date.slice(0, 10)}</div>
      <div className="mt-1 flex flex-col gap-0.5 font-mono tabular-nums">
        <span>{formatRows(point.rowsWritten)} written</span>
        <span className="text-muted-foreground">{formatRows(point.expected)} expected</span>
        {point.rowsWritten > 0 && (
          <span className="text-muted-foreground">
            {point.rowsInserted.toLocaleString()} ins / {point.rowsUpdated.toLocaleString()} upd /{" "}
            {point.rowsDeleted.toLocaleString()} del
          </span>
        )}
        <span className="text-muted-foreground">
          {point.runs} run(s){point.failures > 0 ? `, ${point.failures} failed` : ""}
        </span>
        {point.excludedBackfillRuns > 0 && (
          <span className="text-muted-foreground">{point.excludedBackfillRuns} backfill run(s) excluded</span>
        )}
      </div>
      {point.anomaly && (
        <div className="mt-1 border-t border-border/60 pt-1 text-destructive">
          {point.reason} ({point.severity.toFixed(1)} sigma)
        </div>
      )}
      {point.immature && <div className="mt-1 text-[11px] text-muted-foreground">Still arriving; not flagged.</div>}
    </div>
  );
}

/** One detector's row: whether it fired, its confidence, and the sentence it measured. */
function SignalRow({ signal }: { signal: StreamSignal }) {
  return (
    <div className="flex items-start gap-2.5 border-b border-border/60 py-2 last:border-b-0">
      {signal.fired ? (
        <CircleSlash className="mt-0.5 size-3.5 shrink-0 text-destructive" aria-hidden />
      ) : (
        <CircleCheck className="mt-0.5 size-3.5 shrink-0 text-success" aria-hidden />
      )}
      <div className="flex min-w-0 flex-col gap-0.5">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[13px] font-medium">{detectorLabels[signal.detector]}</span>
          {signal.primary && (
            <span className="rounded-sm bg-muted px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-muted-foreground">
              primary
            </span>
          )}
          <span className="text-[11px] text-muted-foreground">
            {signal.fired ? `fired, confidence ${(signal.score * 100).toFixed(0)}%` : "quiet"}
          </span>
        </div>
        <p className="text-[13px] leading-5 text-muted-foreground">{signal.detail}</p>
        <p className="text-[11px] leading-4 text-muted-foreground/70">{detectorMethods[signal.detector]}</p>
      </div>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{label}</span>
      <span className="font-mono text-[13px] tabular-nums">{value}</span>
    </div>
  );
}

interface StreamDetailSheetProps {
  pipelineId: string | null;
  flowName: string | null;
  windowDays: number;
  includeBackfills: boolean;
  onClose: () => void;
}

/**
 * One stream in full: the daily volume against what the detector expected, and every detector's reasoning
 * (including the ones that stayed quiet, so a verdict can be checked rather than taken on faith).
 *
 * The chart is one measure on one axis: bars are the rows that actually arrived, the dashed line is the
 * trend-and-weekday expectation drawn in recessive ink because it is a reference rather than a second
 * series, and a flagged day wears the reserved status color with its reason spelled out in the tooltip, so
 * identity never rests on color alone (DESIGN.md 3.4).
 */
export function StreamDetailSheet({
  pipelineId, flowName, windowDays, includeBackfills, onClose,
}: StreamDetailSheetProps) {
  const navigate = useNavigate();
  const ink = useChartInk();
  const query = useQuery({
    queryKey: ["datastreams", "detail", pipelineId, windowDays, includeBackfills],
    queryFn: () => dataStreamApi.get(pipelineId!, windowDays, includeBackfills),
    enabled: pipelineId !== null,
  });

  const stream = query.data;

  return (
    <Sheet open={pipelineId !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent side="right" className="w-full gap-0 sm:max-w-2xl">
        <SheetHeader>
          <SheetTitle className="font-mono text-sm">{flowName ?? "Stream"}</SheetTitle>
          <SheetDescription>
            {stream === undefined
              ? `Last ${windowDays} days of write activity`
              : `${stream.targetObject ?? "no known target"} - last ${windowDays} days`}
          </SheetDescription>
        </SheetHeader>

        <div className="flex flex-col gap-4 overflow-y-auto px-4 pb-6">
          {query.isError && (isApiError(query.error)
            ? <CorrelationError error={query.error} />
            : <p className="text-[13px] text-destructive">{String(query.error)}</p>)}

          {query.isPending && pipelineId !== null && (
            <>
              <Skeleton className="h-8 rounded-md" />
              <Skeleton className="h-56 rounded-lg" />
              <Skeleton className="h-40 rounded-lg" />
            </>
          )}

          {stream !== undefined && (
            <>
              <div className="flex flex-wrap items-center gap-2">
                <StreamStatusBadge status={stream.status} severity={stream.severity} />
                <span className="text-[11px] text-muted-foreground">
                  {finding(stream)}
                  {" - "}
                  {evidence(stream)}
                </span>
                {/* The verdict is recomputed from run history on every request, so after landing data by hand
                    this is the one control that answers "did that fix it" without waiting for the poll. */}
                <Button
                  variant="outline"
                  size="sm"
                  className="ml-auto"
                  onClick={() => void query.refetch()}
                  disabled={query.isFetching}
                  aria-label="Recheck this stream"
                  data-testid="datastream-detail-recheck"
                >
                  <RefreshCw className={query.isFetching ? "animate-spin" : undefined} />
                  Recheck
                </Button>
              </div>
              <p className="text-[11px] text-muted-foreground">
                Checked <RelativeTime value={new Date(query.dataUpdatedAt).toISOString()} absolute={false} />
              </p>
              <p className="text-[13px] leading-5">{stream.summary}</p>

              {/* The learned pattern: the reference the verdict above is stated against. Without it,
                  "silent for four days" is a number rather than a judgement. */}
              <div className="rounded-lg border border-border bg-muted/30 p-3">
                <div className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
                  Learned pattern
                </div>
                <p className="mt-1 text-[13px] leading-5">{stream.profile.pattern.description}</p>
                {/* The recurring delivery, called out rather than left inside the sentence, because the date
                    the next one is due is the one thing on this panel an operator can act on: it is when to
                    come back and check that the big one arrived. */}
                {stream.profile.pattern.cycle !== null && (
                  <div className="mt-2 grid grid-cols-2 gap-2 border-t border-border pt-2 sm:grid-cols-4">
                    <Stat
                      label="Cycle"
                      value={stream.profile.pattern.cycle.monthly
                        ? "monthly"
                        : `every ${stream.profile.pattern.cycle.periodDays}d`}
                    />
                    <Stat
                      label={stream.profile.pattern.cycle.heavier ? "Cycle day" : "Light day"}
                      value={`${formatRows(stream.profile.pattern.cycle.cycleRows)} vs ${
                        formatRows(stream.profile.pattern.cycle.ordinaryRows)}`}
                    />
                    <Stat
                      label="Last one"
                      value={stream.profile.pattern.cycle.lastOccurrenceUtc?.slice(0, 10) ?? "-"}
                    />
                    <Stat
                      label="Next due"
                      value={stream.profile.pattern.cycle.nextExpectedUtc?.slice(0, 10) ?? "-"}
                    />
                  </div>
                )}
              </div>

              <div className="grid grid-cols-2 gap-3 rounded-lg border border-border p-3 sm:grid-cols-3">
                <Stat label="Avg inserted / run" value={formatRows(stream.profile.avgRowsInsertedPerRun)} />
                <Stat label="Avg updated / run" value={formatRows(stream.profile.avgRowsUpdatedPerRun)} />
                <Stat label="Avg deleted / run" value={formatRows(stream.profile.avgRowsDeletedPerRun)} />
                <Stat label="Median load / day" value={formatRows(stream.profile.medianRowsWrittenPerLoadedDay)} />
                <Stat
                  label="Expected gap"
                  value={`${stream.profile.expectedGapDays}d (${stream.profile.cadenceSource})`}
                />
                <Stat label="Last load" value={formatDays(stream.profile.daysSinceLastLoad)} />
                <Stat
                  label="Empty days"
                  value={`${stream.profile.unexpectedNullDays} vs ${stream.profile.predictedNullDays.toFixed(1)} predicted`}
                />
                <Stat
                  label="Empty day split"
                  value={`${stream.profile.emptyRunDays} ran empty, ${stream.profile.noRunDays} no run`}
                />
                <Stat
                  label="Normal delivery"
                  value={`${(stream.profile.pattern.reliability * 100).toFixed(0)}% of expected days`}
                />
                <Stat label="Loading days" value={`${stream.profile.loadedDays} of ${stream.profile.runDays} run days`} />
                <Stat label="Runs" value={`${stream.profile.runs}${stream.profile.failures > 0 ? `, ${stream.profile.failures} failed` : ""}`} />
                <Stat
                  label="Trend"
                  value={`${stream.profile.trendRowsPerDay >= 0 ? "+" : ""}${Math.round(stream.profile.trendRowsPerDay).toLocaleString()} rows/day`}
                />
              </div>

              {stream.profile.trimmedLoadDays > 0 && (
                <p className="text-[11px] text-muted-foreground">
                  {stream.profile.trimmedLoadDays} day(s) above {formatRows(stream.profile.trimFence)} rows were
                  trimmed as suspected reprocessing before fitting. The chart still shows what really landed.
                </p>
              )}

              {stream.scheduleName !== null && (
                <p className="text-[11px] text-muted-foreground">
                  Held to schedule <span className="font-mono">{stream.scheduleName}</span>
                  {stream.cron !== null && <> (<span className="font-mono">{stream.cron}</span>, {stream.timezone})</>}
                </p>
              )}

              {stream.series !== null && stream.series.length > 0 ? (
                <div className="flex flex-col gap-1.5">
                  <div className="flex flex-wrap items-center gap-3 text-[11px] text-muted-foreground">
                    <span className="inline-flex items-center gap-1.5">
                      <span className="size-2.5 rounded-sm" style={{ background: ink.series }} aria-hidden />
                      Rows written
                    </span>
                    <span className="inline-flex items-center gap-1.5">
                      <span className="h-0.5 w-4 border-t-2 border-dashed" style={{ borderColor: ink.axis }} aria-hidden />
                      Expected
                    </span>
                    <span className="inline-flex items-center gap-1.5">
                      <span className="size-2.5 rounded-sm bg-destructive" aria-hidden />
                      Flagged day
                    </span>
                  </div>
                  <ResponsiveContainer width="100%" height={220}>
                    <ComposedChart data={stream.series} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
                      <CartesianGrid stroke={ink.grid} strokeDasharray="3 3" vertical={false} />
                      <XAxis
                        dataKey="date"
                        tickFormatter={(value: string) => value.slice(5, 10)}
                        stroke={ink.axis}
                        tick={{ fontSize: 11 }}
                        minTickGap={24}
                      />
                      <YAxis
                        stroke={ink.axis}
                        tick={{ fontSize: 11 }}
                        width={52}
                        tickFormatter={(value: number) => formatRows(value, true)}
                      />
                      <Tooltip content={<PointTooltip />} cursor={{ fill: ink.cursor, fillOpacity: 0.35 }} />
                      <Bar dataKey="rowsWritten" radius={[4, 4, 0, 0]} isAnimationActive={false}>
                        {stream.series.map((point) => (
                          <Cell
                            key={point.date}
                            fill={point.anomaly ? ink.flagged : ink.series}
                            fillOpacity={point.immature ? 0.45 : 1}
                          />
                        ))}
                      </Bar>
                      <Line
                        dataKey="expected"
                        stroke={ink.axis}
                        strokeWidth={2}
                        strokeDasharray="4 3"
                        dot={false}
                        isAnimationActive={false}
                      />
                    </ComposedChart>
                  </ResponsiveContainer>
                </div>
              ) : (
                <EmptyState title="No scored series" description="This stream has no analysed days in the window." />
              )}

              <div>
                <h3 className="mb-1 text-[13px] font-medium">Detectors</h3>
                {stream.signals.map((signal) => <SignalRow key={signal.detector} signal={signal} />)}
              </div>

              {/* A stopped stream is rarely about this flow: the upstream stopped producing, or something
                  downstream is already reading a stale table. So the graph of the object it writes is the
                  next step before acting, and it is offered here rather than left to be found by name. */}
              <div className="flex flex-wrap gap-2">
                <Button variant="outline" size="sm" onClick={() => navigate(`/pipelines/${stream.pipelineId}`)}>
                  Open flow
                </Button>
                <Button variant="outline" size="sm" onClick={() => navigate(`/runs?pipelineId=${stream.pipelineId}`)}>
                  Runs
                </Button>
                {stream.targetObjectKey !== null && (
                  <LineageJumpButton
                    variant="outlined"
                    fullLabel
                    target={{
                      kind: "object",
                      objectKey: stream.targetObjectKey,
                      objectKind: stream.targetObjectKind ?? "",
                      label: stream.targetObject ?? stream.flowName,
                      sublabel: "What feeds this table, and what reads it",
                    }}
                  />
                )}
              </div>
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
