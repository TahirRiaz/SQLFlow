import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  Ban, CircleCheck, CircleSlash, CircleX, Clock3, GitBranch, KeyRound, Loader2, Power, PowerOff, type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import type {
  ExternalTriggerKind, TriggerRequest, TriggerRequestSchedule, TriggerRequestState, TriggerScheduleState, TriggerSyncOutcome,
} from "../../api/types";
import { IconBadge } from "../../components/StatusBadge";
import { usePanel } from "../../layout/workbench/PanelContext";
import { ActivityTracePanel } from "../activity/ActivityTracePanel";

/** One error-to-text mapping for every toast on the page (the API's detail wins over a generic title). */
export function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

/** The activity-trace kind a trigger request writes under (mirrors ActivityKinds.TriggerRequest server-side). */
const TRIGGER_REQUEST_KIND = "trigger-request";

type Tone = "success" | "destructive" | "info" | "warning" | "muted";

const requestVisuals: Record<TriggerRequestState, { tone: Tone; label: string; icon: LucideIcon; spin?: boolean }> = {
  syncing: { tone: "info", label: "syncing", icon: Loader2, spin: true },
  firing: { tone: "info", label: "firing", icon: Loader2, spin: true },
  running: { tone: "info", label: "running", icon: Loader2, spin: true },
  succeeded: { tone: "success", label: "succeeded", icon: CircleCheck },
  failed: { tone: "destructive", label: "failed", icon: CircleX },
  cancelled: { tone: "muted", label: "cancelled", icon: Ban },
};

/** Where a request stands, as the outcome badge every run status uses (DESIGN.md 7.3): the glyph, the word on hover. */
export function RequestStateBadge({ state, testId = "request-state" }: { state: TriggerRequestState; testId?: string }) {
  const visual = requestVisuals[state] ?? requestVisuals.running;
  return <IconBadge tone={visual.tone} label={visual.label} icon={visual.icon} spin={visual.spin} testId={testId} />;
}

/** Whether a trigger is accepted: a state, so the outlined on/off glyph, never the outcome check mark. */
export function TriggerStateBadge({ enabled }: { enabled: boolean }) {
  return enabled
    ? <IconBadge family="state" tone="success" label="enabled" icon={Power} testId="trigger-state" />
    : <IconBadge family="state" tone="muted" label="disabled" icon={PowerOff} testId="trigger-state" />;
}

const scheduleGlyphs: Record<TriggerScheduleState, { icon: LucideIcon; tone: string; spin?: boolean; word: string }> = {
  pending: { icon: Clock3, tone: "text-muted-foreground", word: "waiting for the sync" },
  firing: { icon: Loader2, tone: "text-info", spin: true, word: "firing" },
  running: { icon: Loader2, tone: "text-info", spin: true, word: "running" },
  succeeded: { icon: CircleCheck, tone: "text-success", word: "succeeded" },
  failed: { icon: CircleX, tone: "text-destructive", word: "did not succeed" },
  cancelled: { icon: Ban, tone: "text-muted-foreground", word: "cancelled" },
  "not-fired": { icon: CircleSlash, tone: "text-warning", word: "did not fire" },
};

/**
 * One schedule a request asked for: a neutral chip with its name, led by a glyph in the tone of how it went. Only the
 * glyph carries the tone, so a row of several schedules never reads as one block of color; why it ended as it did is
 * on hover.
 */
export function ScheduleChip({ schedule }: { schedule: TriggerRequestSchedule }) {
  const glyph = scheduleGlyphs[schedule.state] ?? scheduleGlyphs.pending;
  const Icon = glyph.icon;
  const counts = schedule.counts.total > 0
    ? ` ${schedule.counts.succeeded} of ${schedule.counts.total} flows succeeded.`
    : "";
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Badge
          variant="secondary"
          className="max-w-44 rounded-md border-border bg-muted/60 font-mono text-[11px] text-foreground"
          data-testid="request-schedule"
          data-state={schedule.state}
        >
          <Icon className={cn("size-3.5 shrink-0", glyph.tone, glyph.spin && "animate-spin")} aria-hidden />
          <span className="truncate">{schedule.name}</span>
          <span className="sr-only">{glyph.word}</span>
        </Badge>
      </TooltipTrigger>
      <TooltipContent className="max-w-sm">
        <span className="font-mono">{schedule.name}</span> {glyph.word}.{counts}
        {schedule.detail ? <> {schedule.detail}</> : null}
      </TooltipContent>
    </Tooltip>
  );
}

/** How a trigger proves who it is, as a glyph ahead of its name, with the words on hover and for assistive tech. */
export function KindGlyph({ kind }: { kind: ExternalTriggerKind }) {
  const Icon = kind === "github" ? GitBranch : KeyRound;
  const label = kind === "github" ? "Proves who it is with a GitHub workflow's token" : "Proves who it is with a key";
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex shrink-0 text-muted-foreground" data-testid="trigger-kind" data-kind={kind}>
          <Icon className="size-3.5" aria-hidden />
          <span className="sr-only">{label}</span>
        </span>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

/** How a request's sync went, in a few words. */
export function syncText(request: Pick<TriggerRequest, "sync" | "syncOutcome" | "syncRequestedUtc">): string {
  const outcomes: Record<TriggerSyncOutcome, string> = {
    current: "already synced",
    succeeded: "synced",
    failed: "sync failed",
    "timed-out": "sync timed out",
    skipped: "not asked for",
  };
  if (request.syncOutcome !== null) {
    return outcomes[request.syncOutcome] ?? request.syncOutcome;
  }

  if (!request.sync) {
    return "not asked for";
  }

  return request.syncRequestedUtc === null ? "waiting" : "syncing";
}

/** A commit as the tables show it: its first seven characters. */
export function shortSha(sha: string | null): string {
  return sha === null ? "" : sha.startsWith("worktree") ? "worktree" : sha.slice(0, 7);
}

/**
 * Opens the workbench bottom panel on a request's live trace (its sync's lines, each fire, each run as it starts and
 * ends), the same panel a repository's sync streams into; the request's lists refresh when it ends.
 */
export function useRequestTracePanel(): (request: Pick<TriggerRequest, "id" | "repoName">) => void {
  const panel = usePanel();
  const queryClient = useQueryClient();
  const [nonce, setNonce] = useState(0);

  return (request) => {
    const next = nonce + 1;
    setNonce(next);
    panel.open({
      id: `trigger-request:${request.id}`,
      title: `Request · ${request.repoName}`,
      node: (
        <ActivityTracePanel
          kind={TRIGGER_REQUEST_KIND}
          subject={request.id}
          nonce={next}
          onEnded={() => {
            void queryClient.invalidateQueries({ queryKey: ["trigger-requests"] });
            void queryClient.invalidateQueries({ queryKey: ["external-triggers"] });
          }}
        />
      ),
    });
  };
}
