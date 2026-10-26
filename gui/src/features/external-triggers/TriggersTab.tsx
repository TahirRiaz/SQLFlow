import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  Ellipsis, FileLock2, KeyRound, ListChecks, Pencil, Plug, Power, PowerOff, Trash2, Webhook,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { externalTriggerApi } from "../../api/endpoints";
import type { ExternalTrigger, ExternalTriggerWritten, TriggerRequestState } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { NoteRef } from "../../components/NoteRef";
import { RelativeTime } from "../../components/RelativeTime";
import { RichTooltip } from "../../components/RichTooltip";
import { TruncatedText } from "../../components/TruncatedText";
import { KindGlyph, RequestStateBadge, TriggerStateBadge, errorText } from "./triggerVisuals";

/** What a trigger trusts, in one line: for GitHub the repository, branch, workflow and environment; for a key its prefix. */
export function trustsText(trigger: ExternalTrigger): string {
  if (trigger.kind === "key") {
    return `${trigger.keyPrefix ?? "sqlft_"}...`;
  }

  const github = trigger.gitHub;
  const branch = github?.ref?.startsWith("refs/heads/") ? github.ref.slice("refs/heads/".length) : github?.ref ?? "";
  return [github?.repository, branch, github?.workflow?.split("/").at(-1) ?? "any workflow", github?.environment]
    .filter((part): part is string => !!part)
    .join(" · ");
}

/** The schedules a trigger may fire, as chips: the first few, and how many more on hover. */
function SchedulesCell({ trigger }: { trigger: ExternalTrigger }) {
  if (trigger.allSchedules) {
    return <span className="text-[13px]">every schedule</span>;
  }

  if (trigger.schedules.length === 0) {
    return <span className="text-[13px] text-muted-foreground">none: only syncs</span>;
  }

  const shown = trigger.schedules.slice(0, 1);
  const rest = trigger.schedules.slice(1);
  return (
    <div className="flex items-center gap-1">
      {shown.map((schedule) => (
        <Badge key={schedule} variant="outline" className="max-w-32 rounded-md font-mono text-[11px]">
          <span className="truncate">{schedule}</span>
        </Badge>
      ))}
      {rest.length > 0 && (
        <RichTooltip title="Also" body={rest.join("\n")} mono>
          <span className="text-[12px] text-muted-foreground">+{rest.length}</span>
        </RichTooltip>
      )}
    </div>
  );
}

/**
 * The triggers, one row each: whether it is accepted, its name, how it proves who it is and what it trusts, the
 * repository and schedules it may touch, when and by whom it was last used, and how its latest request went. A row's
 * actions edit it, show how to connect it, rotate its key, turn it on or off, list its requests, or remove it; a trigger
 * the configuration sets offers only what changes nothing.
 */
