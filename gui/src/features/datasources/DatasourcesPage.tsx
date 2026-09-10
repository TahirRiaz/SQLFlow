import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Cable, CircleAlert, CircleCheck, Loader2, Telescope } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { datasourceApi } from "../../api/endpoints";
import type { ConnectionTestResult, Datasource } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ConnectionRef } from "../../components/ConnectionRef";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { useCompute } from "./useCompute";

/** The connection-test sheet: runs the testConnection task when opened and shows the live outcome. */
function TestConnectionSheet({ datasource, onClose }: { datasource: Datasource; onClose: () => void }) {
  const test = useCompute<ConnectionTestResult>();
  const { run } = test;

  // The sheet mounts per target, so a single on-mount run tests exactly the datasource it was opened for.
  useEffect(() => {
    void run({ reference: datasource.reference, operation: "testConnection", kind: datasource.kind });
  }, [run, datasource.reference, datasource.kind]);

  return (
    <Sheet open onOpenChange={(open) => { if (!open) onClose(); }}>
      <SheetContent side="right" className="sm:max-w-md" data-testid="test-connection-dialog">
        <SheetHeader>
          <SheetTitle>Test connection</SheetTitle>
          <SheetDescription>
            <Mono>{datasource.reference}</Mono>
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-col gap-3 px-4">
          {test.running && (
            <div className="flex items-center gap-2 text-[13px] text-muted-foreground">
              <Loader2 className="size-4 animate-spin" />
              A worker node is opening the connection...
            </div>
          )}
          {test.error !== null && (
            <Alert variant="destructive" data-testid="test-connection-error">
              <CircleAlert />
              <AlertDescription>{test.error}</AlertDescription>
            </Alert>
          )}
          {test.data !== null && (
            <Alert className="border-success/50 text-success" data-testid="test-connection-ok">
              <CircleCheck />
              <AlertDescription className="text-success/90">
                <p>
                  Connected to a {test.data.kind} source
                  {test.data.database !== null ? <> (database <Mono>{test.data.database}</Mono>)</> : null}
                  {test.data.serverVersion !== null ? <>, server version {test.data.serverVersion}</> : null}
                  {" "}in {Math.round(test.data.elapsedMs)} ms.
                </p>
              </AlertDescription>
            </Alert>
          )}
        </div>
        <SheetFooter>
          <Button variant="outline" size="sm" onClick={onClose} data-testid="test-connection-close">
            Close
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/**
 * The estate's datasources: every connection reference the active pipelines declare, with live actions
 * (test the connection, browse databases/schemas/tables) that run as queued compute tasks on a worker node
 * that can actually reach the source. Inline-literal identities are listed but not browsable: a worker
 * cannot turn a hash back into a connection.
 */
export default function DatasourcesPage() {
  const navigate = useNavigate();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [testTarget, setTestTarget] = useState<Datasource | null>(null);

  const datasources = useQuery({
    queryKey: ["datasources", "list"],
    queryFn: () => datasourceApi.list(),
  });

  const columns: Column<Datasource>[] = [
    {
      id: "reference",
      header: "Reference",
      render: (row) => <ConnectionRef value={row.reference} copyTestId="copy-datasource-ref" />,
    },
    {
      id: "kind",
      header: "Kind",
      render: (row) => (row.kind !== null
        ? <Badge variant="outline">{row.kind}</Badge>
        : <span className="text-[13px] text-muted-foreground">unknown</span>),
    },
    {
      id: "usage",
      header: "Used by",
      render: (row) => (
        <span className="text-[13px]">
          {row.sourcePipelines} source / {row.targetPipelines} target pipeline(s)
        </span>
      ),
    },
    {
      id: "resolvable",
      header: "Compute",
      render: (row) => (row.resolvable
        ? <Badge variant="outline" className="border-success/50 text-success">browsable</Badge>
        : (
          <Tooltip>
            <TooltipTrigger asChild>
              <Badge variant="outline" className="text-muted-foreground">not browsable</Badge>
            </TooltipTrigger>
            <TooltipContent className="max-w-xs">
              An inline connection literal is identified by hash only; a worker cannot resolve it for ad-hoc
              compute. Declare it as a ${"{...}"} reference to browse it.
            </TooltipContent>
          </Tooltip>
        )),
    },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => (
        <span className="flex items-center justify-end gap-2">
          <Button
            variant="ghost"
            size="xs"
            disabled={!canOperate || !row.resolvable}
            onClick={(e) => {
              e.stopPropagation();
              setTestTarget(row);
            }}
            data-testid="datasource-test"
          >
            <Cable />
            Test
          </Button>
          <Button
            variant="outline"
            size="xs"
            disabled={!canOperate || !row.resolvable}
            onClick={(e) => {
              e.stopPropagation();
              navigate(`/datasources/browse?ref=${encodeURIComponent(row.reference)}${row.kind !== null ? `&kind=${encodeURIComponent(row.kind)}` : ""}`);
            }}
            data-testid="datasource-browse"
          >
            <Telescope />
            Browse
          </Button>
        </span>
      ),
    },
  ];

  return (
    <Page data-testid="page-datasources">
      <PageHeader
        title="Datasources"
        subtitle={canOperate
          ? "The connection references the estate's pipelines declare. Browse runs live against the source on a worker node; nothing here ever carries a secret."
          : "The connection references the estate's pipelines declare. Live browse and connection tests need the operate scope."}
      />

      {datasources.isError && (isApiError(datasources.error)
        ? <CorrelationError error={datasources.error} />
        : <p className="text-[13px] text-destructive">{String(datasources.error)}</p>)}

      {!datasources.isError && (
        <DataTable
          columns={columns}
          rows={datasources.data}
          rowKey={(row) => row.reference}
          emptyMessage="No datasources yet: sync a repo with flows and their connection references appear here."
          data-testid="datasources-table"
        />
      )}

      {testTarget !== null && (
        <TestConnectionSheet datasource={testTarget} onClose={() => setTestTarget(null)} />
      )}
    </Page>
  );
}
