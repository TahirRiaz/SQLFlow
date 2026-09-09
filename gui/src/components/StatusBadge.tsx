import {
  Ban,
  CircleCheck,
  CircleMinus,
  CircleX,
  Clock3,
  Loader2,
  Pause,
  Power,
  PowerOff,
  SkipForward,
  Wifi,
  WifiOff,
  type LucideIcon,
} from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { RunStatus } from "../api/types";

type Tone = "success" | "destructive" | "info" | "warning" | "muted";

/**
 * Which of the two status families a badge belongs to (DESIGN.md 7.3).
 *
 * `outcome` is something that ran and finished: a run status, a delivery result. It renders as a filled,
 * tinted circle.
 *
 * `state` is how an object is configured right now: a pipeline active or not, a schedule enabled/paused,
 * a node online. It renders as an outlined rounded square with an on/off style glyph.
 *
 * The two families must never share a silhouette. A green check on a green disc means "this run
 * succeeded" and nothing else; an object that is merely switched on says so with a power glyph in an
 * outlined chip, so the two stop reading as the same thing in a dense table.
 */
type Family = "outcome" | "state";

/** Filled tint for the outcome family: solid presence, because something actually happened. */
const outcomeToneClasses: Record<Tone, string> = {
  success: "bg-success/12 text-success",
  destructive: "bg-destructive/12 text-destructive",
  info: "bg-info/12 text-info",
  warning: "bg-warning/15 text-warning",
  muted: "bg-muted text-muted-foreground",
};

/** Outline for the state family: a hairline ring instead of a fill, so a setting never shouts like a result. */
const stateToneClasses: Record<Tone, string> = {
  success: "text-success ring-success/45",
  destructive: "text-destructive ring-destructive/45",
  info: "text-info ring-info/45",
  warning: "text-warning ring-warning/50",
  muted: "text-muted-foreground ring-border",
};

/** The surface of a badge: filled disc for outcomes, outlined chip for states. */
function familyClasses(family: Family, tone: Tone): string {
  return family === "state"
    ? cn("bg-transparent ring-1 ring-inset", stateToneClasses[tone])
    : outcomeToneClasses[tone];
}

/**
 * The one status pill (DESIGN.md 7.3): an icon plus its word, so color never carries the state alone.
 * Outcomes are a filled `rounded-full` pill; states are an outlined `rounded-md` chip.
 */
function Pill({
  tone,
  label,
  icon: Icon,
  family = "outcome",
  spin = false,
  testId,
}: {
  tone: Tone;
  label: string;
  icon: LucideIcon;
  family?: Family;
  spin?: boolean;
  testId: string;
}) {
  return (
    <span
      data-testid={testId}
      className={cn(
        "inline-flex items-center gap-1 whitespace-nowrap px-2 py-0.5 text-[11px] font-medium leading-4",
        family === "state" ? "rounded-md" : "rounded-full",
        familyClasses(family, tone),
      )}
    >
      <Icon className={cn("size-3.5 shrink-0", spin && "animate-spin")} />
      {label}
    </span>
  );
}

/** The tone and icon a run status renders with; the label doubles as the tooltip text and the accessible name. */
function runStatusVisual(status: RunStatus | string): { tone: Tone; label: string; icon: LucideIcon; spin?: boolean } {
  switch (status) {
    case "queued":
      return { tone: "warning", label: "queued", icon: Clock3 };
    case "running":
      return { tone: "info", label: "running", icon: Loader2, spin: true };
    case "succeeded":
      return { tone: "success", label: "succeeded", icon: CircleCheck };
    case "failed":
      return { tone: "destructive", label: "failed", icon: CircleX };
    case "cancelled":
      return { tone: "muted", label: "cancelled", icon: Ban };
    case "skipped":
      return { tone: "muted", label: "skipped", icon: SkipForward };
    default:
      return { tone: "muted", label: status, icon: CircleMinus };
  }
}

/**
 * A status rendered as the tinted icon alone: the word is redundant next to the icon (and next to a column
 * header that already names the state), so it moves to a hover tooltip and a visually hidden label. Icon shape
 * plus tone still carry the state without color alone (DESIGN.md 7.3), and the hidden label keeps the word
 * queryable in tests and available to assistive tech. Pass `family="state"` for a configuration state so it
 * cannot be mistaken for a run outcome.
 */
