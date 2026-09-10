import { useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "../../api/client";
import { lineageApi, pipelineApi, repoApi } from "../../api/endpoints";
import type { FlowDependency } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { ConnectionRef } from "../../components/ConnectionRef";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { LineageJumpButton } from "../../components/LineageJumpButton";
import { Mono } from "../../components/Mono";
import { RelativeTime } from "../../components/RelativeTime";

/** One direction of a flow's dependency table: the other flow and the objects that mediate the edge. */
function dependencyColumns(otherFlow: (row: FlowDependency) => { name: string; pipelineId: string }): Column<FlowDependency>[] {
  return [
    {
      id: "flow",
      header: "Flow",
      render: (row) => {
        const other = otherFlow(row);
        return (
          <Link to={`/pipelines/${other.pipelineId}`} className="font-mono text-[12px] text-primary hover:underline">
            {other.name}
          </Link>
        );
      },
    },
    {
      id: "via",
      header: "Via objects",
      render: (row) => (
        <span className="whitespace-normal break-words font-mono text-[12px]">{row.viaObjects || "-"}</span>
      ),
    },
  ];
}

const waitsForColumns = dependencyColumns((row) => ({ name: row.fromFlow, pipelineId: row.fromPipelineId }));
const unblocksColumns = dependencyColumns((row) => ({ name: row.toFlow, pipelineId: row.toPipelineId }));

/**
 * The catalog tree's details panel for a flow: identity and scheduling facts (kind, batch, wave, lifecycle),
 * the authored YAML, and the flow-to-flow dependencies the lineage engine derived, split into what this flow
 * waits for and what it unblocks.
 */
export function FlowDetailsPanel({ repoId, pipelineId }: { repoId: string; pipelineId: string }) {
  const [tab, setTab] = useState<"overview" | "yaml" | "dependencies">("overview");

  const pipeline = useQuery({
    queryKey: ["catalog-pipeline", pipelineId],
    queryFn: () => pipelineApi.getById(pipelineId),
  });
  const repo = useQuery({
    queryKey: ["catalog-repo", repoId],
    queryFn: () => repoApi.getById(repoId),
  });
  const dependencies = useQuery({
    queryKey: ["catalog-flow-dependencies", repoId, pipelineId],
    queryFn: () => lineageApi.dependencies(repoId, { pipelineId, pageSize: 200 }),
    enabled: tab === "dependencies",
  });

  if (pipeline.isPending) {
    return (
      <div className="flex flex-col gap-2">
        <Skeleton className="h-8 w-72 max-w-full" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-2/3" />
        <Skeleton className="mt-2 h-64 w-full" />
      </div>
    );
  }
  if (pipeline.isError) {
    return isApiError(pipeline.error)
      ? <CorrelationError error={pipeline.error} />
      : <p className="text-[13px] text-destructive">{String(pipeline.error)}</p>;
  }

  const flow = pipeline.data;
  const waitsFor = (dependencies.data?.items ?? []).filter((row) => row.toPipelineId === pipelineId);
  const unblocks = (dependencies.data?.items ?? []).filter((row) => row.fromPipelineId === pipelineId);

  return (
    <div data-testid="catalog-flow-details">
      <div className="mb-1 flex flex-wrap items-center gap-2">
        <h2 className="min-w-0 break-words font-mono text-base font-medium">{flow.name}</h2>
        <Badge variant="secondary">{flow.kind}</Badge>
        {!flow.active && <Badge variant="outline" className="border-warning/50 text-warning">inactive</Badge>}
        <div className="grow" />
        <Link to={`/pipelines/${flow.id}`} className="whitespace-nowrap text-[13px] text-primary hover:underline">
          Open pipeline page
        </Link>
        <LineageJumpButton
          target={{
            kind: "node",
            repoId: flow.repoId,
            repoName: repo.data?.name ?? flow.repoId,
            focusId: flow.id,
            label: flow.name,
            sublabel: flow.relativePath,
          }}
          variant="outlined"
        />
      </div>
      <p className="mb-4 break-all font-mono text-xs text-muted-foreground">{flow.relativePath}</p>

      <Tabs value={tab} onValueChange={(value) => setTab(value as typeof tab)} className="gap-4">
        <TabsList variant="line">
          <TabsTrigger value="overview" data-testid="catalog-tab-flow-overview">Overview</TabsTrigger>
          <TabsTrigger value="yaml" data-testid="catalog-tab-flow-yaml">YAML</TabsTrigger>
          <TabsTrigger value="dependencies" data-testid="catalog-tab-flow-dependencies">Dependencies</TabsTrigger>
        </TabsList>

        <TabsContent value="overview">
          <div className="grid grid-cols-2 gap-3">
            <DetailPair label="Repo">{repo.data?.name ?? repoId}</DetailPair>
            <DetailPair label="Batch"><Mono>{flow.batch ?? "default"}</Mono></DetailPair>
            <DetailPair label="Wave"><Mono className="tabular-nums">{flow.wave}</Mono></DetailPair>
            <DetailPair label="Lifecycle">{flow.lifecycle}</DetailPair>
            <DetailPair label="Execution mode">{flow.executionMode}</DetailPair>
            <DetailPair label="Active">{flow.active ? "yes" : "no"}</DetailPair>
            <DetailPair label="Source server"><ConnectionRef value={flow.sourceServer} copyTestId="copy-flow-source" /></DetailPair>
            <DetailPair label="Target server"><ConnectionRef value={flow.targetServer} copyTestId="copy-flow-target" /></DetailPair>
            <DetailPair label="First seen"><RelativeTime value={flow.firstSeenUtc} absolute /></DetailPair>
            <DetailPair label="Last seen"><RelativeTime value={flow.lastSeenUtc} absolute /></DetailPair>
          </div>
        </TabsContent>

        <TabsContent value="yaml">
          <CodeView value={flow.yaml} language="yaml" height={420} data-testid="catalog-flow-yaml" />
        </TabsContent>

        <TabsContent value="dependencies">
          {dependencies.isPending ? (
            <Skeleton className="h-48 w-full" />
          ) : dependencies.isError ? (
            isApiError(dependencies.error)
              ? <CorrelationError error={dependencies.error} />
              : <p className="text-[13px] text-destructive">{String(dependencies.error)}</p>
          ) : (
            <div className="flex flex-col gap-4">
              <section>
                <h3 className="mb-2 text-sm font-medium">{`Waits for (${waitsFor.length})`}</h3>
                <DataTable<FlowDependency>
                  columns={waitsForColumns}
                  rows={waitsFor}
                  rowKey={(row) => row.id}
                  emptyMessage="Nothing upstream: this flow can start in the first wave of its group."
                  data-testid="catalog-flow-waits-for"
                />
              </section>
              <section>
                <h3 className="mb-2 text-sm font-medium">{`Unblocks (${unblocks.length})`}</h3>
                <DataTable<FlowDependency>
                  columns={unblocksColumns}
                  rows={unblocks}
                  rowKey={(row) => row.id}
                  emptyMessage="Nothing downstream waits on this flow."
                  data-testid="catalog-flow-unblocks"
                />
              </section>
            </div>
          )}
        </TabsContent>
      </Tabs>
    </div>
  );
}
