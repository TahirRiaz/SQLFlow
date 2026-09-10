import { useEffect, useId, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMutation, useQuery } from "@tanstack/react-query";
import { CalendarClock, Info, Loader2, Play, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, runApi, scheduleApi } from "../../api/endpoints";
import type { RunParameterDescriptor, RunScope } from "../../api/types";
import { ComboBoxField } from "../../components/ComboBoxField";
import { CorrelationError } from "../../components/CorrelationError";
import { DateRangeCalendar } from "../../components/DateRangeCalendar";
import { useRunDock } from "./RunDockContext";

/** Prior-run values used to prefill the form on Re-run (ISO strings for the window; they are trimmed to the
 * minute for the datetime-local inputs). Absent fields default to empty/off. */
export interface TriggerRunParameterValues {
  fullLoad?: boolean;
  backfillFrom?: string | null;
  backfillTo?: string | null;
  filePattern?: string | null;
  sourceFilter?: string | null;
  assertionsOnly?: boolean;
}

export interface TriggerRunDialogProps {
  open: boolean;
  onClose: () => void;
  /** Prefills (and locks) the repo when launched from a repo/pipeline context. */
  repoId?: string;
  /** Prefills (and locks) the flow when launched from a pipeline detail page or a lineage node. */
  flowName?: string;
  /** The flow's pipeline id, when the launching context knows it (pipeline detail, Re-run). Lets the dialog load
   * the flow's applicable parameters without first resolving the id from the repo's pipeline list. */
  flowId?: string;
  /** Prior-run parameter values to prefill (Re-run). */
  initialParameters?: TriggerRunParameterValues;
  /** The initial execution scope (defaults to "flow"). Set by the lineage graph's Run / Run + descendants / Run
   * batch actions and by the batch status board. */
  scope?: RunScope;
  /** The batch label for a batch-scoped launch (from the status board); locks the dialog to that batch. */
  batch?: string;
}

const SCOPE_LABELS: Record<RunScope, string> = {
  flow: "This flow",
  node: "This flow + descendants",
  batch: "Whole batch",
};

/** The scopes a free-choice trigger offers. "Whole batch" is not a run scope in V3 (the server runs a whole source
 * through its schedule, not a batch trigger), so it is surfaced as the related-schedule section below instead. */
const OFFERED_SCOPES: RunScope[] = ["flow", "node"];

/** A one-line cadence for a related schedule ("cron 0 2 * * *", "every 3600s", or "manual"). */
function describeCadence(cron: string | null, intervalSeconds: number | null): string {
  if (cron !== null && cron.trim() !== "") {
    return `cron ${cron}`;
  }

  if (intervalSeconds !== null) {
    return `every ${intervalSeconds}s`;
  }

  return "manual";
}

/** An ISO instant (or datetime-local string) trimmed to the "yyyy-MM-ddThh:mm" a datetime-local input expects. */
function toLocalInput(value: string | null | undefined): string {
  return value ? value.slice(0, 16) : "";
}

interface ComboOption {
  value: string;
  label: string;
}

/**
 * The single trigger-run path in the GUI: launched from the runs page (free choice of repo + flow), a pipeline's
 * detail page (prefilled), the lineage graph / batch board (prefilled with a scope), and a run's Re-run (prefilled
 * with the prior parameters). A "flow" run POSTs one flow and navigates to it; a "node" (flow + descendants) or
 * "batch" run POSTs a group and navigates to the group view.
 *
 * The parameter form is built from the selected flow's own definition: the control plane returns exactly the run
 * parameters that flow's kind honors (a copy flow gets a modified-date window, a file flow adds a glob, a
 * relational ingestion adds full-load / assertions and a window when it has a date column, and kinds with no
 * selection surface get none), so a user is never shown a control the run would ignore.
 */
