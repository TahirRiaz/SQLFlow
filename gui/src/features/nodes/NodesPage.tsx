import { useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { Node } from "../../api/types";
import { nodeApi } from "../../api/endpoints";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { OnlineBadge } from "../../components/StatusBadge";
import { DispatchPanel } from "./DispatchPanel";
import { ManagedActivityPanel, ManagedWorkersPanel } from "./ManagedWorkersPanel";
import { NodeRestartButton } from "./NodeRestartButton";
import { NodeDeleteButton } from "./NodeDeleteButton";
import { useManagedWorkers } from "./nodeQueries";
import { NodesSummary, type NodesTab } from "./NodesSummary";
import { PurgeOfflineNodesButton } from "./PurgeOfflineNodesButton";
import { WorkerPoolsPanel } from "./WorkerPoolsPanel";

const tabs: readonly NodesTab[] = ["workers", "scaling", "queue", "activity"];

/** The tab a link asks for (`?tab=queue`), else the workers. */
function tabFromParam(value: string | null): NodesTab {
  return tabs.find((tab) => tab === value) ?? "workers";
}

const columns: Column<Node>[] = [
  {
    id: "name",
    header: "Name",
    render: (row) => <span className="font-mono text-[12px] font-medium">{row.name}</span>,
  },
  { id: "status", header: "Status", render: (row) => <OnlineBadge online={row.online} /> },
  {
    id: "pool",
    header: "Pool",
    render: (row) => <Mono>{row.pool === null || row.pool.length === 0 ? "default" : row.pool}</Mono>,
  },
  {
    id: "runs",
    header: "Runs",
    align: "right",
    render: (row) => (
      <span className="font-mono tabular-nums" title="runs executing / runs the node executes at once">
        {row.busyRuns} / {row.runSlots}
      </span>
    ),
  },
  { id: "version", header: "Version", render: (row) => <Mono>{row.version ?? "-"}</Mono> },
  {
    id: "lastSeen",
    header: "Last seen",
    render: (row) => (
      <Tooltip>
        <TooltipTrigger asChild>
          <span>
            <RelativeTime value={row.lastSeenUtc} />
          </span>
        </TooltipTrigger>
        <TooltipContent>
          First seen <RelativeTime value={row.firstSeenUtc} />
        </TooltipContent>
      </Tooltip>
    ),
  },
  {
    id: "actions",
    header: "",
    render: (row) => (
      <div className="flex items-center justify-end gap-2">
        <NodeRestartButton node={row} />
        <NodeDeleteButton node={row} />
      </div>
    ),
  },
];

/** The worker fleet, one question per tab under a line of headline facts: which workers exist and whether they are
 *  online (Workers), how many each pool runs and the controls that change it (Scaling), what is queued, what holds it
 *  back and what is running where (Queue), and, with managed workers on, what the control plane started, drained and
 *  stopped and why (Activity). The summary's cells open the tab behind each number, and `?tab=` opens one directly. */
export default function NodesPage() {
  const [searchParams] = useSearchParams();
  const [tab, setTab] = useState<NodesTab>(tabFromParam(searchParams.get("tab")));
  const managed = useManagedWorkers().data;
  const managedOn = managed !== undefined && managed.enabled && managed.status !== null;
  // Activity exists only while managed workers are on; a link or a stale choice of it falls back to the workers.
  const shown = tab === "activity" && !managedOn ? "workers" : tab;

  return (
    <Page data-testid="page-nodes">
      <PageHeader
        title="Nodes"
        subtitle="Where runs execute, and how workers are started."
        actions={shown === "workers" ? <PurgeOfflineNodesButton /> : undefined}
      />

      <NodesSummary onOpen={setTab} />

      <Tabs value={shown} onValueChange={(next) => setTab(tabFromParam(next))} className="gap-3">
        <TabsList variant="line" className="w-full justify-start overflow-x-auto" data-testid="nodes-tabs">
          <TabsTrigger value="workers" className="flex-none" data-testid="nodes-tab-workers">Workers</TabsTrigger>
          <TabsTrigger value="scaling" className="flex-none" data-testid="nodes-tab-scaling">Scaling</TabsTrigger>
          <TabsTrigger value="queue" className="flex-none" data-testid="nodes-tab-queue">Queue</TabsTrigger>
          {managedOn && (
            <TabsTrigger value="activity" className="flex-none" data-testid="nodes-tab-activity">Activity</TabsTrigger>
          )}
        </TabsList>

        <TabsContent value="workers">
          <PagedTable
            queryKey={["nodes", "list"]}
            fetchPage={(page, pageSize) => nodeApi.list({ page, pageSize })}
            columns={columns}
            rowKey={(row) => row.name}
            pollMs={5000}
            emptyMessage="No worker nodes have registered yet."
            data-testid="nodes-table"
          />
        </TabsContent>

        <TabsContent value="scaling" className="flex flex-col gap-3">
          <WorkerPoolsPanel />
          <ManagedWorkersPanel />
        </TabsContent>

        <TabsContent value="queue">
          <DispatchPanel />
        </TabsContent>

        {managedOn && (
          <TabsContent value="activity">
            <ManagedActivityPanel />
          </TabsContent>
        )}
      </Tabs>
    </Page>
  );
}
