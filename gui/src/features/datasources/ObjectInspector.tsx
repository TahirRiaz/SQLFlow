import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { CircleAlert, ExternalLink, KeyRound, Loader2, TriangleAlert, X } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import type {
  ComputeTaskRequest, DatasourceObject, IntrospectionResult, UniqueKeyReport,
} from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CopyButton } from "../../components/CopyButton";
import { DataTable, type Column } from "../../components/DataTable";
import { Mono } from "../../components/Mono";
import { UniqueKeyReportView } from "./UniqueKeyReportView";
import { keyDetectionPath } from "./keyDetectionLink";
import { useCompute } from "./useCompute";

interface ObjectInspectorProps {
  reference: string;
  kind: string | null;
  database: string | null;
  object: DatasourceObject;
  onClose: () => void;
}

interface ColumnRow {
  name: string;
  ordinal: number;
  nativeType: string;
  isNullable: boolean;
  isIdentity: boolean;
  isPrimaryKeyMember: boolean;
}

const columnColumns: Column<ColumnRow>[] = [
  {
    id: "ordinal",
    header: "#",
    render: (row) => <span className="font-mono tabular-nums">{row.ordinal}</span>,
    width: 48,
  },
  {
    id: "name",
    header: "Column",
    render: (row) => (
      <span className="flex items-center gap-1.5">
        <Mono className={row.isPrimaryKeyMember ? "font-bold" : undefined}>{row.name}</Mono>
        {row.isPrimaryKeyMember && (
          <Badge variant="outline" className="border-primary/50 text-primary">PK</Badge>
        )}
        {row.isIdentity && <Badge variant="outline">identity</Badge>}
      </span>
    ),
  },
  { id: "type", header: "Type", render: (row) => <Mono>{row.nativeType}</Mono> },
  { id: "nullable", header: "Nullable", render: (row) => (row.isNullable ? "NULL" : "NOT NULL") },
];

/**
 * The drill-down for one live object: full introspection (columns, indexes) fetched as a compute task when
 * the sheet opens, plus on-demand unique-key detection. Everything runs on a worker node against the live
 * source; the sheet only renders task results. The dedicated Key detection page (deep-linked from the
 * section header) is the specialized surface with options, history, and the full report.
 */
