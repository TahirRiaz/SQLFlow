import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { format } from "date-fns";
import {
  Ban, BellPlus, BellRing, CalendarClock, CircleCheck, CircleMinus, CircleX, Clock3, Info, Loader2,
  RefreshCw, Send, SkipForward, TriangleAlert, type LucideIcon,
} from "lucide-react";
import { Link } from "react-router-dom";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  Sheet, SheetContent, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "../../api/client";
import { notificationApi } from "../../api/endpoints";
import type {
  MyNotificationOptions, NotificationDelivery, NotificationDigestFlow, NotificationDigestSummary,
  NotificationSubscription,
} from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CopyButton } from "../../components/CopyButton";
import { CorrelationError } from "../../components/CorrelationError";
import { DateRangeCalendar } from "../../components/DateRangeCalendar";
import { EmptyState } from "../../components/EmptyState";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { OutcomePill } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { parseUtc } from "../../lib/time";

type Channel = NotificationSubscription["channel"];
type Mode = NotificationSubscription["mode"];

const CHANNEL_LABELS: Record<Channel, string> = { email: "Email", slack: "Slack" };
const MODE_LABELS: Record<Mode, string> = { immediate: "Immediate", digest: "Digest" };

/** One error-to-text mapping for every toast on this page (the API's detail wins over a generic title). */
function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

/** Display names for the run event kinds; an unknown kind falls back to its raw identifier. */
const KIND_LABELS: Record<string, string> = {
  run_failed: "Run failed",
  run_cancelled: "Run cancelled",
  run_skipped: "Run skipped",
  assertion_failed: "Assertions failed",
};

function kindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? kind;
}