export function TriggersTab({
  triggers, loading, onCreate, onEdit, onConnect, onShowRequests, onOpenRequest,
}: {
  triggers: ExternalTrigger[] | undefined;
  loading: boolean;
  onCreate: () => void;
  onEdit: (trigger: ExternalTrigger) => void;
  onConnect: (trigger: ExternalTrigger, written: ExternalTriggerWritten | null) => void;
  onShowRequests: (trigger: ExternalTrigger) => void;
  onOpenRequest: (requestId: string, repoName: string) => void;
}) {
  const queryClient = useQueryClient();
  const [removeTarget, setRemoveTarget] = useState<ExternalTrigger | null>(null);
  const [rotateTarget, setRotateTarget] = useState<ExternalTrigger | null>(null);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: ["external-triggers"] });

  const setEnabled = useMutation({
    mutationFn: ({ trigger, enabled }: { trigger: ExternalTrigger; enabled: boolean }) =>
      enabled ? externalTriggerApi.enable(trigger.id) : externalTriggerApi.disable(trigger.id),
    onSuccess: (trigger) => {
      toast.success(trigger.enabled ? `Trigger "${trigger.name}" enabled.` : `Trigger "${trigger.name}" disabled: its next call is refused.`);
      refresh();
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const remove = useMutation({
    mutationFn: (trigger: ExternalTrigger) => externalTriggerApi.remove(trigger.id),
    onSuccess: (_result, trigger) => {
      toast.success(`Trigger "${trigger.name}" removed; its requests stay listed.`);
      setRemoveTarget(null);
      refresh();
    },
    onError: (error) => {
      toast.error(errorText(error));
      setRemoveTarget(null);
    },
  });

  const rotate = useMutation({
    mutationFn: (trigger: ExternalTrigger) => externalTriggerApi.rotateKey(trigger.id, trigger.keyExpiresUtc === null ? null : 365),
    onSuccess: (written) => {
      toast.success(`Trigger "${written.trigger.name}" has a new key; the old one no longer works.`);
      setRotateTarget(null);
      refresh();
      onConnect(written.trigger, written);
    },
    onError: (error) => {
      toast.error(errorText(error));
      setRotateTarget(null);
    },
  });

  const columns: Column<ExternalTrigger>[] = [
    { id: "state", header: "", width: 40, render: (t) => <TriggerStateBadge enabled={t.enabled} /> },
    {
      id: "name",
      header: "Name",
      render: (t) => (
        <span className="inline-flex items-center gap-1.5">
          <KindGlyph kind={t.kind} />
          <span className="font-mono text-[13px] font-medium">{t.name}</span>
          {t.configuredBy && (
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="inline-flex text-muted-foreground" data-testid="trigger-configured">
                  <FileLock2 className="size-3.5" aria-hidden />
                  <span className="sr-only">set by configuration</span>
                </span>
              </TooltipTrigger>
              <TooltipContent className="max-w-xs">
                Set by the control plane&apos;s configuration, <span className="font-mono">{t.configuredBy}</span>; change it there.
              </TooltipContent>
            </Tooltip>
          )}
          <NoteRef note={t.description} title="What it is for" placeholder="" />
        </span>
      ),
    },
    {
      id: "trusts",
      header: "Trusts",
      fill: true,
      floor: 104,
      render: (t) => <TruncatedText text={trustsText(t)} mono maxWidth={560} />,
    },
    { id: "repo", header: "Repository", render: (t) => <TruncatedText text={t.repoName} mono maxWidth={140} /> },
    { id: "schedules", header: "May fire", render: (t) => <SchedulesCell trigger={t} /> },
    {
      id: "last",
      header: "Last request",
      render: (t) => {
        const used = t.lastUsedDetail === null ? null : `Last used by ${t.lastUsedDetail}`;
        if (t.lastRequest === null) {
          return used === null
            ? <span className="text-[13px] text-muted-foreground">never used</span>
            : (
              <RichTooltip title="No request yet" body={used} align="start">
                <span className="text-[13px] text-muted-foreground">used, no request</span>
              </RichTooltip>
            );
        }

        const last = t.lastRequest;
        return (
          <span className="inline-flex items-center gap-1.5">
            <button
              type="button"
              aria-label={`Show the trace of ${t.name}'s latest request`}
              className="inline-flex items-center gap-1.5 rounded-sm text-[12px] hover:underline"
              onClick={(e) => {
                e.stopPropagation();
                onOpenRequest(last.id, t.repoName);
              }}
              data-testid="trigger-last-request"
            >
              <RequestStateBadge state={last.state as TriggerRequestState} testId="trigger-last-request-state" />
              <RelativeTime value={last.createdUtc} />
            </button>
            {used !== null && <NoteRef note={used} title="Asked from" placeholder="" />}
          </span>
        );
      },
    },
    {
      id: "actions",
      header: "",
      align: "right",
      render: (t) => (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-xs" aria-label={`Actions for ${t.name}`} data-testid="trigger-actions">
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onClick={() => onConnect(t, null)} data-testid="trigger-action-connect">
              <Plug /> Connect
            </DropdownMenuItem>
            <DropdownMenuItem onClick={() => onShowRequests(t)} data-testid="trigger-action-requests">
              <ListChecks /> Requests
            </DropdownMenuItem>
            {!t.configuredBy && (
              <>
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={() => onEdit(t)} data-testid="trigger-action-edit">
                  <Pencil /> Edit
                </DropdownMenuItem>
                {t.kind === "key" && (
                  <DropdownMenuItem onClick={() => setRotateTarget(t)} data-testid="trigger-action-rotate">
                    <KeyRound /> New key
                  </DropdownMenuItem>
                )}
                <DropdownMenuItem
                  onClick={() => setEnabled.mutate({ trigger: t, enabled: !t.enabled })}
                  disabled={setEnabled.isPending}
                  data-testid="trigger-action-toggle"
                >
                  {t.enabled ? <><PowerOff /> Disable</> : <><Power /> Enable</>}
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem
                  className="text-destructive focus:text-destructive"
                  onClick={() => setRemoveTarget(t)}
                  data-testid="trigger-action-remove"
                >
                  <Trash2 /> Remove
                </DropdownMenuItem>
              </>
            )}
          </DropdownMenuContent>
        </DropdownMenu>
      ),
    },
  ];

  if (!loading && triggers !== undefined && triggers.length === 0) {
    return (
      <div className="rounded-lg border border-border" data-testid="triggers-empty">
        <EmptyState
          icon={<Webhook />}
          title="No external triggers yet"
          description="Create one so a GitHub workflow can sync a repository right after a merge, fire the schedules it picks, and show the sync and every run in the job."
          action={<Button size="sm" variant="outline" onClick={onCreate}>New trigger</Button>}
        />
      </div>
    );
  }

  return (
    <>
      <DataTable
        columns={columns}
        rows={triggers}
        rowKey={(t) => t.id}
        emptyMessage="No external triggers."
        data-testid="triggers-table"
      />

      <ConfirmDialog
        open={removeTarget !== null}
        title="Remove trigger"
        message={`Remove "${removeTarget?.name ?? ""}"? A workflow or system using it is refused from its next call. Its requests stay listed under the name it had.`}
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => { if (removeTarget !== null) remove.mutate(removeTarget); }}
        onClose={() => setRemoveTarget(null)}
      />

      <ConfirmDialog
        open={rotateTarget !== null}
        title="Give the trigger a new key"
        message={`"${rotateTarget?.name ?? ""}" gets a new key, shown once. The key it has now stops working at once, so update the calling system's secret right after.`}
        confirmLabel="New key"
        busy={rotate.isPending}
        onConfirm={() => { if (rotateTarget !== null) rotate.mutate(rotateTarget); }}
        onClose={() => setRotateTarget(null)}
      />
    </>
  );
}