export function ObjectInspector({ reference, kind, database, object, onClose }: ObjectInspectorProps) {
  const introspect = useCompute<IntrospectionResult>();
  const detect = useCompute<UniqueKeyReport>();
  const { run: runIntrospect } = introspect;
  const [detectStarted, setDetectStarted] = useState(false);

  const base: Pick<ComputeTaskRequest, "reference" | "kind" | "database" | "schema" | "objectName"> = {
    reference,
    kind,
    database,
    schema: object.schema,
    objectName: object.name,
  };

  useEffect(() => {
    void runIntrospect({
      reference, kind, database, schema: object.schema, objectName: object.name,
      operation: "introspectObject",
    });
  }, [runIntrospect, reference, kind, database, object.schema, object.name]);

  // The detection outlives the click, so it terminates with a toast on both outcomes (DESIGN.md 8.2).
  // A closed sheet aborts (and best-effort cancels) the task, which resolves without an error: no stray toast.
  useEffect(() => {
    if (detect.error !== null) {
      toast.error(detect.error);
    }
  }, [detect.error]);

  const runDetect = () => {
    setDetectStarted(true);
    void detect.run({ ...base, operation: "detectUniqueKey" }).then((report) => {
      if (report !== null) {
        toast.success(`Unique key detection finished for ${object.schema}.${object.name}.`);
      }
    });
  };

  const introspected = introspect.data?.found === true ? introspect.data.object ?? null : null;
  const unsupportedKind = kind !== null && kind !== "MSSQL" && kind !== "AZDB";

  return (
    <Sheet open onOpenChange={(open) => { if (!open) onClose(); }}>
      <SheetContent
        side="right"
        className="w-full gap-0 sm:max-w-[560px]"
        showCloseButton={false}
        data-testid="object-inspector"
      >
        <SheetHeader className="border-b border-border">
          <div className="flex items-center justify-between gap-2">
            <SheetTitle className="flex min-w-0 items-center gap-2 text-base">
              <span className="truncate font-mono text-sm font-semibold">{object.schema}.{object.name}</span>
              <Badge variant="outline">{object.type}</Badge>
            </SheetTitle>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Close"
              onClick={onClose}
              data-testid="object-inspector-close"
            >
              <X />
            </Button>
          </div>
          <SheetDescription className="sr-only">
            Live introspection of {object.schema}.{object.name} on {reference}.
          </SheetDescription>
        </SheetHeader>

        <div className="flex flex-1 flex-col gap-4 overflow-y-auto p-4">
          {introspect.running && (
            <div className="flex items-center gap-2 text-[13px] text-muted-foreground">
              <Loader2 className="size-4 animate-spin" />
              Introspecting on a worker node...
            </div>
          )}
          {introspect.error !== null && (
            <Alert variant="destructive" data-testid="object-inspector-error">
              <CircleAlert />
              <AlertDescription>{introspect.error}</AlertDescription>
            </Alert>
          )}
          {introspect.data?.found === false && (
            <Alert className="border-warning/50 text-warning">
              <TriangleAlert />
              <AlertDescription className="text-warning/90">
                The object no longer exists on the source (it may have been dropped).
              </AlertDescription>
            </Alert>
          )}

          {introspected !== null && (
            <>
              <div className="flex items-center justify-between gap-2">
                <h3 className="text-sm font-medium">Columns ({introspected.columns.length})</h3>
                {introspected.columns.length > 0 && (
                  <CopyButton
                    label="Copy columns"
                    text={() => introspected.columns.map((c) => c.name).join(", ")}
                    testId="copy-column-names"
                  />
                )}
              </div>
              <DataTable
                columns={columnColumns}
                rows={introspected.columns}
                rowKey={(row) => row.name}
                emptyMessage="The object exposes no columns."
                data-testid="object-columns-table"
              />

              {introspected.indexes.length > 0 && (
                <>
                  <h3 className="text-sm font-medium">Indexes ({introspected.indexes.length})</h3>
                  <div className="flex flex-col gap-1.5">
                    {introspected.indexes.map((index) => (
                      <div key={index.name} className="flex flex-wrap items-center gap-2">
                        <Mono>{index.name}</Mono>
                        <span className="text-[13px] text-muted-foreground">
                          ({index.keyColumns.join(", ")})
                        </span>
                        {index.isPrimaryKey && (
                          <Badge variant="outline" className="border-primary/50 text-primary">primary key</Badge>
                        )}
                        {index.isUnique && !index.isPrimaryKey && <Badge variant="outline">unique</Badge>}
                        {index.isClustered && <Badge variant="outline">clustered</Badge>}
                        {index.isColumnStore && <Badge variant="outline">columnstore</Badge>}
                      </div>
                    ))}
                  </div>
                </>
              )}

              {introspected.type === "View" && (
                <>
                  <div className="flex items-center justify-between gap-2">
                    <h3 className="text-sm font-medium">View source</h3>
                    {introspected.definition !== null && (
                      <CopyButton
                        label="Copy definition"
                        text={() => introspected.definition ?? ""}
                        testId="copy-view-definition"
                      />
                    )}
                  </div>
                  {introspected.definition !== null ? (
                    <CodeView
                      value={introspected.definition}
                      language="sql"
                      height={320}
                      data-testid="view-definition"
                    />
                  ) : (
                    // Saying WHY the source is absent is the point of this panel. "Permission denied" is an
                    // action for the source's owner; anything else is a fact about the view. Reporting either
                    // as a blank box would send someone looking for a problem that is not theirs.
                    <Alert data-testid="view-definition-unavailable">
                      <CircleAlert />
                      <AlertDescription>
                        {introspected.definitionAvailability === "PermissionDenied"
                          ? "This connection may read the view's rows but not its source. Reading it needs "
                            + "VIEW DEFINITION on SQL Server, SHOW VIEW on MySQL, or the equivalent grant, "
                            + "which the source's owner has to give."
                          : "The engine does not expose this view's source. Encrypted and system-supplied "
                            + "views report themselves this way."}
                      </AlertDescription>
                    </Alert>
                  )}
                </>
              )}

              <Separator />

              <div className="flex flex-wrap items-center justify-between gap-2">
                <h3 className="text-sm font-medium">Unique key detection</h3>
                <div className="flex items-center gap-2">
                  <Button variant="ghost" size="sm" asChild data-testid="open-key-detection">
                    <Link to={keyDetectionPath({ reference, kind, database, schema: object.schema, objectName: object.name })}>
                      <ExternalLink />
                      Open in Key detection
                    </Link>
                  </Button>
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={detect.running || unsupportedKind}
                    onClick={runDetect}
                    data-testid="detect-unique-key"
                  >
                    {detect.running ? <Loader2 className="animate-spin" /> : <KeyRound />}
                    {detect.running ? "Profiling..." : "Detect unique key"}
                  </Button>
                </div>
              </div>
              {unsupportedKind && (
                <p className="text-[13px] text-muted-foreground">
                  Detection profiles with T-SQL, so it is available for SQL Server and Azure SQL sources only.
                </p>
              )}
              {detect.error !== null && (
                <Alert variant="destructive" data-testid="detect-error">
                  <CircleAlert />
                  <AlertDescription>{detect.error}</AlertDescription>
                </Alert>
              )}
              {detectStarted && detect.data !== null && (
                <UniqueKeyReportView report={detect.data} dense data-testid="detect-report" />
              )}
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
