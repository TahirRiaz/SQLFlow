import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Database, Info, ListTree, Loader2, ShieldCheck, Trash2 } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { isApiError } from "../../api/client";
import { maintenanceApi } from "../../api/endpoints";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";

const STORAGE_KEY = ["maintenance", "trace-storage"] as const;

/** The API's detail line wins over a generic title for any toast on this page. */
function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : "Something went wrong.";
}

/** One labelled count. */
function Stat({ icon: Icon, label, value, hint }: {
  icon: typeof Database;
  label: string;
  value: number;
  hint?: string;
}) {
  return (
    <div className="flex flex-col gap-1 rounded-lg border border-border p-3">
      <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Icon className="size-3.5 shrink-0" />
        {label}
      </div>
      <div className="text-2xl font-semibold tabular-nums leading-7">{value.toLocaleString()}</div>
      {hint ? <div className="text-xs text-muted-foreground">{hint}</div> : null}
    </div>
  );
}

/**
 * Settings → Maintenance: the operator's view of the per-run trace, split by kind. The generated SQL statements are
 * heavy and near-identical every run (they only change when a table's schema does), so they are purgeable, on a
 * retention schedule and with a one-click purge. The run events are the execution log (rows affected + when each run
 * ran), kept for run history and analytics, so they are shown but never deleted. The run header (stats + error) is
 * always kept regardless.
 */
