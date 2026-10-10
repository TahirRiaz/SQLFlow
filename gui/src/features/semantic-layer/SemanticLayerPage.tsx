import { useCallback, useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Plus } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { columnPolicyApi, semanticLayerApi } from "../../api/endpoints";
import type { RestrictedColumn } from "../../api/types";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { MeasureDialog } from "./MeasureDialog";
import { ReportsPanel } from "./ReportsPanel";
import { SavedAnswersPanel } from "./SavedAnswersPanel";
import { MeasuresTable, SemanticObjectPanel } from "./SemanticObjectPanel";
import { SemanticTree } from "./SemanticTree";
import { COLUMN_POLICY_ROOT, errorText, refreshSemanticLayer, SEMANTIC_ROOT, textOrNull } from "./shared";

type PageTab = "tables" | "instructions" | "examples" | "reports" | "blocked";

const parseTab = (value: string | null): PageTab =>
  value === "instructions" || value === "examples" || value === "reports" || value === "blocked" ? value : "tables";

/** The layer-wide instructions and every measure: what an assistant reads before writing any SQL. */
function InstructionsPanel({ onOpenObject }: { onOpenObject: (key: string) => void }) {
  const queryClient = useQueryClient();
  const instructions = useQuery({
    queryKey: [SEMANTIC_ROOT, "instructions"],
    queryFn: () => semanticLayerApi.instructions(),
  });
  const measures = useQuery({ queryKey: [SEMANTIC_ROOT, "measures"], queryFn: () => semanticLayerApi.measures() });
  const [text, setText] = useState("");
  const [creating, setCreating] = useState(false);

  const stored = instructions.data?.instructions ?? "";
  useEffect(() => setText(stored), [stored]);

  const save = useMutation({
    mutationFn: () => semanticLayerApi.setInstructions(textOrNull(text)),
    onSuccess: () => {
      toast.success("Instructions saved.");
      refreshSemanticLayer(queryClient);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  return (
    <div className="flex flex-col gap-6" data-testid="semantic-instructions-panel">
      <section className="flex max-w-4xl flex-col gap-2">
        <Label htmlFor="semantic-instructions" className="text-sm font-medium">General instructions</Label>
        <p className="text-[13px] text-muted-foreground">
          Read by the assistant before it writes any SQL against the layer: conventions, fiscal calendar, which status
          values count as active, filters every query must apply. Markdown is fine.
        </p>
        {instructions.isPending ? <Skeleton className="h-60 w-full" /> : (
          <Textarea
            id="semantic-instructions"
            value={text}
            onChange={(event) => setText(event.target.value)}
            placeholder="- Amounts are in NOK excluding VAT.&#10;- Exclude test stations (StationId < 0)."
            className="min-h-60 font-mono text-[12px]"
            data-testid="semantic-instructions"
          />
        )}
        {instructions.isError && <p className="text-[13px] text-destructive">{errorText(instructions.error)}</p>}
        <div className="flex items-center gap-3">
          <Button
            size="sm"
            onClick={() => save.mutate()}
            disabled={instructions.isPending || text.trim() === stored.trim() || save.isPending}
            data-testid="semantic-instructions-save"
          >
            {save.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
          {instructions.data?.updatedUtc != null && (
            <span className="text-xs text-muted-foreground">
              {`Last changed by ${instructions.data.updatedBy ?? "unknown"} `}
              <RelativeTime value={instructions.data.updatedUtc} />
            </span>
          )}
        </div>
      </section>

      <section className="flex flex-col gap-2">
        <div className="flex items-center gap-2">
          <h3 className="mr-auto text-sm font-medium">{`Measures (${measures.data?.length ?? 0})`}</h3>
          <Button size="xs" variant="outline" onClick={() => setCreating(true)} data-testid="semantic-new-measure">
            <Plus />
            New measure
          </Button>
        </div>
        {measures.isError && <p className="text-[13px] text-destructive">{errorText(measures.error)}</p>}
        {measures.isPending ? <Skeleton className="h-32 w-full" /> : (
          <MeasuresTable
            measures={measures.data ?? []}
            anchor={null}
            onOpenObject={onOpenObject}
            emptyMessage="No measure is defined yet."
            testId="semantic-all-measures"
          />
        )}
      </section>

      {creating && <MeasureDialog measure={null} anchor={null} onClose={() => setCreating(false)} />}
    </div>
  );
}

/** Every column currently outside the layer, across the catalog: the audit of what the assistant cannot see. */
function BlockedColumnsPanel({ onOpenObject }: { onOpenObject: (key: string) => void }) {
  const columns: Column<RestrictedColumn>[] = [
    { id: "objectName", header: "Object", render: (row) => <span className="font-mono text-[12px] font-medium">{row.objectName}</span> },
    { id: "database", header: "Database", render: (row) => <Mono>{row.database ?? "-"}</Mono> },
    { id: "schema", header: "Schema", render: (row) => <Mono>{row.schema ?? "-"}</Mono> },
    { id: "columnName", header: "Column", render: (row) => <Mono>{row.columnName}</Mono> },
    { id: "reason", header: "Reason", render: (row) => row.reason ?? <span className="text-muted-foreground">-</span> },
    {
      id: "state",
      header: "State",
      render: (row) => (
        <Badge variant="outline" className="text-[11px]">{row.updatedUtc === null ? "never reviewed" : "denied"}</Badge>
      ),
    },
    { id: "updatedUtc", header: "Changed", render: (row) => <RelativeTime value={row.updatedUtc} /> },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => (
        <Button variant="ghost" size="xs" onClick={() => onOpenObject(row.objectKey)} data-testid="semantic-blocked-open">
          Open
        </Button>
      ),
    },
  ];

  return (
    <div className="flex flex-col gap-2" data-testid="semantic-blocked-panel">
      <p className="text-[13px] text-muted-foreground">
        Every column outside the semantic layer: explicitly denied, or never reviewed. None of these is visible to the
        assistant or readable by its queries.
      </p>
      <PagedTable<RestrictedColumn>
        queryKey={[COLUMN_POLICY_ROOT, "list"]}
        fetchPage={(page, pageSize) => columnPolicyApi.list({ page, pageSize })}
        columns={columns}
        rowKey={(row) => `${row.objectKey}::${row.columnName}`}
        emptyMessage="Every known column is in the semantic layer."
        data-testid="semantic-blocked-table"
      />
    </div>
  );
}

/**
 * The semantic layer: the tables, views, and columns the AI assistant may use, and what they mean. The column
 * allow-list is the layer, so this is also where columns are allowed or denied: an object is in the layer once any
 * of its columns is allowed, and the assistant's schema tools serve only that, with the business context described
 * here. Tables are browsed in an explorer tree (selection in the URL as ?object=, so it is deep-linkable), layer-wide
 * instructions and measures have their own tab, the Power BI reports the layer learns vocabulary from have theirs, and
 * the blocked-columns audit lists everything still outside.
 */
export default function SemanticLayerPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const tab = parseTab(searchParams.get("tab"));
  const selectedKey = searchParams.get("object");

  const overview = useQuery({ queryKey: [SEMANTIC_ROOT, "overview"], queryFn: () => semanticLayerApi.overview() });

  const setParams = useCallback((changes: Record<string, string | null>) => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      for (const [name, value] of Object.entries(changes)) {
        if (value === null) {
          next.delete(name);
        } else {
          next.set(name, value);
        }
      }
      return next;
    });
  }, [setSearchParams]);

  const openObject = useCallback((key: string) => setParams({ tab: null, object: key }), [setParams]);

  return (
    <Page data-testid="page-semantic-layer">
      <PageHeader
        title="AI knowledge"
        subtitle="What the AI assistant knows about your data: which tables and columns it may use and what they mean, answers people have confirmed, and the Power BI reports it learns business terms from. A column is visible to the assistant only once it is allowed; anything never reviewed stays hidden and is refused when a query runs."
        actions={overview.data !== undefined && (
          <div className="flex items-center gap-2" data-testid="semantic-layer-stats">
            <Badge variant="secondary">{`${overview.data.tableCount} table${overview.data.tableCount === 1 ? "" : "s"}`}</Badge>
            <Badge variant="secondary">{`${overview.data.measures.length} measure${overview.data.measures.length === 1 ? "" : "s"}`}</Badge>
          </div>
        )}
      />

      <Tabs value={tab} onValueChange={(value) => setParams({ tab: value === "tables" ? null : value })} className="gap-4">
        <TabsList variant="line">
          <TabsTrigger value="tables" data-testid="semantic-page-tab-tables">Tables</TabsTrigger>
          <TabsTrigger value="instructions" data-testid="semantic-page-tab-instructions">Instructions &amp; measures</TabsTrigger>
          <TabsTrigger value="examples" data-testid="semantic-page-tab-examples">Saved answers</TabsTrigger>
          <TabsTrigger value="reports" data-testid="semantic-page-tab-reports">Power BI reports</TabsTrigger>
          <TabsTrigger value="blocked" data-testid="semantic-page-tab-blocked">Blocked columns</TabsTrigger>
        </TabsList>

        <TabsContent value="tables">
          <div className="flex flex-col overflow-hidden rounded-lg border bg-card md:flex-row">
            <div className="max-h-80 shrink-0 overflow-y-auto border-b p-2 md:max-h-[calc(100vh-260px)] md:min-h-80 md:w-80 md:border-b-0 md:border-r">
              <SemanticTree selectedKey={selectedKey} onSelect={openObject} />
            </div>
            <div className="min-h-80 min-w-0 flex-1 overflow-y-auto p-4 md:max-h-[calc(100vh-260px)]">
              {selectedKey === null ? (
                <p className="text-[13px] text-muted-foreground" data-testid="semantic-details-placeholder">
                  Select a table or view to choose which of its columns the assistant may use, and to describe what the
                  table and its columns mean, how it joins, and the measures computed from it. The badge beside each
                  name is allowed/total columns.
                </p>
              ) : (
                <SemanticObjectPanel objectKey={selectedKey} />
              )}
            </div>
          </div>
        </TabsContent>

        <TabsContent value="instructions">
          <InstructionsPanel onOpenObject={openObject} />
        </TabsContent>

        <TabsContent value="examples">
          <SavedAnswersPanel />
        </TabsContent>

        <TabsContent value="reports">
          <ReportsPanel />
        </TabsContent>

        <TabsContent value="blocked">
          <BlockedColumnsPanel onOpenObject={openObject} />
        </TabsContent>
      </Tabs>
    </Page>
  );
}
