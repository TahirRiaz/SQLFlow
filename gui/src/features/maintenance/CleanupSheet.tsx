import { useEffect, useMemo, useRef, useState } from "react";
import { useQueries, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { BrushCleaning, CircleCheck, CircleX, Loader2, Lock } from "lucide-react";
import {
  AlertDialog, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { maintenanceApi } from "../../api/endpoints";
import type { StorageArea, StorageCleanupPreview, StorageReport } from "../../api/types";
import { RichTooltip } from "../../components/RichTooltip";
import { STORAGE_KEY, cleanupsKey } from "./storageQueries";
import { VerdictGlyph, dateText } from "./storagePresentation";
import {
  cleanableAreas, daysFit, daysText, estimateFreed, initialSelection, sizeText, verdictLabels, verdictShort,
} from "./storageText";

/** The ages offered at a click; a person may type any other. */
const AGE_CHOICES = [
  { days: 30, label: "30 days" },
  { days: 90, label: "90 days" },
  { days: 365, label: "1 year" },
  { days: 730, label: "2 years" },
];

/** The age the panel starts at: a month, which costs nothing for what is safe to remove, and touches nothing optional
 * until a person ticks it. */
const DEFAULT_DAYS = 30;

function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : "Something went wrong.";
}

/** One cleanup this panel started: the area, its entry, and how it ended once it has. */
interface Started {
  areaId: string;
  entryId: number;
}

/**
 * The safe purge: everything a cleanup can take, in one place. The areas that are safe to remove start ticked and the
 * optional ones start clear, each saying what removing it costs; one age applies to all, and each area previews how much
 * of it is older, before anything is chosen. One button, after a confirmation that lists what goes and what stays,
 * cleans up the ticked areas; each row then follows its cleanup to its end. A person may keep the age as the areas'
 * limit, so the automatic cleanup does the same from then on.
 */
export function CleanupSheet({ open, openedFor, report, admin, onClose }: {
  open: boolean;
  /** The area the panel was opened for, which alone starts ticked; null when opened from the page. */
  openedFor: string | null;
  report: StorageReport | undefined;
  admin: boolean;
  onClose: () => void;
}) {
  return (
    <Sheet open={open} onOpenChange={(next) => { if (!next) { onClose(); } }}>
      <SheetContent side="right" className="flex w-full flex-col gap-0 p-0 sm:max-w-2xl" data-testid="cleanup-sheet">
        {/* Keyed by what it was opened for, so each opening starts from its own selection. */}
        {open && report !== undefined && (
          <CleanupBody key={openedFor ?? "page"} openedFor={openedFor} report={report} admin={admin} onClose={onClose} />
        )}
      </SheetContent>
    </Sheet>
  );
}

function CleanupBody({ openedFor, report, admin, onClose }: {
  openedFor: string | null;
  report: StorageReport;
  admin: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const cleanable = useMemo(() => cleanableAreas(report.areas), [report.areas]);
  const opened = openedFor === null ? null : cleanable.find((area) => area.id === openedFor) ?? null;
  const [selected, setSelected] = useState<Set<string>>(() => initialSelection(cleanable, openedFor));
  const [choice, setChoice] = useState<string>(() => {
    const start = opened?.retention?.days ?? DEFAULT_DAYS;
    return AGE_CHOICES.some((c) => c.days === start) ? String(start) : "custom";
  });
  const [customText, setCustomText] = useState(() => String(opened?.retention?.days ?? DEFAULT_DAYS));
  const [keepAsLimit, setKeepAsLimit] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [started, setStarted] = useState<Started[]>([]);
  const announced = useRef(false);

  const days = choice === "custom"
    ? (/^\d+$/.test(customText.trim()) ? Number.parseInt(customText.trim(), 10) : null)
    : Number.parseInt(choice, 10);

  const previews = useQueries({
    queries: cleanable.map((area) => ({
      queryKey: ["maintenance", "preview", area.id, days] as const,
      queryFn: () => maintenanceApi.preview(area.id, days!),
      enabled: days !== null && daysFit(area, days),
      staleTime: 30_000,
    })),
  });
  const previewOf = (areaId: string): StorageCleanupPreview | undefined =>
    previews[cleanable.findIndex((area) => area.id === areaId)]?.data;

  const chosen = cleanable.filter((area) => selected.has(area.id) && days !== null && daysFit(area, days));
  const counted = chosen.map((area) => ({ area, preview: previewOf(area.id) }));
  const ready = counted.length > 0 && counted.every((c) => c.preview !== undefined);
  const toRemove = counted.filter((c) => (c.preview?.count ?? 0) > 0);
  const freed = toRemove.reduce((sum, c) => sum + (estimateFreed(c.area, c.preview!.count) ?? 0), 0);
  const unestimated = toRemove.some((c) => estimateFreed(c.area, c.preview!.count) === null);
  const cutoff = previews.find((p) => p.data !== undefined)?.data?.olderThanUtc ?? null;

  // The cleanups this panel started, as the page's report follows them: running, or ended with what they removed.
  const progress = started.map((s) => {
    const area = report.areas.find((a) => a.id === s.areaId);
    const last = area?.lastCleanup;
    const done = area !== undefined && !area.running && last !== null && last !== undefined && last.id === s.entryId && last.completedUtc !== null;
    return { ...s, area, done, removed: done ? last!.removed : 0, error: done ? last!.error : null };
  });
  const finished = progress.length > 0 && progress.every((p) => p.done);

  useEffect(() => {
    if (!finished || announced.current) {
      return;
    }

    announced.current = true;
    const failed = progress.filter((p) => p.error !== null);
    const removed = progress.reduce((sum, p) => sum + p.removed, 0);
    void queryClient.invalidateQueries({ queryKey: ["maintenance", "preview"] });
    if (failed.length === 0) {
      toast.success(`Cleanup finished: ${removed.toLocaleString()} removed from ${progress.length} ${progress.length === 1 ? "area" : "areas"}.`);
    } else {
      toast.error(`Cleanup of ${failed.map((p) => p.area?.title ?? p.areaId).join(", ")} did not finish. ${failed[0].error}`);
    }
  }, [finished, progress, queryClient]);

  const toggle = (areaId: string, on: boolean) => setSelected((current) => {
    const next = new Set(current);
    if (on) {
      next.add(areaId);
    } else {
      next.delete(areaId);
    }

    return next;
  });

  const run = async () => {
    if (days === null) {
      return;
    }

    setBusy(true);
    const begun: Started[] = [];
    try {
      if (keepAsLimit) {
        for (const { area } of counted) {
          await maintenanceApi.setRetention(area.id, days);
        }
      }

      for (const { area } of toRemove) {
        try {
          const entry = await maintenanceApi.cleanUp(area.id, days);
          begun.push({ areaId: area.id, entryId: entry.id });
          void queryClient.invalidateQueries({ queryKey: cleanupsKey(area.id) });
        } catch (error) {
          toast.error(`${area.title}: ${errorText(error)}`);
        }
      }
    } catch (error) {
      toast.error(errorText(error));
    } finally {
      setBusy(false);
      setConfirming(false);
      setStarted(begun);
      announced.current = false;
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
    }

    if (begun.length > 0) {
      toast.info(`Cleaning up ${begun.length} ${begun.length === 1 ? "area" : "areas"}; each row shows when it ends.`);
    } else if (keepAsLimit) {
      toast.success(`Nothing older than ${daysText(days)} to remove now; the limit is set.`);
    }
  };

  const safe = cleanable.filter((area) => area.verdict === "safe");
  const optional = cleanable.filter((area) => area.verdict === "optional");
  const selfCleaning = report.areas.filter((area) => area.retention === null && area.verdict === "safe").length;
  const running = progress.some((p) => !p.done);

  const row = (area: StorageArea) => {
    const index = cleanable.findIndex((a) => a.id === area.id);
    const preview = previews[index];
    const fits = days !== null && daysFit(area, days);
    const mine = progress.find((p) => p.areaId === area.id);
    const checked = selected.has(area.id);
    const estimate = preview?.data === undefined ? null : estimateFreed(area, preview.data.count);
    return (
      <label
        key={area.id}
        className={cn(
          "flex cursor-pointer gap-3 border-b border-border/60 px-4 py-2.5 last:border-b-0 hover:bg-accent/40",
          !checked && "opacity-90",
        )}
        data-testid={`cleanup-row-${area.id}`}
      >
        <Checkbox
          className="mt-0.5"
          checked={checked}
          onCheckedChange={(value) => toggle(area.id, value === true)}
          disabled={busy || running}
          aria-label={`Clean up ${area.title}`}
        />
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <div className="flex items-baseline justify-between gap-3">
            <span className="truncate text-[13px] font-medium">{area.title}</span>
            <span className="shrink-0 text-right text-[13px]" data-testid={`cleanup-count-${area.id}`}>
              {mine !== undefined ? (
                mine.done
                  ? mine.error === null
                    ? <span className="inline-flex items-center gap-1"><CircleCheck className="size-3.5 text-success" aria-hidden /><span className="font-mono">{mine.removed.toLocaleString()}</span> removed</span>
                    : <RichTooltip title="Did not finish" body={mine.error}><span className="inline-flex items-center gap-1 text-destructive"><CircleX className="size-3.5" aria-hidden />did not finish</span></RichTooltip>
                  : <span className="inline-flex items-center gap-1"><Loader2 className="size-3.5 animate-spin text-primary" aria-hidden />cleaning up</span>
              ) : !fits ? (
                <span className="text-xs text-muted-foreground">
                  {days === null ? "choose an age" : `takes ${area.retention!.minDays.toLocaleString()} to ${area.retention!.maxDays.toLocaleString()} days`}
                </span>
              ) : preview?.isLoading || preview?.isFetching && preview.data === undefined ? (
                <Loader2 className="inline size-3.5 animate-spin text-muted-foreground" aria-label="Counting" />
              ) : preview?.isError ? (
                <span className="text-xs text-destructive">could not count</span>
              ) : preview?.data?.count === 0 ? (
                <span className="text-muted-foreground">nothing older than {daysText(days!)}</span>
              ) : preview?.data !== undefined ? (
                <span><span className="font-mono">{preview.data.count.toLocaleString()}</span> {preview.data.unit}</span>
              ) : null}
            </span>
          </div>
          <div className="flex items-baseline justify-between gap-3 text-[11px] text-muted-foreground">
            <span className="truncate">{area.group} · {area.owner}</span>
            <span className="shrink-0 font-mono tabular-nums">
              {estimate !== null && estimate > 0 ? `about ${sizeText(estimate)} of ${sizeText(area.bytes)}` : sizeText(area.bytes)}
            </span>
          </div>
          {area.verdict === "optional" && (
            <p className="mt-0.5 line-clamp-2 text-xs leading-snug">
              <span className="font-medium text-warning">Costs: </span>{area.reason}
            </p>
          )}
          <p className="text-[11px] leading-snug text-muted-foreground">
            <span className="font-medium">Always kept: </span>{area.retention!.keeps}
            {area.retention!.days !== null && <> Limit now: {daysText(area.retention!.days)}.</>}
          </p>
        </div>
      </label>
    );
  };

  return (
    <>
      <SheetHeader className="gap-1 border-b border-border px-4 py-3">
        <SheetTitle className="flex items-center gap-2 text-base">
          <BrushCleaning className="size-4 text-success" aria-hidden />
          Clean up
        </SheetTitle>
        <SheetDescription className="text-xs">
          Remove data older than an age you choose. Each area keeps what it always keeps, nothing goes until you confirm, and
          what is optional stays unless you tick it.
        </SheetDescription>
      </SheetHeader>

      <div className="flex flex-col gap-2 border-b border-border px-4 py-3" data-testid="cleanup-age">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[13px] font-medium">Older than</span>
          <ToggleGroup
            type="single"
            value={choice}
            onValueChange={(value) => { if (value !== "") { setChoice(value); } }}
            variant="outline"
            size="sm"
            disabled={busy || running}
          >
            {AGE_CHOICES.map((c) => (
              <ToggleGroupItem key={c.days} value={String(c.days)} className="px-2.5 text-xs" data-testid={`cleanup-age-${c.days}`}>
                {c.label}
              </ToggleGroupItem>
            ))}
            <ToggleGroupItem value="custom" className="px-2.5 text-xs" data-testid="cleanup-age-custom">Other</ToggleGroupItem>
          </ToggleGroup>
          {choice === "custom" && (
            <span className="flex items-center gap-1.5">
              <Input
                className="h-8 w-20 font-mono"
                inputMode="numeric"
                value={customText}
                onChange={(e) => setCustomText(e.target.value)}
                aria-label="Days"
                data-testid="cleanup-age-days"
              />
              <span className="text-xs text-muted-foreground">days</span>
            </span>
          )}
        </div>
        <p className="text-xs text-muted-foreground">
          {days === null
            ? "Type a whole number of days."
            : cutoff === null ? `Data older than ${daysText(days)}.` : `Data from before ${dateText(cutoff)}, ${daysText(days)} ago.`}
        </p>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        <div className="flex items-center gap-2 bg-muted/40 px-4 py-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
          <VerdictGlyph verdict="safe" />
          {verdictLabels.safe}: {verdictShort.safe}
        </div>
        {safe.map(row)}
        {selfCleaning > 0 && (
          <p className="border-b border-border/60 px-4 py-2 text-xs text-muted-foreground">
            {selfCleaning} more {selfCleaning === 1 ? "area that is" : "areas that are"} safe to remove {selfCleaning === 1 ? "cleans itself" : "clean themselves"} by a rule of {selfCleaning === 1 ? "its" : "their"} own; there is nothing to do there.
          </p>
        )}
        <div className="flex items-center gap-2 bg-muted/40 px-4 py-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
          <VerdictGlyph verdict="optional" />
          {verdictLabels.optional}: {verdictShort.optional}
        </div>
        {optional.map(row)}
      </div>

      <div className="flex flex-col gap-2 border-t border-border bg-card px-4 py-3" data-testid="cleanup-footer">
        <p className="text-[13px]" data-testid="cleanup-summary">
          {chosen.length === 0
            ? "Tick what to clean up."
            : !ready
              ? "Counting what would go..."
              : toRemove.length === 0
                ? `Nothing ticked is older than ${days === null ? "that" : daysText(days)}. Choose a shorter age, or tick another area.`
                : (
                  <>
                    Removes{" "}
                    {toRemove.map((c, i) => (
                      <span key={c.area.id}>
                        {i > 0 && (i === toRemove.length - 1 ? " and " : ", ")}
                        <span className="font-mono">{c.preview!.count.toLocaleString()}</span> {c.preview!.unit}
                      </span>
                    ))}
                    {freed > 0 && <>, freeing {unestimated ? "at least " : "about "}<span className="font-mono">{sizeText(freed)}</span></>}.
                  </>
                )}
        </p>
        {admin && days !== null && chosen.length > 0 && (
          <label className="flex cursor-pointer items-start gap-2 text-xs text-muted-foreground">
            <Checkbox className="mt-px" checked={keepAsLimit} onCheckedChange={(v) => setKeepAsLimit(v === true)} disabled={busy || running} data-testid="cleanup-keep-limit" />
            <span>Keep doing this: make {daysText(days)} the limit of {chosen.length === 1 ? "this area" : `these ${chosen.length} areas`}, so the automatic cleanup removes what grows older from now on.</span>
          </label>
        )}
        {!admin && (
          <p className="flex items-start gap-2 rounded-md border border-border bg-muted/40 px-3 py-2 text-xs" data-testid="cleanup-admin-only">
            <Lock className="mt-px size-3.5 shrink-0 text-muted-foreground" aria-hidden />
            <span>
              Cleaning up needs the admin role, since what it removes cannot be brought back. You see what each area would
              remove at the age you choose; an admin runs the cleanup, or sets the age as a limit the automatic cleanup applies.
            </span>
          </p>
        )}
        <div className="flex flex-wrap items-center justify-between gap-2">
          <span className="text-xs text-muted-foreground">
            {running ? "Cleaning up..." : finished ? "Done. Close, or choose again." : ""}
          </span>
          <div className="flex items-center gap-2">
            <Button size="sm" variant="ghost" onClick={onClose} disabled={busy}>Close</Button>
            <Button
              size="sm"
              variant="destructive"
              onClick={() => setConfirming(true)}
              disabled={!admin || !ready || (toRemove.length === 0 && !keepAsLimit) || busy || running}
              data-testid="cleanup-start"
            >
              <BrushCleaning className="size-3.5" />
              {toRemove.length === 0 && keepAsLimit ? "Set the limit" : `Clean up${toRemove.length > 0 ? ` ${toRemove.length} ${toRemove.length === 1 ? "area" : "areas"}` : ""}`}
            </Button>
          </div>
        </div>
      </div>

      <AlertDialog open={confirming} onOpenChange={(next) => { if (!next && !busy) { setConfirming(false); } }}>
        <AlertDialogContent className="max-w-lg" data-testid="cleanup-confirm">
          <AlertDialogHeader>
            <AlertDialogTitle>
              {toRemove.length === 0 ? `Set a limit of ${days === null ? "" : daysText(days)}?` : `Remove data older than ${days === null ? "" : daysText(days)}?`}
            </AlertDialogTitle>
            <AlertDialogDescription asChild>
              <div className="flex flex-col gap-2 text-[13px]">
                {toRemove.length > 0 && (
                  <ul className="flex flex-col gap-1">
                    {toRemove.map((c) => (
                      <li key={c.area.id} className="flex justify-between gap-3">
                        <span className="flex items-center gap-1.5"><VerdictGlyph verdict={c.area.verdict} />{c.area.title}</span>
                        <span className="font-mono">{c.preview!.count.toLocaleString()} {c.preview!.unit}</span>
                      </li>
                    ))}
                  </ul>
                )}
                <p className="text-xs">Each area keeps what it always keeps: its latest run, record attempt or test result.</p>
                {toRemove.some((c) => c.area.verdict === "optional") && (
                  <p className="text-xs text-warning">You ticked optional areas: what each says it costs goes with them.</p>
                )}
                {keepAsLimit && (
                  <p className="text-xs">From then on the automatic cleanup removes what grows older than {days === null ? "" : daysText(days)} in {counted.length === 1 ? "this area" : "these areas"}.</p>
                )}
                {toRemove.length > 0 && <p className="text-xs font-medium">This cannot be undone.</p>}
              </div>
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <Button variant="ghost" size="sm" onClick={() => setConfirming(false)} disabled={busy}>Cancel</Button>
            <Button variant="destructive" size="sm" onClick={() => void run()} disabled={busy} data-testid="cleanup-confirm-start">
              {busy && <Loader2 className="size-3.5 animate-spin" />}
              {toRemove.length === 0 ? "Set the limit" : "Clean up now"}
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