export default function MaintenancePage() {
  const queryClient = useQueryClient();
  const [confirmTarget, setConfirmTarget] = useState<"statements" | "events" | null>(null);

  const storageQuery = useQuery({
    queryKey: STORAGE_KEY,
    queryFn: () => maintenanceApi.traceStorage(),
  });

  const storage = storageQuery.data;

  // The retention editor mirrors the stored value until the operator edits it. Initialised once the summary loads.
  const [keepForever, setKeepForever] = useState(false);
  const [daysInput, setDaysInput] = useState("");
  const [retentionInit, setRetentionInit] = useState(false);

  useEffect(() => {
    if (!storage || retentionInit) {
      return;
    }

    if (storage.retentionDays === null) {
      setKeepForever(true);
      setDaysInput("");
    } else {
      setKeepForever(false);
      setDaysInput(String(storage.retentionDays));
    }

    setRetentionInit(true);
  }, [storage, retentionInit]);

  const parsedDays = Number.parseInt(daysInput, 10);
  const retentionValue = keepForever ? null : parsedDays;
  const retentionValid = keepForever || (Number.isInteger(parsedDays) && parsedDays >= 0 && String(parsedDays) === daysInput.trim());
  const retentionChanged = storage !== undefined && retentionValue !== storage.retentionDays;

  const purgeMutation = useMutation({
    mutationFn: () => maintenanceApi.purgeStatements(),
    onSuccess: (result) => {
      setConfirmTarget(null);
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
      toast.success(
        result.statementsDeleted === 0
          ? "No SQL statements to remove."
          : `Removed ${result.statementsDeleted.toLocaleString()} SQL statement row(s).`,
      );
    },
    onError: (error) => {
      setConfirmTarget(null);
      toast.error(errorText(error));
    },
  });

  const eventsPurgeMutation = useMutation({
    mutationFn: () => maintenanceApi.purgeEvents(),
    onSuccess: (result) => {
      setConfirmTarget(null);
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
      toast.success(
        result.eventsDeleted === 0
          ? "No run events to remove."
          : `Removed ${result.eventsDeleted.toLocaleString()} run event row(s).`,
      );
    },
    onError: (error) => {
      setConfirmTarget(null);
      toast.error(errorText(error));
    },
  });

  const retentionMutation = useMutation({
    mutationFn: () => maintenanceApi.setStatementRetention(retentionValue),
    onSuccess: (result) => {
      setKeepForever(result.retentionDays === null);
      setDaysInput(result.retentionDays === null ? "" : String(result.retentionDays));
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
      toast.success(
        result.retentionDays === null
          ? "SQL statements will be kept forever."
          : `SQL statements older than ${result.retentionDays} day${result.retentionDays === 1 ? "" : "s"} will be removed.`,
      );
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const retentionText = useMemo(() => {
    if (!storage) {
      return "";
    }

    const days = storage.retentionDays;
    if (days === null) {
      return "SQL statements are kept forever; no automatic cleanup runs. Set a retention period to auto-drop old duplicates.";
    }

    const window = days === 0 ? "as soon as a newer run exists" : `once older than ${days} day${days === 1 ? "" : "s"}`;
    return `Automatic cleanup keeps each pipeline's latest run (the current-schema copy) and every failed run, and removes an older successful run's SQL ${window}. Reclaimable now: ${storage.prunableStatements.toLocaleString()} statement row(s) across ${storage.prunableRuns.toLocaleString()} run${storage.prunableRuns === 1 ? "" : "s"}.`;
  }, [storage]);

  return (
    <Page data-testid="maintenance-page">
      <PageHeader
        title="Maintenance"
        subtitle="Reclaim storage held by generated SQL the system no longer needs. Run history, stats, and the execution log are kept."
      />

      {storageQuery.isError ? (
        <Alert variant="destructive" data-testid="trace-storage-error">
          <Info className="size-4" />
          <AlertDescription className="flex items-center justify-between gap-3">
            <span>{errorText(storageQuery.error)}</span>
            <Button variant="outline" size="sm" onClick={() => void storageQuery.refetch()}>
              Retry
            </Button>
          </AlertDescription>
        </Alert>
      ) : storageQuery.isLoading || !storage ? (
        <Skeleton className="h-64 rounded-lg" />
      ) : (
        <div className="flex flex-col gap-4">
          {/* SQL statements: purgeable. */}
          <Card className="flex flex-col gap-4 p-4" data-testid="sql-statements-card">
            <div className="flex flex-col gap-1">
              <h2 className="text-sm font-semibold">SQL statements</h2>
              <p className="text-xs text-muted-foreground">
                The SQL each run generated. It is nearly identical on every run of a flow and only changes when a
                table's schema changes, so old copies are almost all duplication and safe to drop. This is the
                heaviest per-run data.
              </p>
            </div>

            <div className="grid grid-cols-2 gap-3 sm:max-w-md">
              <Stat icon={Database} label="Statements stored" value={storage.totalStatements} />
              <Stat icon={Trash2} label="Reclaimable now" value={storage.prunableStatements} />
            </div>

            <div className="flex flex-col gap-2 border-t border-border pt-4">
              <h3 className="text-sm font-medium">Automatic cleanup</h3>
              <Alert>
                <Info className="size-4" />
                <AlertDescription>{retentionText}</AlertDescription>
              </Alert>
              <div className="flex flex-wrap items-center gap-4">
                <div className="flex items-center gap-2">
                  <Input
                    type="number"
                    min={0}
                    inputMode="numeric"
                    value={daysInput}
                    disabled={keepForever}
                    onChange={(e) => setDaysInput(e.target.value)}
                    className="w-24"
                    data-testid="retention-days"
                  />
                  <span className="text-sm text-muted-foreground">days</span>
                </div>
                <label className="flex items-center gap-2 text-sm">
                  <Switch checked={keepForever} onCheckedChange={setKeepForever} data-testid="retention-keep-forever" />
                  Keep forever
                </label>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => retentionMutation.mutate()}
                  disabled={!retentionValid || !retentionChanged || retentionMutation.isPending}
                  data-testid="retention-save"
                >
                  {retentionMutation.isPending ? <Loader2 className="size-4 animate-spin" /> : null}
                  Save
                </Button>
              </div>
            </div>

            <div className="flex items-center gap-3 border-t border-border pt-4">
              <Button
                variant="destructive"
                onClick={() => setConfirmTarget("statements")}
                disabled={storage.totalStatements === 0 || purgeMutation.isPending}
                data-testid="statements-purge"
              >
                {purgeMutation.isPending ? <Loader2 className="size-4 animate-spin" /> : <Trash2 className="size-4" />}
                Purge all SQL statements
              </Button>
              <span className="text-xs text-muted-foreground">
                {storage.totalStatements === 0 ? "Nothing stored." : "Deletes every stored SQL statement now; they regenerate on the next run."}
              </span>
            </div>
          </Card>

          {/* Run events: kept, never purged. */}
          <Card className="flex flex-col gap-4 p-4" data-testid="run-events-card">
            <div className="flex flex-col gap-1">
              <h2 className="text-sm font-semibold">Run events</h2>
              <p className="text-xs text-muted-foreground">
                The per-run execution log: rows affected at each stage and when the run executed. This is what lets
                you track a table's normal inflow over time, so it is kept for history and analytics and is never
                purged.
              </p>
            </div>

            <div className="grid grid-cols-2 gap-3 sm:max-w-md">
              <Stat icon={ListTree} label="Events stored" value={storage.totalEvents} />
              <div className="flex flex-col justify-center gap-1 rounded-lg border border-border p-3">
                <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
                  <ShieldCheck className="size-3.5 shrink-0" />
                  Retention
                </div>
                <div className="text-sm font-medium">Never auto-purged</div>
              </div>
            </div>

            <div className="flex items-center gap-3 border-t border-border pt-4">
              <Button
                variant="destructive"
                onClick={() => setConfirmTarget("events")}
                disabled={storage.totalEvents === 0 || eventsPurgeMutation.isPending}
                data-testid="events-purge"
              >
                {eventsPurgeMutation.isPending ? <Loader2 className="size-4 animate-spin" /> : <Trash2 className="size-4" />}
                Purge all run events
              </Button>
              <span className="text-xs text-muted-foreground">
                {storage.totalEvents === 0
                  ? "Nothing stored."
                  : "Deletes every stored run event now. This also drops the per-run inflow history used for analytics."}
              </span>
            </div>
          </Card>
        </div>
      )}

      <ConfirmDialog
        open={confirmTarget === "statements"}
        title="Purge all SQL statements?"
        message={
          storage
            ? `This permanently deletes all ${storage.totalStatements.toLocaleString()} stored SQL statement row(s) for every run. Run history, stats, error messages, and the run events (execution log) are kept. The SQL regenerates on the next run. This cannot be undone.`
            : "This permanently deletes all stored SQL statements. This cannot be undone."
        }
        confirmLabel="Purge SQL statements"
        danger
        busy={purgeMutation.isPending}
        onConfirm={() => purgeMutation.mutate()}
        onClose={() => setConfirmTarget(null)}
      />

      <ConfirmDialog
        open={confirmTarget === "events"}
        title="Purge all run events?"
        message={
          storage
            ? `This permanently deletes all ${storage.totalEvents.toLocaleString()} stored run event row(s) for every run. Run history and stats on the run header are kept, but the per-run execution log (rows affected at each stage and stage timing) is lost, including the inflow history used for analytics. This cannot be undone.`
            : "This permanently deletes all stored run events, including the inflow history used for analytics. This cannot be undone."
        }
        confirmLabel="Purge run events"
        danger
        busy={eventsPurgeMutation.isPending}
        onConfirm={() => eventsPurgeMutation.mutate()}
        onClose={() => setConfirmTarget(null)}
      />
    </Page>
  );
}