export function TriggerRunDialog({
  open, onClose, repoId, flowName, flowId, initialParameters, scope, batch,
}: TriggerRunDialogProps) {
  const navigate = useNavigate();
  const idPrefix = useId();
  const { track } = useRunDock();
  const [selectedRepoId, setSelectedRepoId] = useState<string | null>(repoId ?? null);
  const [selectedFlow, setSelectedFlow] = useState<string | null>(flowName ?? null);
  const [selectedScope, setSelectedScope] = useState<RunScope>(scope ?? "flow");
  const [pool, setPool] = useState("");
  const [commitSha, setCommitSha] = useState("");
  const [fullLoad, setFullLoad] = useState(false);
  const [backfillFrom, setBackfillFrom] = useState("");
  const [backfillTo, setBackfillTo] = useState("");
  const [filePattern, setFilePattern] = useState("");
  const [sourceFilter, setSourceFilter] = useState("");
  const [assertionsOnly, setAssertionsOnly] = useState(false);
  // Node scope's "find all": include mode: manual and mode: disabled descendants in the group. Off by default,
  // so a deactivated branch is only replayed when the operator deliberately asks for it.
  const [includeAll, setIncludeAll] = useState(false);

  // A batch-locked launch (from the status board) carries no flow: force batch scope and keep it there.
  const batchLocked = batch !== undefined;
  // Seed the form once per open, so a Re-run opens with the prior run's parameters and a fresh launch opens clean.
  useEffect(() => {
    if (open) {
      setSelectedScope(batchLocked ? "batch" : scope ?? "flow");
      setFullLoad(initialParameters?.fullLoad ?? false);
      setBackfillFrom(toLocalInput(initialParameters?.backfillFrom));
      setBackfillTo(toLocalInput(initialParameters?.backfillTo));
      setFilePattern(initialParameters?.filePattern ?? "");
      setSourceFilter(initialParameters?.sourceFilter ?? "");
      setAssertionsOnly(initialParameters?.assertionsOnly ?? false);
      setIncludeAll(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const repos = useQuery({
    queryKey: ["repos", "all-for-trigger"],
    queryFn: () => repoApi.list({ page: 1, pageSize: 200 }),
    enabled: open && !repoId,
  });

  const effectiveRepoId = repoId ?? selectedRepoId;
  const effectiveFlow = flowName ?? selectedFlow;
  // The repo's flows back the free-choice dropdown AND resolve a locked flow's pipeline id, which the applicable-
  // parameters lookup keys on. A context that already knows the id (Re-run, pipeline detail) skips the list; a
  // batch-locked launch needs neither, because a group always runs default parameters.
  const pipelines = useQuery({
    queryKey: ["pipelines", "for-trigger", effectiveRepoId],
    queryFn: () => pipelineApi.list({ repoId: effectiveRepoId!, active: true, page: 1, pageSize: 200 }),
    enabled: open && !batchLocked && !flowId && Boolean(effectiveRepoId),
  });

  const isGroup = selectedScope !== "flow";
  // Both "This flow" and "This flow + descendants" honor a backfill window: a single flow bounds its own read; a
  // node run bounds its ANCHOR (the parent) and its descendants switch to MIN-from-source so the back-dated rows the
  // anchor lands are picked up downstream instead of stopping at staging. "Whole batch" (only ever set by a
  // batch-locked launch) carries no per-run parameters and is surfaced through its schedule instead.
  const paramsScope: "flow" | "node" | "none" =
    selectedScope === "flow" ? "flow" : selectedScope === "node" ? "node" : "none";

  // The selected flow's pipeline id: given directly by the launching context, or resolved from the repo's pipeline
  // list for a free-choice launch. Drives the applicable-parameters lookup.
  const effectiveFlowId = flowId
    ?? pipelines.data?.items.find((p) => p.name === effectiveFlow)?.id
    ?? null;

  const flowParameters = useQuery({
    queryKey: ["pipeline-parameters", effectiveFlowId],
    queryFn: () => pipelineApi.parameters(effectiveFlowId!),
    enabled: open && paramsScope !== "none" && Boolean(effectiveFlowId),
  });
  const applicable = useMemo(() => flowParameters.data?.parameters ?? [], [flowParameters.data]);
  const paramKeys = useMemo(() => new Set(applicable.map((p) => p.key)), [applicable]);
  // A node run exposes only the backfill window; full load, file pattern, and assertions-only are single-flow
  // concepts (a group always runs its members otherwise as defined).
  const renderable = useMemo(
    () => (paramsScope === "node" ? applicable.filter((p) => p.key === "backfillWindow") : applicable),
    [applicable, paramsScope],
  );

  // The schedule(s) this flow is a member of: firing one runs the whole source in dependency order (the V3
  // "whole batch"). Membership is the selector, so this asks the API by pipeline id.
  const schedules = useQuery({
    queryKey: ["flow-schedules", effectiveRepoId, effectiveFlowId],
    queryFn: () => scheduleApi.list({ repoId: effectiveRepoId!, pipelineId: effectiveFlowId!, page: 1, pageSize: 50 }),
    enabled: open && Boolean(effectiveRepoId) && Boolean(effectiveFlowId),
  });
  const relatedSchedules = schedules.data?.items ?? [];

  const runSchedule = useMutation({
    mutationFn: (scheduleId: string) => scheduleApi.runNow(scheduleId),
    onSuccess: (accepted) => {
      onClose();
      if (accepted.groupId) {
        track(accepted.groupId);
        toast.success(`Schedule fired (${accepted.memberCount} ${accepted.memberCount === 1 ? "flow" : "flows"}).`);
        navigate(`/runs/groups/${accepted.groupId}`);
      } else {
        toast.success("Schedule fired.");
        navigate(`/runs/${accepted.runId}`);
      }
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  // "Still resolving" and "could not resolve" are each distinct from "this flow honors no parameters". Collapsing
  // them would tell an operator a flow runs as defined while its lookup is in flight or failed, hiding the very
  // overrides the engine would honor (a copy flow's backfill window, for one).
  const resolvingParameters = pipelines.isLoading || flowParameters.isLoading;
  const parametersUnavailable = !resolvingParameters
    && (flowParameters.isError || (effectiveFlowId === null && !isGroup && Boolean(effectiveFlow)));

  // For a Node or Batch scope, preview which flows the run would touch, so the operator sees "will run N flows across
  // M waves" before committing. A single flow needs no preview.
  const previewEnabled = open
    && selectedScope !== "flow"
    && Boolean(effectiveRepoId)
    && (batchLocked ? Boolean(batch) : Boolean(effectiveFlow));
  const preview = useQuery({
    queryKey: ["run-scope-preview", effectiveRepoId, effectiveFlow, selectedScope, batch, includeAll],
    queryFn: () => runApi.previewScope({
      repoId: effectiveRepoId!,
      flowName: batchLocked ? undefined : effectiveFlow ?? undefined,
      scope: selectedScope,
      batch: batchLocked ? batch : undefined,
      includeAll: selectedScope === "node" ? includeAll : undefined,
    }),
    enabled: previewEnabled,
  });

  const trigger = useMutation({
    mutationFn: runApi.trigger,
    onSuccess: (accepted) => {
      onClose();
      if (accepted.groupId) {
        // Watch the group in the run tray so it stays reachable after this dialog closes and across a tab change.
        track(accepted.groupId);
        const target = selectedScope === "batch"
          ? `batch ${(batchLocked ? batch : preview.data?.anchor) ?? effectiveFlow ?? ""}`
          : `${effectiveFlow ?? ""} + descendants`;
        toast.success(`Run group queued for ${target} (${accepted.memberCount ?? 0} flows).`);
        navigate(`/runs/groups/${accepted.groupId}`);
      } else {
        toast.success(`Run queued: ${effectiveFlow ?? accepted.runId ?? ""}`);
        navigate(`/runs/${accepted.runId}`);
      }
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  const repoOptions = useMemo<ComboOption[]>(
    () => (repos.data?.items ?? []).map((repo) => ({ value: repo.id, label: repo.name })),
    [repos.data],
  );
  const flowOptions = useMemo<ComboOption[]>(
    () => (pipelines.data?.items ?? []).map((p) => ({ value: p.name, label: p.name })),
    [pipelines.data],
  );

  const trimmedFrom = backfillFrom.trim();
  const trimmedTo = backfillTo.trim();
  const hasWindow = trimmedFrom !== "" || trimmedTo !== "";
  const hasPattern = filePattern.trim() !== "";
  const hasSourceFilter = sourceFilter.trim() !== "";

  // Client-side mirror of RunParameters.Validate (single-flow only), so obvious mistakes are caught before the round
  // trip (the server validates authoritatively and its ProblemDetails still renders if anything slips through).
  const windowError = paramsScope === "none"
    ? null
    : assertionsOnly && (fullLoad || hasWindow || hasPattern)
      ? "Assertions-only cannot be combined with full load, a window, or a file pattern."
      : trimmedTo !== "" && trimmedFrom === ""
        ? "An end date needs a start date."
        : fullLoad && hasWindow
          ? "Full load and a backfill window are mutually exclusive."
          : null;

  const hasTarget = batchLocked ? Boolean(batch) : Boolean(effectiveFlow);
  // A group run stays disabled until the preview confirms there is at least one flow to run.
  const groupReady = !isGroup || (preview.data !== undefined && preview.data.memberCount > 0);
  const canSubmit = Boolean(effectiveRepoId) && hasTarget && windowError === null && groupReady && !trigger.isPending;

  const submit = () => {
    // A single flow sends every parameter its kind honors; a node run sends only the backfill window (the server
    // applies it to the anchor and switches the descendants to MIN-from-source); a batch-locked launch sends none.
    const applies = (key: string) =>
      paramsScope !== "none" && paramKeys.has(key) && (paramsScope === "flow" || key === "backfillWindow");
    trigger.mutate({
      repoId: effectiveRepoId!,
      flowName: batchLocked ? "" : (effectiveFlow ?? "").trim(),
      scope: selectedScope,
      batch: batchLocked ? batch : null,
      pool: pool.trim() === "" ? null : pool.trim(),
      commitSha: commitSha.trim() === "" ? null : commitSha.trim(),
      // Only the parameters the flow's kind honors are sent; a group always runs default parameters.
      fullLoad: applies("fullLoad") ? fullLoad : false,
      backfillFrom: applies("backfillWindow") && trimmedFrom !== "" ? `${trimmedFrom}:00Z` : null,
      backfillTo: applies("backfillWindow") && trimmedTo !== "" ? `${trimmedTo}:00Z` : null,
      filePattern: applies("filePattern") && hasPattern ? filePattern.trim() : null,
      sourceFilter: applies("sourceFilter") && hasSourceFilter ? sourceFilter.trim() : null,
      assertionsOnly: applies("assertionsOnly") ? assertionsOnly : false,
      includeAll: selectedScope === "node" ? includeAll : false,
    });
  };

  const renderParameter = (desc: RunParameterDescriptor) => {
    switch (desc.input) {
      case "Toggle": {
        const isFull = desc.key === "fullLoad";
        const checked = isFull ? fullLoad : assertionsOnly;
        const onChange = isFull ? setFullLoad : setAssertionsOnly;
        const disabled = isFull ? assertionsOnly : (fullLoad || hasWindow || hasPattern || hasSourceFilter);
        return (
          <div key={desc.key} className="flex flex-col gap-1">
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch
                checked={checked}
                disabled={disabled}
                onCheckedChange={onChange}
                data-testid={`trigger-${desc.key}`}
              />
              {desc.label}
            </Label>
            <p className="pl-10 text-xs text-muted-foreground">{desc.help}</p>
          </div>
        );
      }
      case "DateRange":
        return (
          <div key={desc.key} className="flex flex-col gap-1.5">
            <span className="text-[13px] font-medium">{desc.label}</span>
            <DateRangeCalendar
              from={backfillFrom}
              to={backfillTo}
              onChange={(from, to) => {
                setBackfillFrom(from);
                setBackfillTo(to);
              }}
              disabled={fullLoad || assertionsOnly}
              testId="trigger-backfill"
            />
            <p className="text-xs text-muted-foreground">{desc.help}</p>
          </div>
        );
      case "Glob":
        return (
          <div key={desc.key} className="flex flex-col gap-1.5">
            <Label htmlFor={`${idPrefix}-file-pattern`}>{desc.label}</Label>
            <Input
              id={`${idPrefix}-file-pattern`}
              className="h-8 font-mono"
              placeholder="orders_2026-03*.json"
              value={filePattern}
              onChange={(event) => setFilePattern(event.target.value)}
              disabled={assertionsOnly}
              data-testid="trigger-file-pattern"
            />
            <p className="text-xs text-muted-foreground">{desc.help}</p>
          </div>
        );
      case "SqlPredicate":
        return (
          <div key={desc.key} className="flex flex-col gap-1.5">
            <Label htmlFor={`${idPrefix}-source-filter`}>{desc.label}</Label>
            <Input
              id={`${idPrefix}-source-filter`}
              className="h-8 font-mono"
              placeholder="AND pk > 92992"
              value={sourceFilter}
              onChange={(event) => setSourceFilter(event.target.value)}
              disabled={assertionsOnly}
              data-testid="trigger-source-filter"
            />
            <p className="text-xs text-muted-foreground">{desc.help}</p>
          </div>
        );
      default:
        return null;
    }
  };

  const showParameters = paramsScope !== "none" && Boolean(effectiveFlow)
    && (paramsScope === "flow" || renderable.length > 0 || resolvingParameters);

  return (
    <Sheet
      open={open}
      onOpenChange={(next) => {
        if (!next && !trigger.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full gap-0 sm:max-w-xl" data-testid="trigger-run-dialog">
        <SheetHeader>
          <SheetTitle>Trigger run</SheetTitle>
          <SheetDescription>
            Launch one flow, a flow with its descendants, or a whole batch, in dependency order.
          </SheetDescription>
        </SheetHeader>

        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4 pb-4">
          {trigger.isError && isApiError(trigger.error) && <CorrelationError error={trigger.error} />}

          {repoId ? null : (
            <ComboBoxField
              label="Repo"
              options={repoOptions}
              optionValue={(option) => option.value}
              optionLabel={(option) => option.label}
              value={selectedRepoId}
              onChange={(value) => {
                setSelectedRepoId(value);
                setSelectedFlow(null);
              }}
              loading={repos.isLoading}
              testId="trigger-repo"
            />
          )}

          {batchLocked ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor={`${idPrefix}-batch`}>Batch</Label>
              <Input
                id={`${idPrefix}-batch`}
                className="h-8 font-mono"
                value={batch}
                disabled
                data-testid="trigger-batch"
              />
            </div>
          ) : flowName ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor={`${idPrefix}-flow-locked`}>Flow</Label>
              <Input id={`${idPrefix}-flow-locked`} className="h-8 font-mono" value={flowName} disabled />
            </div>
          ) : (
            <ComboBoxField
              label="Flow"
              options={flowOptions}
              optionValue={(option) => option.value}
              optionLabel={(option) => option.label}
              value={selectedFlow}
              onChange={setSelectedFlow}
              disabled={!effectiveRepoId}
              loading={pipelines.isLoading}
              testId="trigger-flow"
            />
          )}

          {batchLocked ? null : (
            <div className="flex flex-col gap-1.5">
              <Label>Scope</Label>
              <ToggleGroup
                type="single"
                variant="outline"
                size="sm"
                value={selectedScope}
                onValueChange={(value) => {
                  if (value !== "") {
                    setSelectedScope(value as RunScope);
                  }
                }}
                data-testid="trigger-scope"
              >
                {OFFERED_SCOPES.map((s) => (
                  <ToggleGroupItem key={s} value={s} data-testid={`trigger-scope-${s}`} className="h-8 px-2.5 text-xs">
                    {SCOPE_LABELS[s]}
                  </ToggleGroupItem>
                ))}
              </ToggleGroup>
            </div>
          )}

          {selectedScope === "node" && (
            <div className="flex flex-col gap-1">
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <Switch
                  checked={includeAll}
                  onCheckedChange={setIncludeAll}
                  data-testid="trigger-include-all"
                />
                Include disabled and manual descendants
              </Label>
              <p className="pl-10 text-xs text-muted-foreground">
                By default only active (mode: auto) descendants run; turn this on to replay deactivated or
                manual-only flows together with the anchor.
              </p>
            </div>
          )}

          {isGroup && (
            <Alert data-testid="trigger-scope-preview">
              <Info />
              <AlertDescription>
                {preview.isLoading
                  ? "Resolving the flows to run..."
                  : preview.isError
                    ? "Could not resolve the flows to run."
                    : preview.data && preview.data.memberCount > 0
                      ? `Will run ${preview.data.memberCount} ${preview.data.memberCount === 1 ? "flow" : "flows"} across ${preview.data.waveCount} ${preview.data.waveCount === 1 ? "wave" : "waves"}, in dependency order`
                        + (selectedScope === "batch" ? ` (batch "${preview.data.anchor}").` : ".")
                      : "No flows to run for this selection."}
              </AlertDescription>
            </Alert>
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor={`${idPrefix}-pool`}>Pool (optional)</Label>
            <Input
              id={`${idPrefix}-pool`}
              className="h-8"
              value={pool}
              onChange={(event) => setPool(event.target.value)}
              data-testid="trigger-pool"
            />
            <p className="text-xs text-muted-foreground">
              Route the run to workers serving this pool; empty runs on any node.
            </p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor={`${idPrefix}-commit`}>Commit SHA (optional)</Label>
            <Input
              id={`${idPrefix}-commit`}
              className="h-8 font-mono"
              value={commitSha}
              onChange={(event) => setCommitSha(event.target.value)}
              data-testid="trigger-commit"
            />
            <p className="text-xs text-muted-foreground">
              Pin the run to an exact git commit; empty pins to the repo's last synced commit.
            </p>
          </div>

          {showParameters && (
            <div className="rounded-lg border border-border bg-muted/40 p-3" data-testid="trigger-parameters">
              <h3 className="text-[13px] font-medium">Run parameters</h3>
              {resolvingParameters ? (
                <div className="mt-2 flex items-center gap-2 text-[13px] text-muted-foreground">
                  <Loader2 className="size-4 animate-spin" />
                  Loading this flow's parameters...
                </div>
              ) : parametersUnavailable ? (
                <Alert className="mt-2 text-warning" data-testid="trigger-parameters-unavailable">
                  <TriangleAlert />
                  <AlertDescription className="text-warning/90">
                    Could not load this flow's run parameters. Triggering now would run it with its defined defaults.
                  </AlertDescription>
                </Alert>
              ) : renderable.length === 0 ? (
                <p className="mt-2 text-[13px] text-muted-foreground">
                  This flow runs as defined; it has no adjustable run parameters.
                </p>
              ) : (
                <div className="mt-2 flex flex-col gap-3">
                  <p className="text-xs text-muted-foreground">
                    One-off overrides applied to this run only. The flow definition in git is unchanged.
                  </p>
                  {renderable.map(renderParameter)}
                  {paramsScope === "node" && (
                    <p className="text-xs text-muted-foreground" data-testid="trigger-node-backfill-note">
                      The parent applies this window; its descendants read from the source minimum for this run, so the
                      back-dated rows are picked up instead of stopping at staging.
                    </p>
                  )}
                  {windowError !== null && (
                    <p className="text-xs font-medium text-destructive" data-testid="trigger-backfill-error">
                      {windowError}
                    </p>
                  )}
                </div>
              )}
            </div>
          )}

          {relatedSchedules.length > 0 && (
            <div className="rounded-lg border border-border bg-muted/40 p-3" data-testid="trigger-schedules">
              <h3 className="flex items-center gap-2 text-[13px] font-medium">
                <CalendarClock className="size-4 text-muted-foreground" />
                Runs as part of
              </h3>
              <p className="mt-1 text-xs text-muted-foreground">
                Executing a schedule fires its whole member set in dependency order (the V3 "whole batch"), without
                moving the next scheduled fire.
              </p>
              <div className="mt-2 flex flex-col gap-2">
                {relatedSchedules.map((schedule) => (
                  <div
                    key={schedule.id}
                    className="flex items-center gap-2 rounded-md border border-border bg-background px-2.5 py-1.5"
                    data-testid={`trigger-schedule-${schedule.id}`}
                  >
                    <div className="min-w-0 grow">
                      <div className="truncate font-mono text-[12px] font-medium">{schedule.name}</div>
                      <div className="mt-0.5 flex flex-wrap items-center gap-1.5">
                        <Badge variant="outline" className="font-mono text-[11px]">
                          {describeCadence(schedule.cron, schedule.intervalSeconds)}
                        </Badge>
                        {schedule.paused && (
                          <Badge className="border-transparent bg-warning/15 text-warning">paused</Badge>
                        )}
                        {!schedule.enabled && (
                          <Badge variant="outline" className="text-muted-foreground">disabled</Badge>
                        )}
                      </div>
                    </div>
                    <Button
                      variant="outline"
                      size="sm"
                      className="h-8 shrink-0"
                      onClick={() => runSchedule.mutate(schedule.id)}
                      disabled={runSchedule.isPending}
                      data-testid={`trigger-schedule-run-${schedule.id}`}
                    >
                      {runSchedule.isPending && runSchedule.variables === schedule.id
                        ? <Loader2 className="animate-spin" />
                        : <Play />}
                      Execute schedule
                    </Button>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>

        <SheetFooter className="flex-row justify-end gap-2 border-t border-border">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={trigger.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} disabled={!canSubmit} data-testid="trigger-submit">
            {trigger.isPending && <Loader2 className="animate-spin" />}
            Trigger
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
