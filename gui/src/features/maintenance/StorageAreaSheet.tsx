import { useEffect, useRef, useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { toast } from "sonner";
import { CalendarClock, Eye, Loader2, TrendingUp, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { maintenanceApi } from "../../api/endpoints";
import type { StorageArea, StorageCleanup, StorageCleanupPreview } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { RichTooltip } from "../../components/RichTooltip";
import { pollingInterval } from "../../hooks/usePolling";
import { allNavItems } from "../../layout/nav";
import { STORAGE_KEY, cleanupsKey } from "./storageQueries";
import { VerdictGlyph, VerdictMark, dateText, dateTimeText } from "./storagePresentation";
import {
  daysText, reasonHeading, sizeText, verdictMeaning,
} from "./storageText";

/** The days offered at a click, each previewed at once: a month, a quarter, a year and two. */
const DAY_CHOICES = [
  { days: 30, label: "30 days" },
  { days: 90, label: "90 days" },
  { days: 365, label: "1 year" },
  { days: 730, label: "2 years" },
];

/** This page's own route: an area managed here links nowhere. */
const MAINTENANCE_PATH = "/settings/maintenance";

function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : "Something went wrong.";
}

/** A whole number of days the area takes, or null for anything else. */
function parseDays(text: string, min: number, max: number): number | null {
  const trimmed = text.trim();
  if (!/^\d+$/.test(trimmed)) {
    return null;
  }

  const value = Number.parseInt(trimmed, 10);
  return value >= min && value <= max ? value : null;
}

function intervalText(seconds: number): string {
  if (seconds % 3600 === 0) {
    return seconds === 3600 ? "every hour" : `every ${seconds / 3600} hours`;
  }

  return seconds % 60 === 0 ? `every ${seconds / 60} minutes` : `every ${seconds} seconds`;
}

function Section({ title, children, testId, aside }: { title: string; children: ReactNode; testId?: string; aside?: ReactNode }) {
  return (
    <section className="flex flex-col gap-2 border-b border-border/60 px-4 py-3 last:border-b-0" data-testid={testId}>
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{title}</h3>
        {aside}
      </div>
      {children}
    </section>
  );
}

/** One fact of the area's header: a small caption over a mono value. */
function Fact({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col">
      <span className="text-[10.5px] uppercase tracking-wider text-muted-foreground">{label}</span>
      <span className="truncate font-mono text-[13px] tabular-nums">{value}</span>
    </div>
  );
}

function CleanupRow({ entry, unit }: { entry: StorageCleanup; unit: string }) {
  const how = entry.trigger === "automatic" ? "Automatic" : `By ${entry.actor ?? "an admin"}`;
  return (
    <tr className="border-b border-border/50 last:border-b-0" data-testid="cleanup-row">
      <td className="py-1.5 pr-3 font-mono text-xs tabular-nums whitespace-nowrap">{dateTimeText(entry.startedUtc)}</td>
      <td className="max-w-[140px] truncate py-1.5 pr-3 text-xs">
        <RichTooltip title={how} body={entry.trigger === "automatic" ? `The limit ${entry.actor ?? "an admin"} set, applied by the automatic cleanup.` : `Asked for by ${entry.actor ?? "an admin"} on this page.`}>
          <span>{how}</span>
        </RichTooltip>
      </td>
      <td className="py-1.5 pr-3 text-xs whitespace-nowrap text-muted-foreground">{daysText(entry.days)}</td>
      <td className="py-1.5 text-right text-xs whitespace-nowrap">
        {entry.running ? (
          <span className="inline-flex items-center gap-1"><Loader2 className="size-3 animate-spin" aria-hidden /> running</span>
        ) : entry.error === null ? (
          <span className="font-mono tabular-nums">{entry.removed.toLocaleString()} {unit}</span>
        ) : (
          <RichTooltip title="Did not finish" body={entry.error}>
            <span className="text-destructive">{entry.removed > 0 ? `${entry.removed.toLocaleString()}, then stopped` : "did not finish"}</span>
          </RichTooltip>
        )}
      </td>
    </tr>
  );
}

/**
 * One storage area, opened from the Maintenance page. It reads top down as the decision it supports: what the area holds,
 * the verdict and the reason for it (and what a cleanup always keeps), how its data leaves the database today, and, for an
 * area that takes one, its retention: the limit in force, then one choice of days whose preview says exactly what would go,
 * from which an admin either cleans up once or keeps those days as the limit the automatic cleanup applies. The cleanups
 * that ran, and the tables the area takes, close it.
 */
export function StorageAreaSheet({ area, automaticCleanup, cleanupIntervalSeconds, admin, onClose }: {
  area: StorageArea | null;
  automaticCleanup: boolean;
  cleanupIntervalSeconds: number;
  admin: boolean;
  onClose: () => void;
}) {
  return (
    <Sheet open={area !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent side="right" className="w-full gap-0 overflow-y-auto p-0 sm:max-w-xl" data-testid="storage-area-sheet">
        {/* Keyed by the area, so each area the sheet opens on starts from its own limit, with no preview left over. */}
        {area !== null && (
          <AreaBody
            key={area.id}
            area={area}
            automaticCleanup={automaticCleanup}
            cleanupIntervalSeconds={cleanupIntervalSeconds}
            admin={admin}
          />
        )}
      </SheetContent>
    </Sheet>
  );
}

function AreaBody({ area, automaticCleanup, cleanupIntervalSeconds, admin }: {
  area: StorageArea;
  automaticCleanup: boolean;
  cleanupIntervalSeconds: number;
  admin: boolean;
}) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const retention = area.retention;
  // The limit in force when there is one; otherwise nothing, so no number is suggested that nobody chose.
  const [daysInput, setDaysInput] = useState(retention?.days == null ? "" : String(retention.days));
  const [preview, setPreview] = useState<StorageCleanupPreview | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [watching, setWatching] = useState<number | null>(null);
  const announced = useRef(new Set<number>());

  const history = useQuery({
    queryKey: cleanupsKey(area.id),
    queryFn: () => maintenanceApi.cleanups(area.id, 20),
    enabled: retention !== null,
    refetchInterval: (q) => (q.state.data?.some((c) => c.running) === true || watching !== null ? 2_000 : pollingInterval(60_000)()),
  });

  // The cleanup this sheet started: when its entry closes, say once how it ended and measure the page again.
  useEffect(() => {
    const done = watching === null ? undefined : history.data?.find((c) => c.id === watching && !c.running && c.completedUtc !== null);
    if (done === undefined || announced.current.has(done.id)) {
      return;
    }

    announced.current.add(done.id);
    void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
    if (done.error === null) {
      toast.success(done.removed === 0
        ? `${area.title}: nothing was old enough to remove.`
        : `${area.title}: removed ${done.removed.toLocaleString()} ${retention?.unit ?? "rows"}.`);
    } else {
      toast.error(`${area.title}: the cleanup did not finish. ${done.error}`);
    }
  }, [history.data, queryClient, watching, area.title, retention?.unit]);

  const days = retention === null ? null : parseDays(daysInput, retention.minDays, retention.maxDays);
  const shownPreview = preview !== null && preview.days === days ? preview : null;

  const setLimit = useMutation({
    mutationFn: (limit: number | null) => maintenanceApi.setRetention(area.id, limit),
    onSuccess: (stored) => {
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
      toast.success(stored.days === null
        ? `${area.title}: no limit; nothing removes it automatically.`
        : `${area.title}: data older than ${daysText(stored.days)} will be removed ${automaticCleanup ? `by the automatic cleanup, ${intervalText(cleanupIntervalSeconds)}` : "when an admin cleans up"}.`);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const previewMutation = useMutation({
    mutationFn: (chosen: number) => maintenanceApi.preview(area.id, chosen),
    onSuccess: setPreview,
    onError: (error) => toast.error(errorText(error)),
  });

  const cleanUp = useMutation({
    mutationFn: (chosen: number) => maintenanceApi.cleanUp(area.id, chosen),
    onSuccess: (entry) => {
      setConfirming(false);
      setPreview(null);
      setWatching(entry.id);
      void queryClient.invalidateQueries({ queryKey: cleanupsKey(entry.areaId) });
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
      toast.info(`${area.title}: cleanup started.`);
    },
    onError: (error) => {
      setConfirming(false);
      toast.error(errorText(error));
    },
  });

  const runningEntry = history.data?.find((c) => c.running) ?? null;
  const running = area.running || runningEntry !== null;
  // The page the area is managed on, by its menu entry, so the button names it as the menu does; none for this page itself.
  const managed = area.managedAt === null || area.managedAt === MAINTENANCE_PATH
    ? null
    : allNavItems().find((item) => item.to === area.managedAt) ?? null;
  const ManagedIcon = managed?.icon ?? null;
  const last = area.lastCleanup;
  const unit = retention?.unit ?? "rows";

  return (
    <>
      <SheetHeader className="gap-2 border-b border-border px-4 py-3">
        <div className="flex flex-wrap items-center gap-2 pr-6">
          <SheetTitle className="text-base">{area.title}</SheetTitle>
          <RichTooltip title={area.verdict === "safe" ? "Safe to remove" : area.verdict === "optional" ? "Optional" : "Keep"} body={verdictMeaning[area.verdict]}>
            <span><VerdictMark verdict={area.verdict} /></span>
          </RichTooltip>
        </div>
        <SheetDescription className="text-xs">{area.group} · {area.owner}</SheetDescription>
        <div className="grid grid-cols-4 gap-3 pt-1" data-testid="storage-area-facts">
          <Fact label="Size" value={sizeText(area.bytes)} />
          <Fact label={area.column === null ? "Rows" : "Values"} value={area.rows.toLocaleString()} />
          <Fact label={area.column === null ? "Tables" : "Column"} value={area.column === null ? area.tables.length.toLocaleString() : area.column.column} />
          <Fact label="Last cleanup" value={running ? "running" : last === null ? "never" : dateText(last.startedUtc)} />
        </div>
        {managed !== null && (
          <div>
            <Button size="sm" variant="outline" className="h-7" onClick={() => navigate(managed.to)} data-testid="storage-area-open">
              {ManagedIcon !== null && <ManagedIcon className="size-3.5" />}
              Open {managed.label}
            </Button>
          </div>
        )}
      </SheetHeader>

      <Section title="What it holds">
        <p className="text-[13px] leading-snug">{area.holds}</p>
      </Section>

      <Section title={reasonHeading[area.verdict]} testId="storage-area-reason">
        <div className="flex gap-2.5 rounded-md border border-border bg-muted/30 px-3 py-2.5">
          <VerdictGlyph verdict={area.verdict} className="mt-0.5 size-4" />
          <p className="text-[13px] leading-snug">{area.reason}</p>
        </div>
        {retention !== null && (
          <p className="text-xs leading-snug text-muted-foreground">
            <span className="font-medium text-foreground">Always kept: </span>{retention.keeps}
          </p>
        )}
      </Section>

      {(area.lifecycle !== null || area.grows) && (
        <Section title="How it leaves the database">
          {area.lifecycle !== null && <p className="text-[13px] leading-snug">{area.lifecycle}</p>}
          {area.grows && (
            <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <TrendingUp className="size-3.5 shrink-0 text-warning" aria-hidden />
              It grows with use, and nothing removes it.
            </p>
          )}
        </Section>
      )}

      {retention !== null && (
        <Section
          title="Retention"
          testId="storage-area-retention"
          aside={admin && retention.days !== null && (
            <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => setLimit.mutate(null)} disabled={setLimit.isPending} data-testid="storage-limit-clear">
              Remove limit
            </Button>
          )}
        >
          <div className="flex items-start gap-2.5" data-testid="storage-limit-status">
            <CalendarClock className={cn("mt-0.5 size-4 shrink-0", retention.days === null ? "text-muted-foreground" : "text-primary")} aria-hidden />
            <div className="flex min-w-0 flex-col gap-0.5">
              <p className="text-[13px] leading-snug">
                {retention.days === null
                  ? "No limit: nothing removes it automatically."
                  : automaticCleanup
                    ? `Older than ${daysText(retention.days)} is removed ${intervalText(cleanupIntervalSeconds)} by the automatic cleanup.`
                    : `The limit is ${daysText(retention.days)}, but the automatic cleanup is off; it applies when an admin cleans up.`}
              </p>
              {retention.updatedUtc !== null && (
                <p className="text-xs text-muted-foreground">
                  {retention.days === null ? "Limit removed" : "Set"} by {retention.updatedBy ?? "an admin"}, {dateTimeText(retention.updatedUtc)}.
                </p>
              )}
            </div>
          </div>

          {running && (
            <p className="flex items-center gap-1.5 rounded-md border border-border px-3 py-2 text-xs" data-testid="storage-area-running">
              <Loader2 className="size-3.5 animate-spin text-primary" aria-hidden />
              A cleanup is running{runningEntry === null ? "" : `, asked for by ${runningEntry.actor ?? "an admin"} at ${dateTimeText(runningEntry.startedUtc)}`}.
            </p>
          )}

          <div className="mt-1 flex flex-col gap-2 rounded-md border border-border px-3 py-2.5" data-testid="storage-area-choose">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-[13px]">Older than</span>
              <Input
                className="h-8 w-20 font-mono"
                inputMode="numeric"
                value={daysInput}
                onChange={(e) => setDaysInput(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Enter" && days !== null) { previewMutation.mutate(days); } }}
                aria-label="Days"
                data-testid="storage-clean-days"
              />
              <span className="text-[13px]">days</span>
              <Button
                size="sm"
                variant="outline"
                onClick={() => { if (days !== null) { previewMutation.mutate(days); } }}
                disabled={days === null || previewMutation.isPending}
                data-testid="storage-clean-preview"
              >
                {previewMutation.isPending ? <Loader2 className="size-3.5 animate-spin" /> : <Eye className="size-3.5" />}
                Preview
              </Button>
              {days === null && daysInput.trim() !== "" && (
                <span className="text-xs text-destructive">
                  {retention.minDays.toLocaleString()} to {retention.maxDays.toLocaleString()} days
                </span>
              )}
            </div>

            <div className="flex flex-wrap items-center gap-1.5" data-testid="storage-days-choices">
              {DAY_CHOICES.filter((choice) => choice.days >= retention.minDays && choice.days <= retention.maxDays).map((choice) => (
                <button
                  key={choice.days}
                  type="button"
                  className={cn(
                    "rounded-sm border border-border px-2 py-0.5 text-xs transition-colors hover:bg-accent",
                    days === choice.days && "border-primary text-primary",
                  )}
                  onClick={() => { setDaysInput(String(choice.days)); previewMutation.mutate(choice.days); }}
                  data-testid={`storage-days-${choice.days}`}
                >
                  {choice.label}
                </button>
              ))}
            </div>

            {shownPreview === null ? (
              <p className="text-xs text-muted-foreground">
                Preview counts what a cleanup at these days would remove, and changes nothing. From there you can clean up once, or keep the days as the limit.
              </p>
            ) : (
              <div className="flex flex-col gap-2" data-testid="storage-clean-preview-result">
                <p className="text-[13px] leading-snug">
                  {shownPreview.count === 0
                    ? `Nothing from before ${dateText(shownPreview.olderThanUtc)} is left to remove.`
                    : <><span className="font-mono">{shownPreview.count.toLocaleString()}</span> {shownPreview.unit} from before {dateText(shownPreview.olderThanUtc)} would go.</>}
                </p>
                {admin ? (
                  <div className="flex flex-wrap items-center gap-2">
                    {shownPreview.count > 0 && (
                      <Button
                        size="sm"
                        variant="destructive"
                        onClick={() => setConfirming(true)}
                        disabled={cleanUp.isPending || running}
                        data-testid="storage-clean-start"
                      >
                        <Trash2 className="size-3.5" />
                        Clean up now
                      </Button>
                    )}
                    {retention.days === shownPreview.days ? (
                      <span className="text-xs text-muted-foreground">These days are the limit.</span>
                    ) : (
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => setLimit.mutate(shownPreview.days)}
                        disabled={setLimit.isPending}
                        data-testid="storage-limit-save"
                      >
                        {setLimit.isPending && <Loader2 className="size-3.5 animate-spin" />}
                        Keep {daysText(shownPreview.days)} as the limit
                      </Button>
                    )}
                  </div>
                ) : (
                  <p className="text-xs text-muted-foreground">Only an admin can clean up or set a limit.</p>
                )}
              </div>
            )}
          </div>
        </Section>
      )}

      {retention !== null && (
        <Section title="Cleanups" testId="storage-area-history">
          {history.isLoading ? (
            <Skeleton className="h-16" />
          ) : history.isError ? (
            <p className="text-xs text-destructive">{errorText(history.error)}</p>
          ) : (history.data ?? []).length === 0 ? (
            <p className="text-xs text-muted-foreground">Nothing has been cleaned up yet.</p>
          ) : (
            <table className="w-full">
              <thead>
                <tr className="text-left text-[10.5px] uppercase tracking-wider text-muted-foreground">
                  <th className="pb-1 pr-3 font-medium">When</th>
                  <th className="pb-1 pr-3 font-medium">How</th>
                  <th className="pb-1 pr-3 font-medium">Older than</th>
                  <th className="pb-1 text-right font-medium">Removed</th>
                </tr>
              </thead>
              <tbody>
                {(history.data ?? []).map((entry) => <CleanupRow key={entry.id} entry={entry} unit={unit} />)}
              </tbody>
            </table>
          )}
        </Section>
      )}

      <Section title={area.column === null ? "Tables" : "Column"}>
        {area.measureError !== null && <p className="text-xs text-muted-foreground">{area.measureError}</p>}
        {area.column !== null ? (
          <p className="text-[13px] leading-snug">
            The <span className="font-mono">{area.column.column}</span> column of{" "}
            <span className="font-mono">{area.column.schema}.{area.column.table}</span>; a cleanup clears its value and the row stays.
          </p>
        ) : area.tables.length === 0 ? (
          <p className="font-mono text-xs text-muted-foreground">{area.claims.join(", ")}</p>
        ) : (
          <div className="flex flex-col">
            {area.tables.map((t) => (
              <div key={`${t.schema}.${t.name}`} className="flex items-center justify-between gap-3 border-b border-border/40 py-1 text-xs last:border-b-0">
                <span className="truncate font-mono">{t.schema}.{t.name}</span>
                <span className="shrink-0 font-mono tabular-nums text-muted-foreground">
                  {t.rows.toLocaleString()} rows · {sizeText(t.bytes)}
                </span>
              </div>
            ))}
          </div>
        )}
      </Section>

      <ConfirmDialog
        open={confirming}
        title={`Clean up ${area.title.toLowerCase()}?`}
        message={shownPreview === null
          ? "This permanently removes what the preview counted. It cannot be undone."
          : `This permanently removes ${shownPreview.count.toLocaleString()} ${shownPreview.unit} from before ${dateText(shownPreview.olderThanUtc)}. Always kept: ${retention?.keeps ?? ""} It cannot be undone.`}
        confirmLabel={shownPreview === null ? "Clean up" : `Remove ${shownPreview.count.toLocaleString()} ${shownPreview.unit}`}
        danger
        busy={cleanUp.isPending}
        onConfirm={() => { if (shownPreview !== null) { cleanUp.mutate(shownPreview.days); } }}
        onClose={() => setConfirming(false)}
      />
    </>
  );
}
