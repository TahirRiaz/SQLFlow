import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useSearchParams } from "react-router-dom";
import { CircleOff, Plus } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "../../api/client";
import { externalTriggerApi } from "../../api/endpoints";
import type { ExternalTrigger, ExternalTriggerWritten } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { ConnectSheet } from "./ConnectSheet";
import { RequestsTab } from "./RequestsTab";
import { TriggerSheet } from "./TriggerSheet";
import { TriggersTab } from "./TriggersTab";
import { errorText, useRequestTracePanel } from "./triggerVisuals";

type Tab = "triggers" | "requests";

function tabFromParam(value: string | null): Tab {
  return value === "requests" ? "requests" : "triggers";
}

/**
 * The callers outside SQLFlow that may sync a repository and fire a selection of its schedules (a GitHub workflow after a
 * merge, or a system holding a key), and every request they made. Triggers lists them, with how to connect each; Requests
 * lists what they asked for and how it went, each request's live trace opening in the bottom panel. `?tab=requests`
 * opens the requests, and `?request=<id>` (the link a GitHub job's summary carries) opens one request's trace.
 */
export default function ExternalTriggersPage() {
  const [searchParams] = useSearchParams();
  const linkedRequest = searchParams.get("request");
  const [tab, setTab] = useState<Tab>(linkedRequest !== null ? "requests" : tabFromParam(searchParams.get("tab")));
  const [requestTrigger, setRequestTrigger] = useState<string | null>(null);
  const [editing, setEditing] = useState<ExternalTrigger | "new" | null>(null);
  const [connecting, setConnecting] = useState<{ trigger: ExternalTrigger; written: ExternalTriggerWritten | null } | null>(null);
  const openRequest = useRequestTracePanel();

  const triggersQuery = useQuery({ queryKey: ["external-triggers"], queryFn: externalTriggerApi.list, refetchInterval: 30_000 });
  const setupQuery = useQuery({ queryKey: ["external-triggers", "setup"], queryFn: externalTriggerApi.setup, staleTime: 300_000 });

  // A request linked from outside (a GitHub job's summary) opens once per link, not on every render.
  const opened = useRef<string | null>(null);
  useEffect(() => {
    if (linkedRequest !== null && opened.current !== linkedRequest) {
      opened.current = linkedRequest;
      openRequest({ id: linkedRequest, repoName: `${linkedRequest.slice(0, 8)}` });
    }
  }, [linkedRequest, openRequest]);

  return (
    <Page data-testid="page-external-triggers">
      <PageHeader
        title="External triggers"
        subtitle="GitHub workflows and other systems that may sync a repository and fire a selection of its schedules, and every request they made."
        actions={(
          <Button size="sm" onClick={() => setEditing("new")} data-testid="open-create-trigger">
            <Plus />
            New trigger
          </Button>
        )}
      />

      {setupQuery.data !== undefined && !setupQuery.data.enabled && (
        <Alert data-testid="triggers-turned-off">
          <CircleOff />
          <AlertTitle>External triggers are turned off</AlertTitle>
          <AlertDescription>
            This control plane refuses every exchange and request until{" "}
            <code className="font-mono text-[12px]">ControlPlane:ExternalTriggers:Enabled</code> is true again. Triggers
            and requests stay listed.
          </AlertDescription>
        </Alert>
      )}

      {triggersQuery.isError && (
        isApiError(triggersQuery.error)
          ? <CorrelationError error={triggersQuery.error} />
          : <p className="text-[13px] text-destructive">{errorText(triggersQuery.error)}</p>
      )}

      <Tabs value={tab} onValueChange={(next) => setTab(tabFromParam(next))} className="gap-3">
        <TabsList variant="line" className="w-full justify-start overflow-x-auto" data-testid="external-triggers-tabs">
          <TabsTrigger value="triggers" className="flex-none" data-testid="external-triggers-tab-triggers">
            Triggers{triggersQuery.data ? <span className="ml-1 text-muted-foreground">{triggersQuery.data.length}</span> : null}
          </TabsTrigger>
          <TabsTrigger value="requests" className="flex-none" data-testid="external-triggers-tab-requests">Requests</TabsTrigger>
        </TabsList>

        <TabsContent value="triggers">
          <TriggersTab
            triggers={triggersQuery.data}
            loading={triggersQuery.isLoading}
            onCreate={() => setEditing("new")}
            onEdit={(trigger) => setEditing(trigger)}
            onConnect={(trigger, written) => setConnecting({ trigger, written })}
            onShowRequests={(trigger) => {
              setRequestTrigger(trigger.id);
              setTab("requests");
            }}
            onOpenRequest={(id, repoName) => openRequest({ id, repoName })}
          />
        </TabsContent>

        <TabsContent value="requests">
          <RequestsTab
            triggers={triggersQuery.data}
            triggerId={requestTrigger}
            onTriggerChange={setRequestTrigger}
            onOpen={(request) => openRequest(request)}
          />
        </TabsContent>
      </Tabs>

      {editing !== null && (
        <TriggerSheet
          existing={editing === "new" ? null : editing}
          onSaved={(written, created) => {
            setEditing(null);
            if (created || written.key !== null) {
              setConnecting({ trigger: written.trigger, written });
            }
          }}
          onClose={() => setEditing(null)}
        />
      )}

      {connecting !== null && setupQuery.data !== undefined && (
        <ConnectSheet
          trigger={connecting.trigger}
          setup={setupQuery.data}
          secret={connecting.written?.key ?? null}
          warnings={connecting.written?.warnings ?? []}
          onClose={() => setConnecting(null)}
        />
      )}
    </Page>
  );
}