export function IconBadge({
  tone,
  label,
  icon: Icon,
  family = "outcome",
  spin = false,
  testId,
}: {
  tone: Tone;
  label: string;
  icon: LucideIcon;
  family?: Family;
  spin?: boolean;
  testId: string;
}) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          data-testid={testId}
          className={cn(
            "inline-flex size-5.5 shrink-0 items-center justify-center",
            family === "state" ? "rounded-[6px]" : "rounded-full",
            familyClasses(family, tone),
          )}
        >
          <Icon className={cn("size-3.5 shrink-0", spin && "animate-spin")} />
          <span className="sr-only">{label}</span>
        </span>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

/**
 * One mapping from run status to tone/affordance, used everywhere a run status renders. Renders through
 * {@link IconBadge}: only the tinted status icon, with the word on hover and for assistive tech.
 */
export function RunStatusBadge({ status, testId = "status-badge" }: { status: RunStatus | string; testId?: string }) {
  const { tone, label, icon: Icon, spin } = runStatusVisual(status);
  return <IconBadge tone={tone} label={label} icon={Icon} spin={spin} testId={testId} />;
}

/**
 * Worst-wins rollup of a group's run statuses into one headline status, so a collapsed group can show green at a
 * glance only when nothing failed and nothing is still pending. Precedence, most to least alarming:
 * failed > running > queued > succeeded > cancelled > skipped. A group with only benign trailing states
 * (skipped/cancelled) alongside successes still rolls up green, since nothing needs attention.
 */
export function rollupStatus(statuses: readonly (RunStatus | string)[]): RunStatus {
  const has = (s: RunStatus) => statuses.includes(s);
  if (has("failed")) {
    return "failed";
  }

  if (has("running")) {
    return "running";
  }

  if (has("queued")) {
    return "queued";
  }

  if (has("succeeded")) {
    return "succeeded";
  }

  return has("cancelled") ? "cancelled" : "skipped";
}

/** A node's reachability: a state, not a result, so it takes the outlined chip. */
export function OnlineBadge({ online }: { online: boolean }) {
  return online
    ? <Pill family="state" tone="success" label="online" icon={Wifi} testId="online-badge" />
    : <Pill family="state" tone="muted" label="offline" icon={WifiOff} testId="online-badge" />;
}

/**
 * Whether a pipeline, user, or integration is switched on. A power glyph in an outlined square, never the
 * check mark on a filled disc: in a table where the neighbouring column shows how the last run ended, an
 * "active" check and a "succeeded" check were the same picture and had to be read twice to tell apart.
 */
export function ActiveBadge({ active }: { active: boolean }) {
  return active
    ? <IconBadge family="state" tone="success" label="active" icon={Power} testId="active-badge" />
    : <IconBadge family="state" tone="muted" label="inactive" icon={PowerOff} testId="active-badge" />;
}

/** A schedule's own state, in the same on/off language as {@link ActiveBadge}: three states, three glyphs,
 * under a column header that already says "State", so spelling the word out again cost a column's width per
 * row and told the reader nothing the glyph did not. */
export function ScheduleStateBadge({ enabled, paused }: { enabled: boolean; paused: boolean }) {
  if (!enabled) {
    return <IconBadge family="state" tone="muted" label="disabled" icon={PowerOff} testId="schedule-badge" />;
  }

  return paused
    ? <IconBadge family="state" tone="warning" label="paused" icon={Pause} testId="schedule-badge" />
    : <IconBadge family="state" tone="success" label="enabled" icon={Power} testId="schedule-badge" />;
}

/**
 * Something that ran and finished, where the word matters as much as the glyph: a delivery result, a digest's
 * "failed x12". The filled-disc outcome family (DESIGN.md 7.3), exported for the same reason {@link StatePill}
 * is, so a feature never hand-rolls a tinted pill of its own.
 */
export function OutcomePill({
  tone,
  label,
  icon,
  testId,
}: {
  tone: Tone;
  label: string;
  icon: LucideIcon;
  testId: string;
}) {
  return <Pill family="outcome" tone={tone} label={label} icon={icon} testId={testId} />;
}

/**
 * A lifecycle state that is not a simple on/off (an access token that is active, expired, or revoked). Same
 * outlined-chip family as the badges above, exported so features stop hand-rolling their own pill and drifting
 * back onto the outcome check mark.
 */
export function StatePill({
  tone,
  label,
  icon,
  testId,
}: {
  tone: Tone;
  label: string;
  icon: LucideIcon;
  testId: string;
}) {
  return <Pill family="state" tone={tone} label={label} icon={icon} testId={testId} />;
}