/** Formats a minute count for pacing text: "45 min", "6 h", "1 h 30 min". */
function formatMinutes(minutes: number): string {
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

function pacingText(subscription: NotificationSubscription): string {
  if (subscription.mode === "digest") {
    return `Digest every ${formatMinutes(subscription.digestIntervalMinutes)}`;
  }

  return subscription.cooldownMinutes === 0
    ? "Immediate"
    : `Immediate, ${formatMinutes(subscription.cooldownMinutes)} cooldown`;
}

/** Where messages go: the explicit target, or the channel's fallback (account email / Slack direct message). */
function destinationText(subscription: NotificationSubscription, userEmail: string | null): string {
  if (subscription.channel === "email") {
    if (subscription.emailAddress !== null && subscription.emailAddress !== "") {
      return subscription.emailAddress;
    }

    return userEmail !== null && userEmail !== "" ? `${userEmail} (account email)` : "Account email";
  }

  return subscription.slackTarget !== null && subscription.slackTarget !== ""
    ? subscription.slackTarget
    : "Direct message";
}

/** Parses a whole-minute text field; null when empty or not an integer. */
function parseMinutes(raw: string): number | null {
  const trimmed = raw.trim();
  if (trimmed === "") {
    return null;
  }

  const value = Number(trimmed);
  return Number.isInteger(value) ? value : null;
}

/** The delivery outcome pill (DESIGN.md 7.3): icon + label so color never carries the state alone. */
function DeliveryStatusBadge({ status }: { status: NotificationDelivery["status"] }) {
  const visual = status === "sent"
    ? { tone: "success" as const, icon: CircleCheck }
    : status === "failed"
      ? { tone: "destructive" as const, icon: CircleX }
      : { tone: "muted" as const, icon: Clock3 };
  return <OutcomePill tone={visual.tone} label={status} icon={visual.icon} testId="delivery-status" />;
}

/** The one create/edit form, in a right side sheet: creating picks a channel, editing keeps it locked. */
function SubscriptionSheet({
  options,
  subscription,
  onClose,
}: {
  options: MyNotificationOptions;
  /** Null creates a new subscription; otherwise the sheet edits this one. */
  subscription: NotificationSubscription | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();

  const [channel, setChannel] = useState<Channel>(
    subscription?.channel ?? (options.email.available ? "email" : "slack"),
  );
  const [mode, setMode] = useState<Mode>(subscription?.mode ?? "immediate");
  const [kinds, setKinds] = useState<string[]>(subscription?.kinds ?? options.defaultKinds);
  const [flowPattern, setFlowPattern] = useState(subscription?.flowPattern ?? "");
  const [destination, setDestination] = useState(
    subscription === null
      ? ""
      : (subscription.channel === "email" ? subscription.emailAddress : subscription.slackTarget) ?? "",
  );
  const [cooldownText, setCooldownText] = useState(
    String(subscription?.cooldownMinutes ?? options.defaultCooldownMinutes),
  );
  const [digestText, setDigestText] = useState(
    String(subscription?.digestIntervalMinutes ?? options.defaultDigestIntervalMinutes),
  );

  // Only configured channels are offered; an existing subscription keeps its locked channel listed even if the
  // deployment no longer configures that channel.
  const channelOptions: Channel[] = [];
  if (options.email.available) {
    channelOptions.push("email");
  }
  if (options.slack.available) {
    channelOptions.push("slack");
  }
  if (subscription !== null && !channelOptions.includes(subscription.channel)) {
    channelOptions.push(subscription.channel);
  }

  const knownModes = options.modes.filter((m): m is Mode => m === "immediate" || m === "digest");
  const modeOptions: Mode[] = knownModes.length > 0 ? knownModes : ["immediate", "digest"];

  // The server's kinds plus any the subscription already carries, so nothing becomes un-toggleable.
  const kindOptions = Array.from(new Set([...options.kinds, ...(subscription?.kinds ?? [])]));

  const save = useMutation({
    mutationFn: () => {
      const pattern = flowPattern.trim();
      const target = destination.trim();
      const cooldown = parseMinutes(cooldownText);
      const digest = parseMinutes(digestText);
      if (subscription === null) {
        return notificationApi.createSubscription({
          channel,
          mode,
          kinds,
          flowPattern: pattern === "" ? null : pattern,
          emailAddress: channel === "email" && target !== "" ? target : null,
          slackTarget: channel === "slack" && target !== "" ? target : null,
          digestIntervalMinutes: mode === "digest" && digest !== null ? digest : undefined,
          cooldownMinutes: mode === "immediate" && cooldown !== null ? cooldown : undefined,
        });
      }

      // PUT semantics: an empty string clears flowPattern/emailAddress/slackTarget, so the saved state always
      // matches exactly what the sheet shows.
      return notificationApi.updateSubscription(subscription.id, {
        mode,
        kinds,
        flowPattern: pattern,
        emailAddress: channel === "email" ? target : undefined,
        slackTarget: channel === "slack" ? target : undefined,
        digestIntervalMinutes: mode === "digest" && digest !== null ? digest : undefined,
        cooldownMinutes: mode === "immediate" && cooldown !== null ? cooldown : undefined,
      });
    },
    onSuccess: () => {
      toast.success(subscription === null ? "Subscription created." : "Subscription updated.");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
      onClose();
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const toggleKind = (kind: string) =>
    setKinds((current) =>
      current.includes(kind) ? current.filter((k) => k !== kind) : [...current, kind]);

  const cooldown = parseMinutes(cooldownText);
  const cooldownValid = cooldown !== null && cooldown >= 0 && cooldown <= 1440;
  const digest = parseMinutes(digestText);
  const digestValid = digest !== null && digest >= 5 && digest <= 10080;
  const canSubmit = kinds.length > 0 && (mode === "immediate" ? cooldownValid : digestValid) && !save.isPending;

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !save.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-xl" data-testid="create-subscription-dialog">
        <SheetHeader>
          <SheetTitle>{subscription === null ? "New subscription" : "Edit subscription"}</SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          {save.isError && isApiError(save.error) && <CorrelationError error={save.error} />}
          {save.isError && !isApiError(save.error) && (
            <p className="text-[13px] text-destructive">{String(save.error)}</p>
          )}

          <div className="flex flex-col gap-1.5">
            <Label>Channel</Label>
            <Select
              value={channel}
              onValueChange={(value) => {
                setChannel(value as Channel);
                setDestination("");
              }}
              disabled={subscription !== null}
            >
              <SelectTrigger size="sm" className="h-8 w-full" data-testid="create-subscription-channel">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {channelOptions.map((option) => (
                  <SelectItem key={option} value={option}>{CHANNEL_LABELS[option]}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label>Mode</Label>
            <Select value={mode} onValueChange={(value) => setMode(value as Mode)}>
              <SelectTrigger size="sm" className="h-8 w-full" data-testid="create-subscription-mode">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {modeOptions.map((option) => (
                  <SelectItem key={option} value={option}>{MODE_LABELS[option]}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          {mode === "immediate" ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="create-subscription-cooldown">Cooldown (minutes)</Label>
              <Input
                id="create-subscription-cooldown"
                type="number"
                min={0}
                max={1440}
                value={cooldownText}
                onChange={(e) => setCooldownText(e.target.value)}
                aria-invalid={!cooldownValid || undefined}
                className="h-8"
                data-testid="create-subscription-cooldown"
              />
              <p className={cooldownValid ? "text-xs text-muted-foreground" : "text-xs text-destructive"}>
                {cooldownValid
                  ? "Minimum gap between messages; 0 sends every event."
                  : "Enter a whole number of minutes between 0 and 1440."}
              </p>
            </div>
          ) : (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="create-subscription-digest-interval">Digest interval (minutes)</Label>
              <Input
                id="create-subscription-digest-interval"
                type="number"
                min={5}
                max={10080}
                value={digestText}
                onChange={(e) => setDigestText(e.target.value)}
                aria-invalid={!digestValid || undefined}
                className="h-8"
                data-testid="create-subscription-digest-interval"
              />
              <p className={digestValid ? "text-xs text-muted-foreground" : "text-xs text-destructive"}>
                {digestValid
                  ? "How often bundled events are sent."
                  : "Enter a whole number of minutes between 5 and 10080."}
              </p>
            </div>
          )}

          <div className="flex flex-col gap-1.5">
            <Label>Events</Label>
            <div className="flex flex-col gap-2 pt-1">
              {kindOptions.map((kind) => (
                <Label key={kind} className="flex items-center gap-2 text-[13px] font-normal">
                  <Checkbox
                    checked={kinds.includes(kind)}
                    onCheckedChange={() => toggleKind(kind)}
                    data-testid={`create-subscription-kind-${kind}`}
                  />
                  {kindLabel(kind)}
                </Label>
              ))}
            </div>
            {kinds.length === 0 && (
              <p className="text-xs text-destructive">Select at least one event.</p>
            )}
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="create-subscription-flow-pattern">Flow pattern</Label>
            <Input
              id="create-subscription-flow-pattern"
              placeholder="e.g. sales_*, finance_* (empty = all flows)"
              value={flowPattern}
              onChange={(e) => setFlowPattern(e.target.value)}
              className="h-8 font-mono text-[12px]"
              data-testid="create-subscription-flow-pattern"
            />
          </div>

          {channel === "email" ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="create-subscription-destination">Email address</Label>
              <Input
                id="create-subscription-destination"
                value={destination}
                onChange={(e) => setDestination(e.target.value)}
                className="h-8"
                data-testid="create-subscription-destination"
              />
              <p className="text-xs text-muted-foreground">Leave empty to use your account email</p>
            </div>
          ) : (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="create-subscription-destination">Slack channel</Label>
              <Input
                id="create-subscription-destination"
                placeholder="Channel id, e.g. C0123ABCD"
                value={destination}
                onChange={(e) => setDestination(e.target.value)}
                className="h-8 font-mono text-[12px]"
                data-testid="create-subscription-destination"
              />
              <p className="text-xs text-muted-foreground">Leave empty to be direct-messaged</p>
            </div>
          )}
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={save.isPending}
            data-testid="create-subscription-cancel"
          >
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => save.mutate()}
            disabled={!canSubmit}
            data-testid="create-subscription-submit"
          >
            {save.isPending && <Loader2 className="animate-spin" />}
            {subscription === null ? "Create" : "Save"}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** A UTC instant as a compact wall clock, for the two ends of a digest period. */
function stamp(value: string): string {
  return format(parseUtc(value), "MMM d, HH:mm");
}

/** Where a digest came from: the periodic generator, or the person who asked for one. */
function originText(summary: NotificationDigestSummary): string {
  if (summary.origin === "scheduled") {
    return "Scheduled";
  }

  return summary.generatedBy === null ? "Manual" : `Manual, by ${summary.generatedBy}`;
}

/** The tone, word, and glyph one notification event kind wears, in the same language as a run's own status. */
const KIND_VISUALS: Record<string, { tone: "destructive" | "warning" | "muted"; label: string; icon: LucideIcon }> = {
  run_failed: { tone: "destructive", label: "failed", icon: CircleX },
  assertion_failed: { tone: "warning", label: "assertions", icon: TriangleAlert },
  run_cancelled: { tone: "muted", label: "cancelled", icon: Ban },
  run_skipped: { tone: "muted", label: "skipped", icon: SkipForward },
};

/** One flow row's outcome, with its repeat count folded into the word: "failed", "failed x12". */
function KindPill({ kind, count }: { kind: string; count: number }) {
  const visual = KIND_VISUALS[kind] ?? { tone: "muted" as const, label: kind, icon: CircleMinus };
  return (
    <OutcomePill
      tone={visual.tone}
      label={count > 1 ? `${visual.label} x${count}` : visual.label}
      icon={visual.icon}
      testId="digest-kind"
    />
  );
}

/**
 * The per-kind tally of one digest. Every chip carries its own noun, because four bare integers in a row cannot
 * be attributed while the eye is on the row (DESIGN.md 7.8); zero-count kinds are left out entirely so a quiet
 * window reads as quiet.
 */
function DigestBreakdown({ summary }: { summary: NotificationDigestSummary }) {
  const parts: { key: string; label: string; className: string }[] = [];
  if (summary.failedCount > 0) {
    parts.push({ key: "failed", label: `${summary.failedCount} failed`, className: "text-destructive" });
  }
  if (summary.assertionFailedCount > 0) {
    parts.push({
      key: "assertions",
      label: `${summary.assertionFailedCount} assertions`,
      className: "text-warning",
    });
  }
  if (summary.cancelledCount > 0) {
    parts.push({ key: "cancelled", label: `${summary.cancelledCount} cancelled`, className: "" });
  }
  if (summary.skippedCount > 0) {
    parts.push({ key: "skipped", label: `${summary.skippedCount} skipped`, className: "" });
  }

  if (parts.length === 0) {
    return <span className="text-[13px] text-muted-foreground">All clear</span>;
  }

  return (
    <div className="flex flex-wrap gap-1">
      {parts.map((part) => (
        <Badge key={part.key} variant="outline" className={part.className}>{part.label}</Badge>
      ))}
    </div>
  );
}

/**
 * The headline numbers of a digest (DESIGN.md 7.7). Each tile is a distinct dimension: a total-events tile would
 * repeat the failed count on every digest where nothing but failures happened, which is most of them. The total
 * still reads, as the caption on the count that matters for triage: how many flows need attention.
 */
function DigestKpis({ summary }: { summary: NotificationDigestSummary }) {
  const quiet = summary.cancelledCount + summary.skippedCount;
  const quietParts = [
    summary.skippedCount > 0 ? `${summary.skippedCount} skipped` : null,
    summary.cancelledCount > 0 ? `${summary.cancelledCount} cancelled` : null,
  ].filter((part): part is string => part !== null);

  return (
    <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
      <KpiCard
        label="Flows"
        value={summary.flowCount}
        caption={`${summary.eventCount} ${summary.eventCount === 1 ? "event" : "events"} in total`}
        testId="digest-kpi-flows"
      />
      <KpiCard
        label="Failed runs"
        value={summary.failedCount}
        color={summary.failedCount > 0 ? "error" : undefined}
        testId="digest-kpi-failed"
      />
      <KpiCard
        label="Assertions"
        value={summary.assertionFailedCount}
        color={summary.assertionFailedCount > 0 ? "warning" : undefined}
        testId="digest-kpi-assertions"
      />
      <KpiCard
        label="Cancelled + skipped"
        value={quiet}
        caption={quietParts.length > 0 ? quietParts.join(", ") : undefined}
        testId="digest-kpi-quiet"
      />
    </div>
  );
}

/** The cap the server applies when it persists a digest's per-flow rows; the GUI says so rather than implying none. */
const DIGEST_MAX_FLOW_ROWS = 200;

const DIGEST_FLOW_HEADERS = ["Flow", "Outcome", "Last", "Error", ""];

/** The digest's own table: one row per flow and outcome, newest and most alarming first, each opening its run. */
function DigestFlows({ flows, unlisted }: { flows: NotificationDigestFlow[]; unlisted: number }) {
  if (flows.length === 0) {
    return (
      <EmptyState
        icon={<CircleCheck />}
        title="Nothing failed in this period"
        description="Every run either succeeded or is still going."
      />
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="digest-flows">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {DIGEST_FLOW_HEADERS.map((header) => (
                <TableHead
                  key={header}
                  className="h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground"
                >
                  {header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {flows.map((flow) => (
              <TableRow key={`${flow.flowName}-${flow.kind}`} data-testid="digest-flow-row">
                <TableCell className="max-w-[260px] px-3 py-1.5">
                  <div className="flex flex-col">
                    {/* The flow page answers "is this the eighth failure in a row"; the run answers "why". */}
                    <Link to={`/pipelines/${flow.pipelineId}`} className="text-primary hover:underline">
                      <TruncatedText text={flow.flowName} maxWidth={260} mono />
                    </Link>
                    <span className="text-[11px] text-muted-foreground">{flow.flowKind}</span>
                  </div>
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5">
                  <KindPill kind={flow.kind} count={flow.count} />
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  <RelativeTime value={flow.lastOccurredUtc} />
                </TableCell>
                <TableCell className="max-w-[280px] px-3 py-1.5 text-[13px]">
                  {flow.lastError === null || flow.lastError === "" ? (
                    <span className="text-muted-foreground">-</span>
                  ) : (
                    <TruncatedText text={flow.lastError} maxWidth={280} />
                  )}
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-right">
                  <Button variant="ghost" size="xs" asChild data-testid="digest-flow-run">
                    <Link to={`/runs/${flow.lastRunId}`}>Open run</Link>
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>
      {unlisted > 0 && (
        <p className="text-xs text-muted-foreground">
          {`${unlisted} further event${unlisted === 1 ? "" : "s"} in this window are counted above but not listed: a digest tabulates its ${DIGEST_MAX_FLOW_ROWS} most alarming flows.`}
        </p>
      )}
    </div>
  );
}

/**
 * One digest opened for reading: its headline numbers, the per-flow table behind them, and the message exactly as
 * it would be delivered. The delivered text is a second view rather than the main one, because the reader here is
 * an operator triaging flows, not an inbox; it stays available because it is what actually goes out, and checking
 * it before pressing Send is the point of having it.
 */
function DigestSheet({
  digestId,
  userEmail,
  subscriptions,
  onClose,
}: {
  digestId: string;
  userEmail: string | null;
  subscriptions: NotificationSubscription[];
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [subscriptionId, setSubscriptionId] = useState(subscriptions[0]?.id ?? "");
  const [view, setView] = useState<"flows" | "message">("flows");

  const digestQuery = useQuery({
    queryKey: ["notification-digests", digestId],
    queryFn: () => notificationApi.getDigest(digestId),
  });

  const send = useMutation({
    mutationFn: () => notificationApi.sendDigest(digestId, { subscriptionId }),
    onSuccess: () => {
      toast.success("Digest queued for delivery.");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications", "deliveries"] });
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const digest = digestQuery.data;
  const listed = digest === undefined ? 0 : digest.flows.reduce((total, flow) => total + flow.count, 0);
  const unlisted = digest === undefined ? 0 : Math.max(0, digest.summary.eventCount - listed);

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !send.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-xl" data-testid="digest-dialog">
        <SheetHeader className="gap-1">
          <SheetTitle className="pr-6 text-[15px]">
            {digest === undefined ? "Digest" : digest.summary.subject}
          </SheetTitle>
          {digest !== undefined && (
            <p className="text-xs text-muted-foreground">
              <span className="font-mono tabular-nums">
                {`${stamp(digest.summary.periodStartUtc)} to ${stamp(digest.summary.periodEndUtc)}`}
              </span>
              {` · ${originText(digest.summary)}`}
            </p>
          )}
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4">
          {digestQuery.isError && (
            isApiError(digestQuery.error)
              ? <CorrelationError error={digestQuery.error} />
              : <p className="text-[13px] text-destructive">{errorText(digestQuery.error)}</p>
          )}
          {digest === undefined && !digestQuery.isError && (
            <>
              <Skeleton className="h-16 w-full" />
              <Skeleton className="h-64 w-full" />
            </>
          )}
          {digest !== undefined && (
            <>
              <DigestKpis summary={digest.summary} />

              {digest.summary.truncated && (
                <Alert>
                  <Info />
                  <AlertDescription>
                    The window held more events than one digest covers. The remainder is carried into the next
                    scheduled digest rather than dropped.
                  </AlertDescription>
                </Alert>
              )}

              <Tabs value={view} onValueChange={(next) => setView(next as "flows" | "message")}>
                <TabsList>
                  <TabsTrigger value="flows" data-testid="digest-tab-flows">Flows</TabsTrigger>
                  <TabsTrigger value="message" data-testid="digest-tab-message">Delivered message</TabsTrigger>
                </TabsList>
                <TabsContent value="flows" className="mt-3">
                  <DigestFlows flows={digest.flows} unlisted={unlisted} />
                </TabsContent>
                <TabsContent value="message" className="mt-3">
                  <pre
                    className="max-h-[55vh] overflow-auto rounded-md border bg-muted/40 p-3 font-mono text-[12px] leading-5 whitespace-pre-wrap"
                    data-testid="digest-body"
                  >
                    {digest.textBody}
                  </pre>
                </TabsContent>
              </Tabs>
            </>
          )}
        </div>
        <SheetFooter className="flex-row items-center justify-end gap-2">
          {digest !== undefined && (
            <CopyButton label="Copy" text={digest.textBody} testId="digest-copy" />
          )}
          {subscriptions.length > 0 ? (
            <>
              <Select value={subscriptionId} onValueChange={setSubscriptionId}>
                <SelectTrigger size="sm" className="h-8 w-56" data-testid="digest-send-subscription">
                  <SelectValue placeholder="Send through..." />
                </SelectTrigger>
                <SelectContent>
                  {subscriptions.map((subscription) => (
                    <SelectItem key={subscription.id} value={subscription.id}>
                      {`${CHANNEL_LABELS[subscription.channel]}: ${destinationText(subscription, userEmail)}`}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Button
                size="sm"
                onClick={() => send.mutate()}
                disabled={digest === undefined || subscriptionId === "" || send.isPending}
                data-testid="digest-send"
              >
                {send.isPending ? <Loader2 className="animate-spin" /> : <Send />}
                Send
              </Button>
            </>
          ) : (
            <span className="text-xs text-muted-foreground">
              Create a subscription above to send a digest to email or Slack.
            </span>
          )}
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

const SUBSCRIPTION_HEADERS = ["Channel", "Pacing", "Events", "Flows", "Destination", "Enabled", "Last sent", "Actions"];
const DELIVERY_HEADERS = ["Subject", "Channel", "Target", "Status", "Attempts", "Created", "Error"];
const DIGEST_HEADERS = ["Subject", "Period", "Events", "Flows", "Breakdown", "Origin", "Generated", ""];
const DIGEST_RIGHT_ALIGNED = new Set(["Events", "Flows", ""]);

/**
 * One local calendar day as the picker's two datetime-local edges. Seeded to yesterday: it is the last day the
 * estate has fully lived through, and so the one a daily digest is actually about.
 */
function lastCompleteDay(): { from: string; to: string } {
  const day = new Date();
  day.setDate(day.getDate() - 1);
  const stamp = format(day, "yyyy-MM-dd");
  return { from: `${stamp}T00:00`, to: `${stamp}T23:59` };
}

/**
 * A picked datetime-local edge as the UTC instant the API takes. The picker resolves to the minute, so "to 23:59"
 * means through the end of that minute: the end edge is extended to :59.999 so a day covers all of its last
 * minute instead of silently dropping it.
 */
function edgeToUtc(local: string, edge: "from" | "to"): string {
  const at = new Date(local);
  if (edge === "from") {
    at.setSeconds(0, 0);
  } else {
    at.setSeconds(59, 999);
  }

  return at.toISOString();
}

/** Skeleton rows for one of the hand-rolled tables while its query loads. */
function SkeletonRows({ headers, count }: { headers: string[]; count: number }) {
  return (
    <>
      {Array.from({ length: count }, (_, i) => (
        <TableRow key={`skeleton-${i}`}>
          {headers.map((header) => (
            <TableCell key={header} className="px-3 py-2">
              <Skeleton className="h-4 w-full" />
            </TableCell>
          ))}
        </TableRow>
      ))}
    </>
  );
}

/**
 * Self-service notification subscriptions: email or Slack messages when the caller's flows fail, are cancelled
 * or skipped, or fail assertions, sent either immediately (with a cooldown) or bundled into a digest.
 */
export default function NotificationsPage() {
  const queryClient = useQueryClient();

  const [sheet, setSheet] = useState<{ subscription: NotificationSubscription | null } | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<NotificationSubscription | null>(null);
  const [openDigestId, setOpenDigestId] = useState<string | null>(null);
  const [digestPeriod, setDigestPeriod] = useState(lastCompleteDay);

  const optionsQuery = useQuery({ queryKey: ["me-notifications", "options"], queryFn: notificationApi.options });
  const subscriptionsQuery = useQuery({
    queryKey: ["me-notifications", "subscriptions"],
    queryFn: notificationApi.listSubscriptions,
  });
  const deliveriesQuery = useQuery({
    queryKey: ["me-notifications", "deliveries"],
    queryFn: () => notificationApi.listDeliveries(50),
  });
  const digestsQuery = useQuery({
    queryKey: ["notification-digests", "list"],
    queryFn: () => notificationApi.listDigests(50),
  });

  const options = optionsQuery.data;
  const subscriptions = subscriptionsQuery.data;
  const deliveries = deliveriesQuery.data;
  const digests = digestsQuery.data;
  const periodChosen = digestPeriod.from !== "" && digestPeriod.to !== "";
  const channelAvailable = options !== undefined && (options.email.available || options.slack.available);
  const notConfigured =
    options !== undefined && (!options.enabled || (!options.email.available && !options.slack.available));

  const toggleEnabled = useMutation({
    mutationFn: ({ subscription, enabled }: { subscription: NotificationSubscription; enabled: boolean }) =>
      notificationApi.updateSubscription(subscription.id, { enabled }),
    onSuccess: (updated) => {
      toast.success(updated.enabled ? "Subscription enabled." : "Subscription disabled.");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const test = useMutation({
    mutationFn: (subscription: NotificationSubscription) => notificationApi.testSubscription(subscription.id),
    onSuccess: () => {
      toast.success("Test message queued");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications", "deliveries"] });
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const generate = useMutation({
    mutationFn: () => notificationApi.generateDigest({
      fromUtc: edgeToUtc(digestPeriod.from, "from"),
      toUtc: edgeToUtc(digestPeriod.to, "to"),
    }),
    onSuccess: (digest) => {
      toast.success(
        digest.summary.eventCount === 0
          ? "Digest generated: nothing failed in that window."
          : `Digest generated: ${digest.summary.eventCount} event(s) across ${digest.summary.flowCount} flow(s).`);
      void queryClient.invalidateQueries({ queryKey: ["notification-digests"] });
      // Generating is how a reader asks to SEE one, so open it rather than leaving them to find the row.
      setOpenDigestId(digest.summary.id);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const remove = useMutation({
    mutationFn: (subscription: NotificationSubscription) => notificationApi.deleteSubscription(subscription.id),
    onSuccess: () => {
      toast.success("Subscription deleted.");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
      setDeleteTarget(null);
    },
    onError: (error) => {
      toast.error(errorText(error));
      setDeleteTarget(null);
    },
  });

  return (
    <Page data-testid="page-notifications">
      <PageHeader
        title="Notifications"
        subtitle="Get an email or Slack message when your flows fail, are cancelled or skipped, or fail assertions. Immediate subscriptions respect a per-subscription cooldown; digest subscriptions bundle events on an interval. Estate digests below are written on a schedule whether or not anyone subscribes, and can be generated on demand."
        actions={(
          <Button
            size="sm"
            onClick={() => setSheet({ subscription: null })}
            disabled={!channelAvailable}
            data-testid="open-create-subscription"
          >
            <BellPlus />
            New subscription
          </Button>
        )}
      />

      {notConfigured && (
        <Alert>
          <Info />
          <AlertDescription>
            No notification channel is configured on this deployment, so no messages can be sent. An administrator
            must configure <code className="font-mono text-[12px]">ControlPlane:Notifications:Email</code> or{" "}
            <code className="font-mono text-[12px]">ControlPlane:Notifications:Slack</code> before subscriptions
            deliver anything; existing subscriptions remain saved below.
          </AlertDescription>
        </Alert>
      )}

      {optionsQuery.isError && (
        isApiError(optionsQuery.error)
          ? <CorrelationError error={optionsQuery.error} />
          : <p className="text-[13px] text-destructive">{errorText(optionsQuery.error)}</p>
      )}

      {subscriptionsQuery.isError && (
        isApiError(subscriptionsQuery.error)
          ? <CorrelationError error={subscriptionsQuery.error} />
          : <p className="text-[13px] text-destructive">{errorText(subscriptionsQuery.error)}</p>
      )}

      {/* Hand-rolled on the ui table primitives (not DataTable) so each row keeps its `subscription-row` testid. */}
      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {SUBSCRIPTION_HEADERS.map((header, i) => (
                <TableHead
                  key={header}
                  className={`h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground${i === SUBSCRIPTION_HEADERS.length - 1 ? " text-right" : ""}`}
                >
                  {header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {subscriptions === undefined && !subscriptionsQuery.isError && (
              <SkeletonRows headers={SUBSCRIPTION_HEADERS} count={2} />
            )}
            {subscriptions !== undefined && subscriptions.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={SUBSCRIPTION_HEADERS.length} className="border-0 p-0">
                  <EmptyState
                    icon={<BellRing />}
                    title="You have no notification subscriptions yet"
                    description="Subscribe to be told when your flows fail, are cancelled or skipped, or fail assertions."
                    action={channelAvailable ? (
                      <Button size="sm" variant="outline" onClick={() => setSheet({ subscription: null })}>
                        New subscription
                      </Button>
                    ) : undefined}
                  />
                </TableCell>
              </TableRow>
            )}
            {subscriptions?.map((subscription) => (
              <TableRow key={subscription.id} data-testid="subscription-row">
                <TableCell className="whitespace-nowrap px-3 py-1.5">
                  <Badge variant="outline">{CHANNEL_LABELS[subscription.channel]}</Badge>
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  {pacingText(subscription)}
                </TableCell>
                <TableCell className="px-3 py-1.5">
                  <div className="flex flex-wrap gap-1">
                    {subscription.kinds.map((kind) => (
                      <Badge key={kind} variant="outline">{kindLabel(kind)}</Badge>
                    ))}
                  </div>
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  {subscription.flowPattern === null || subscription.flowPattern === "" ? (
                    <span className="text-muted-foreground">All flows</span>
                  ) : (
                    <code className="font-mono text-[12px]">{subscription.flowPattern}</code>
                  )}
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  {destinationText(subscription, options?.userEmail ?? null)}
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5">
                  <Switch
                    checked={subscription.enabled}
                    onCheckedChange={(enabled) => toggleEnabled.mutate({ subscription, enabled })}
                    disabled={toggleEnabled.isPending}
                    aria-label="Enabled"
                    data-testid="subscription-enabled"
                  />
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  <RelativeTime value={subscription.lastSentUtc} />
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-right">
                  <div className="flex justify-end gap-0.5">
                    <Button
                      variant="ghost"
                      size="xs"
                      onClick={() => test.mutate(subscription)}
                      disabled={test.isPending}
                      data-testid="subscription-test"
                    >
                      {test.isPending && test.variables?.id === subscription.id && (
                        <Loader2 className="animate-spin" />
                      )}
                      Test
                    </Button>
                    <Button
                      variant="ghost"
                      size="xs"
                      onClick={() => setSheet({ subscription })}
                      disabled={options === undefined}
                      data-testid="subscription-edit"
                    >
                      Edit
                    </Button>
                    <Button
                      variant="ghost"
                      size="xs"
                      className="text-destructive hover:text-destructive"
                      onClick={() => setDeleteTarget(subscription)}
                      data-testid="subscription-delete"
                    >
                      Delete
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>

      <div className="flex flex-col gap-2">
        <div>
          <h2 className="text-base font-medium">Estate digests</h2>
          <p className="text-[13px] text-muted-foreground">
            {options?.estateDigest.enabled === false
              ? "Periodic generation is turned off on this deployment, so digests appear here only when someone asks for one."
              : `One digest is written every ${formatMinutes(options?.estateDigest.intervalMinutes ?? 1440)}, whether or not anyone subscribes to it. To read any period now, pick it below: click one day twice for that day alone, or two days for the span between them.`}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {/* A day is that day on both edges; the same picker also takes a week or a month when asked. */}
          <div className="w-[19rem]">
            <DateRangeCalendar
              from={digestPeriod.from}
              to={digestPeriod.to}
              onChange={(from, to) => setDigestPeriod({ from, to })}
              disabled={generate.isPending}
              testId="digest-period"
            />
          </div>
          <Button
            size="sm"
            variant="outline"
            onClick={() => generate.mutate()}
            disabled={generate.isPending || options === undefined || !periodChosen}
            data-testid="generate-digest"
          >
            {generate.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />}
            Generate
          </Button>
          {!periodChosen && (
            <span className="text-xs text-muted-foreground">Pick a day to generate its digest.</span>
          )}
        </div>

        {digestsQuery.isError && (
          isApiError(digestsQuery.error)
            ? <CorrelationError error={digestsQuery.error} />
            : <p className="text-[13px] text-destructive">{errorText(digestsQuery.error)}</p>
        )}

        <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="digests-table">
          <Table>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                {DIGEST_HEADERS.map((header) => (
                  <TableHead
                    key={header}
                    className={`h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground${DIGEST_RIGHT_ALIGNED.has(header) ? " text-right" : ""}`}
                  >
                    {header}
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {digests === undefined && !digestsQuery.isError && (
                <SkeletonRows headers={DIGEST_HEADERS} count={3} />
              )}
              {digests !== undefined && digests.length === 0 && (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={DIGEST_HEADERS.length} className="border-0 p-0">
                    <EmptyState
                      icon={<CalendarClock />}
                      title="No digests yet"
                      description="The control plane writes one each period. You can also generate one for any day above."
                      action={(
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => generate.mutate()}
                          disabled={generate.isPending || options === undefined || !periodChosen}
                        >
                          Generate
                        </Button>
                      )}
                    />
                  </TableCell>
                </TableRow>
              )}
              {digests?.map((digest) => (
                <TableRow
                  key={digest.id}
                  data-testid="digest-row"
                  className="cursor-pointer"
                  onClick={() => setOpenDigestId(digest.id)}
                >
                  <TableCell className="max-w-[320px] px-3 py-1.5 text-[13px]">
                    <TruncatedText text={digest.subject} maxWidth={320} />
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 font-mono text-[12px] tabular-nums">
                    {`${stamp(digest.periodStartUtc)} to ${stamp(digest.periodEndUtc)}`}
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-right font-mono text-[13px] tabular-nums">
                    {digest.eventCount}
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-right font-mono text-[13px] tabular-nums">
                    {digest.flowCount}
                  </TableCell>
                  <TableCell className="px-3 py-1.5">
                    <DigestBreakdown summary={digest} />
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">{originText(digest)}</TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                    <RelativeTime value={digest.generatedUtc} />
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-right">
                    <Button
                      variant="ghost"
                      size="xs"
                      onClick={(event) => {
                        event.stopPropagation();
                        setOpenDigestId(digest.id);
                      }}
                      data-testid="digest-open"
                    >
                      Open
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      </div>

      <div className="flex flex-col gap-2">
        <h2 className="text-base font-medium">Recent deliveries</h2>
        {deliveriesQuery.isError && (
          isApiError(deliveriesQuery.error)
            ? <CorrelationError error={deliveriesQuery.error} />
            : <p className="text-[13px] text-destructive">{errorText(deliveriesQuery.error)}</p>
        )}
        <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="deliveries-table">
          <Table>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                {DELIVERY_HEADERS.map((header) => (
                  <TableHead key={header} className="h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground">
                    {header}
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {deliveries === undefined && !deliveriesQuery.isError && (
                <SkeletonRows headers={DELIVERY_HEADERS} count={3} />
              )}
              {deliveries !== undefined && deliveries.length === 0 && (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={DELIVERY_HEADERS.length} className="border-0 p-0">
                    <EmptyState title="No messages have been sent yet." />
                  </TableCell>
                </TableRow>
              )}
              {deliveries?.map((delivery) => (
                <TableRow key={delivery.id} data-testid="delivery-row">
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">{delivery.subject}</TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <Badge variant="outline">{CHANNEL_LABELS[delivery.channel]}</Badge>
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">{delivery.target}</TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <DeliveryStatusBadge status={delivery.status} />
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <span className="font-mono text-[13px] tabular-nums">{delivery.attempts}</span>
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                    <RelativeTime value={delivery.createdUtc} />
                  </TableCell>
                  <TableCell className="max-w-[280px] px-3 py-1.5 text-[13px]">
                    {delivery.lastError === null || delivery.lastError === "" ? (
                      <span className="text-muted-foreground">-</span>
                    ) : (
                      <TruncatedText text={delivery.lastError} maxWidth={280} />
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      </div>

      {openDigestId !== null && (
        <DigestSheet
          digestId={openDigestId}
          userEmail={options?.userEmail ?? null}
          subscriptions={subscriptions ?? []}
          onClose={() => setOpenDigestId(null)}
        />
      )}

      {sheet !== null && options !== undefined && (
        <SubscriptionSheet
          options={options}
          subscription={sheet.subscription}
          onClose={() => setSheet(null)}
        />
      )}

      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete subscription"
        message={`Delete this ${deleteTarget === null ? "" : deleteTarget.channel === "email" ? "email " : "Slack "}subscription? You will stop receiving its messages immediately.`}
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => {
          if (deleteTarget !== null) {
            remove.mutate(deleteTarget);
          }
        }}
        onClose={() => setDeleteTarget(null)}
      />
    </Page>
  );
}
