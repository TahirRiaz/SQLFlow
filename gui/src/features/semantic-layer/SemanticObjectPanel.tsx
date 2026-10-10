import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { KeyRound, Loader2, Pencil, Plus, Trash2 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { isApiError } from "../../api/client";
import { columnPolicyApi, semanticLayerApi } from "../../api/endpoints";
import type {
  ColumnPolicyState, SemanticCuratedJoin, SemanticDiscoveredJoin, SemanticExampleAdmin, SemanticMeasureAdmin,
  SemanticObjectAdmin,
} from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { RelativeTime } from "../../components/RelativeTime";
import { encodeNodeId } from "../catalog/nodeIds";
import { ExampleDialog } from "./ExampleDialog";
import { JoinDialog, type JoinDraft } from "./JoinDialog";
import type { PickedTable } from "./LayerTablePicker";
import { MeasureDialog } from "./MeasureDialog";
import {
  errorText, formatList, parseList, qualifiedName, refreshSemanticLayer, SEMANTIC_ROOT, ServedBadge, textOrNull,
} from "./shared";

type ObjectTab = "about" | "columns" | "relationships" | "measures" | "examples" | "powerbi";

// ---- About ---------------------------------------------------------------------------------------------------------

/** The table-level business context: what the table is called and means, what people call it, and its key. */
function AboutTab({ detail }: { detail: SemanticObjectAdmin }) {
  const queryClient = useQueryClient();
  const { annotation } = detail;
  const [businessName, setBusinessName] = useState(annotation.businessName ?? "");
  const [description, setDescription] = useState(annotation.description ?? "");
  const [synonyms, setSynonyms] = useState(formatList(annotation.synonyms));
  const [keyColumns, setKeyColumns] = useState(formatList(annotation.keyColumns));

  const storedSynonyms = formatList(annotation.synonyms);
  const storedKey = formatList(annotation.keyColumns);
  useEffect(() => {
    setBusinessName(annotation.businessName ?? "");
    setDescription(annotation.description ?? "");
    setSynonyms(storedSynonyms);
    setKeyColumns(storedKey);
  }, [annotation.businessName, annotation.description, storedSynonyms, storedKey]);

  const save = useMutation({
    mutationFn: () => semanticLayerApi.setAnnotation({
      objectKey: detail.key,
      businessName: textOrNull(businessName),
      description: textOrNull(description),
      synonyms: parseList(synonyms),
      keyColumns: parseList(keyColumns),
    }),
    onSuccess: () => {
      toast.success(`Saved the business context for ${detail.name}.`);
      refreshSemanticLayer(queryClient);
    },
  });

  const dirty = (annotation.businessName ?? "") !== businessName
    || (annotation.description ?? "") !== description
    || formatList(parseList(synonyms)) !== storedSynonyms
    || formatList(parseList(keyColumns)) !== storedKey;

  return (
    <div className="flex max-w-3xl flex-col gap-4" data-testid="semantic-about">
      {save.isError && <p className="text-[13px] text-destructive">{errorText(save.error)}</p>}
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="semantic-business-name">Business name</Label>
        <Input
          id="semantic-business-name"
          value={businessName}
          onChange={(event) => setBusinessName(event.target.value)}
          placeholder="Bike trips"
          className="h-8"
          data-testid="semantic-business-name"
        />
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="semantic-description">Description</Label>
        <Textarea
          id="semantic-description"
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          placeholder="What one row represents, and anything a query author must know (filters to apply, grain, lateness)."
          className="min-h-28"
          data-testid="semantic-description"
        />
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="semantic-synonyms">Synonyms</Label>
        <Input
          id="semantic-synonyms"
          value={synonyms}
          onChange={(event) => setSynonyms(event.target.value)}
          placeholder="rides, journeys"
          className="h-8"
          data-testid="semantic-synonyms"
        />
        <p className="text-xs text-muted-foreground">Comma-separated. The assistant&apos;s search matches these words.</p>
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="semantic-key">Key columns</Label>
        <Input
          id="semantic-key"
          value={keyColumns}
          onChange={(event) => setKeyColumns(event.target.value)}
          placeholder="TripId"
          className="h-8 font-mono text-[12px]"
          data-testid="semantic-key"
        />
        <p className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
          The columns identifying one row, in order. Every one must be allowed.
          {detail.interpretedKeyColumns !== null && (
            <>
              <span>{`Interpreted from the code (${detail.interpretedKeyOrigin?.toLowerCase() ?? "unknown"}):`}</span>
              <Mono>{detail.interpretedKeyColumns}</Mono>
              <Button variant="ghost" size="xs" onClick={() => setKeyColumns(detail.interpretedKeyColumns ?? "")}>
                Use it
              </Button>
            </>
          )}
        </p>
      </div>
      <div className="flex items-center gap-3">
        <Button size="sm" onClick={() => save.mutate()} disabled={!dirty || save.isPending} data-testid="semantic-about-save">
          {save.isPending && <Loader2 className="animate-spin" />}
          Save
        </Button>
        {annotation.updatedUtc !== null && (
          <span className="text-xs text-muted-foreground">
            {`Last changed by ${annotation.updatedBy ?? "unknown"} `}
            <RelativeTime value={annotation.updatedUtc} />
          </span>
        )}
      </div>
    </div>
  );
}

// ---- Columns -------------------------------------------------------------------------------------------------------

/**
 * One column: whether it is in the layer, and what it means. The switch saves immediately (the point of a toggle);
 * the text fields save when they lose focus, so typing does not fire a request per keystroke. Every save sends every
 * field, since the endpoint is a full upsert of the row. A column with no policy row yet renders unchecked: the
 * allow-list is default-deny.
 */
function ColumnRow({ objectKey, column }: { objectKey: string; column: ColumnPolicyState }) {
  const queryClient = useQueryClient();
  const storedSynonyms = formatList(column.synonyms);
  const [description, setDescription] = useState(column.description ?? "");
  const [synonyms, setSynonyms] = useState(storedSynonyms);
  const [reason, setReason] = useState(column.reason ?? "");

  useEffect(() => {
    setDescription(column.description ?? "");
    setSynonyms(storedSynonyms);
    setReason(column.reason ?? "");
  }, [column.description, storedSynonyms, column.reason]);

  const save = useMutation({
    mutationFn: (isAllowed: boolean) => columnPolicyApi.set({
      objectKey,
      columnName: column.columnName,
      isAllowed,
      reason: textOrNull(reason),
      description: textOrNull(description),
      synonyms: parseList(synonyms),
    }),
    onSuccess: () => refreshSemanticLayer(queryClient),
    onError: (error) => toast.error(errorText(error)),
  });

  const dirty = (column.description ?? "") !== description.trim()
    || storedSynonyms !== formatList(parseList(synonyms))
    || (column.reason ?? "") !== reason.trim();
  const saveIfDirty = () => {
    if (dirty && !save.isPending) {
      save.mutate(column.isAllowed);
    }
  };

  return (
    <TableRow data-testid="semantic-column-row">
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px] text-muted-foreground">{column.ordinal}</TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5">
        <span className="font-mono text-[12px] font-medium">{column.columnName}</span>
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5">
        <Mono>{column.dataType ?? "-"}</Mono>
        {column.nullable && <span className="ml-1 text-xs text-muted-foreground">null</span>}
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5">
        <div className="flex items-center gap-2">
          <Switch
            checked={column.isAllowed}
            disabled={save.isPending}
            onCheckedChange={(checked) => save.mutate(checked)}
            aria-label={`Allow ${column.columnName}`}
            data-testid="semantic-column-toggle"
          />
          {save.isPending && <Loader2 className="size-3.5 animate-spin text-muted-foreground" />}
        </div>
      </TableCell>
      <TableCell className="px-3 py-1.5">
        <Input
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          onBlur={saveIfDirty}
          placeholder="What it means"
          className="h-7 min-w-56 text-[12px]"
          disabled={save.isPending}
          data-testid="semantic-column-description"
        />
      </TableCell>
      <TableCell className="px-3 py-1.5">
        <Input
          value={synonyms}
          onChange={(event) => setSynonyms(event.target.value)}
          onBlur={saveIfDirty}
          placeholder="turnover, sales"
          className="h-7 min-w-40 text-[12px]"
          disabled={save.isPending}
          data-testid="semantic-column-synonyms"
        />
      </TableCell>
      <TableCell className="px-3 py-1.5">
        <Input
          value={reason}
          onChange={(event) => setReason(event.target.value)}
          onBlur={saveIfDirty}
          placeholder="Why (optional)"
          className="h-7 min-w-36 text-[12px]"
          disabled={save.isPending}
          data-testid="semantic-column-reason"
        />
      </TableCell>
      <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px] text-muted-foreground">
        {column.updatedBy ?? "-"} <RelativeTime value={column.updatedUtc} />
      </TableCell>
    </TableRow>
  );
}

const COLUMN_HEADERS = ["#", "Column", "Type", "Allowed", "Description", "Synonyms", "Reason", "Last changed"];

function ColumnsTab({ detail }: { detail: SemanticObjectAdmin }) {
  const queryClient = useQueryClient();
  const [confirmDenyAll, setConfirmDenyAll] = useState(false);

  const bulk = useMutation({
    mutationFn: (isAllowed: boolean) => columnPolicyApi.setObject({ objectKey: detail.key, isAllowed }),
    onSuccess: (_, isAllowed) => {
      toast.success(isAllowed ? `Every column of ${detail.name} is allowed.` : `Every column of ${detail.name} is denied.`);
      setConfirmDenyAll(false);
      refreshSemanticLayer(queryClient);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  if (detail.columns.length === 0) {
    return <EmptyState title="This object has no catalogued columns yet" description="A connected catalog sync fills the column dictionary." />;
  }

  return (
    <div className="flex flex-col gap-3" data-testid="semantic-columns">
      <div className="flex flex-wrap items-center gap-2">
        <p className="mr-auto text-[13px] text-muted-foreground">
          Only an allowed column is visible to the assistant and readable by its queries. A toggle saves at once; text
          saves when you leave the field.
        </p>
        <Button variant="outline" size="xs" onClick={() => bulk.mutate(true)} disabled={bulk.isPending} data-testid="semantic-allow-all">
          Allow all
        </Button>
        <Button variant="outline" size="xs" onClick={() => setConfirmDenyAll(true)} disabled={bulk.isPending} data-testid="semantic-deny-all">
          Deny all
        </Button>
      </div>
      <div className="overflow-x-auto rounded-md border">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {COLUMN_HEADERS.map((header) => (
                <TableHead key={header} className="h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground">
                  {header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {detail.columns.map((column) => (
              <ColumnRow key={column.columnName} objectKey={detail.key} column={column} />
            ))}
          </TableBody>
        </Table>
      </div>
      <ConfirmDialog
        open={confirmDenyAll}
        title={`Deny every column of ${detail.name}?`}
        message="The table leaves the semantic layer: the assistant can no longer see it, and every measure, relationship, and example query that reads it is withheld until columns are allowed again."
        confirmLabel="Deny all"
        danger
        busy={bulk.isPending}
        onConfirm={() => bulk.mutate(false)}
        onClose={() => setConfirmDenyAll(false)}
      />
    </div>
  );
}

// ---- Relationships -------------------------------------------------------------------------------------------------

function RelationshipsTab({ detail, table, inLayer }: { detail: SemanticObjectAdmin; table: PickedTable; inLayer: boolean }) {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<JoinDraft | null>(null);
  const [pendingDelete, setPendingDelete] = useState<SemanticCuratedJoin | null>(null);

  const remove = useMutation({
    mutationFn: (id: number) => semanticLayerApi.deleteRelationship(id),
    onSuccess: () => {
      toast.success("Relationship deleted.");
      setPendingDelete(null);
      refreshSemanticLayer(queryClient);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const curatedColumns: Column<SemanticCuratedJoin>[] = [
    {
      id: "join",
      header: "Join",
      render: (row) => (
        <span className="whitespace-normal break-words font-mono text-[12px]">
          {row.fromColumns.map((column, i) => `${row.fromObjectName}.${column} = ${row.toObjectName}.${row.toColumns[i] ?? "?"}`).join(" AND ")}
        </span>
      ),
    },
    { id: "type", header: "Type", render: (row) => <Badge variant="outline" className="text-[11px]">{row.joinType}</Badge>, width: 80 },
    { id: "description", header: "Description", render: (row) => row.description ?? <span className="text-muted-foreground">-</span> },
    { id: "status", header: "Status", render: (row) => <ServedBadge problem={row.problem} />, width: 100 },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      width: 100,
      render: (row) => (
        <div className="flex justify-end gap-1">
          <Button
            variant="ghost"
            size="icon"
            className="size-7"
            aria-label="Edit relationship"
            onClick={() => setDraft({
              id: row.id,
              from: { key: row.fromObjectKey, name: row.fromObjectName, database: null, schema: null },
              to: { key: row.toObjectKey, name: row.toObjectName, database: null, schema: null },
              pairs: row.fromColumns.map((column, i) => ({ from: column, to: row.toColumns[i] ?? "" })),
              joinType: row.joinType,
              description: row.description ?? "",
            })}
          >
            <Pencil />
          </Button>
          <Button variant="ghost" size="icon" className="size-7" aria-label="Delete relationship" onClick={() => setPendingDelete(row)}>
            <Trash2 />
          </Button>
        </div>
      ),
    },
  ];

  const discoveredColumns: Column<SemanticDiscoveredJoin>[] = [
    {
      id: "other",
      header: "Table",
      render: (row) => <Mono>{qualifiedName(row.join.otherDatabase, row.join.otherSchema, row.join.otherName)}</Mono>,
    },
    { id: "on", header: "Join on", render: (row) => <span className="whitespace-normal break-words font-mono text-[12px]">{row.join.on}</span> },
    {
      id: "types",
      header: "Used as",
      render: (row) => (
        <span className="flex flex-wrap gap-1">
          {row.join.joinTypes.map((type) => <Badge key={type} variant="outline" className="text-[11px]">{type}</Badge>)}
          {row.join.isRangeJoin && <Badge variant="outline" className="text-[11px]">range</Badge>}
        </span>
      ),
      width: 130,
    },
    { id: "seen", header: "Seen in", render: (row) => `${row.join.occurrences ?? 0} script${row.join.occurrences === 1 ? "" : "s"}`, width: 100 },
    { id: "status", header: "Status", render: (row) => <ServedBadge problem={row.problem} />, width: 100 },
  ];

  return (
    <div className="flex flex-col gap-5" data-testid="semantic-relationships">
      <section className="flex flex-col gap-2">
        <div className="flex items-center gap-2">
          <h3 className="mr-auto text-sm font-medium">{`Curated (${detail.curatedJoins.length})`}</h3>
          <Button
            size="xs"
            variant="outline"
            disabled={!inLayer}
            onClick={() => setDraft({ id: null, from: table, to: null, pairs: [], joinType: "Inner", description: "" })}
            data-testid="semantic-add-relationship"
          >
            <Plus />
            Add relationship
          </Button>
        </div>
        <DataTable<SemanticCuratedJoin>
          columns={curatedColumns}
          rows={detail.curatedJoins}
          rowKey={(row) => row.id}
          emptyMessage={inLayer
            ? "No relationship declared. Add one to tell the assistant exactly how this table joins."
            : "Allow at least one column to bring this table into the layer before declaring its joins."}
          data-testid="semantic-curated-joins"
        />
      </section>
      <section className="flex flex-col gap-2">
        <h3 className="text-sm font-medium">{`Discovered from the code (${detail.discoveredJoins.length})`}</h3>
        <p className="text-[13px] text-muted-foreground">
          Joins the codebase&apos;s own SQL makes. One is served only when both tables are in the layer and every column
          it joins on is allowed.
        </p>
        <DataTable<SemanticDiscoveredJoin>
          columns={discoveredColumns}
          rows={detail.discoveredJoins}
          rowKey={(row) => `${row.join.otherObjectKey}|${row.join.ownColumns.join(",")}|${row.join.otherColumns.join(",")}`}
          emptyMessage="Nothing in the codebase joins this table to another."
          data-testid="semantic-discovered-joins"
        />
      </section>

      {draft !== null && <JoinDialog draft={draft} onClose={() => setDraft(null)} />}
      <ConfirmDialog
        open={pendingDelete !== null}
        title="Delete this relationship?"
        message="The assistant stops being told about this join. Joins discovered from the code are unaffected."
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => pendingDelete !== null && remove.mutate(pendingDelete.id)}
        onClose={() => setPendingDelete(null)}
      />
    </div>
  );
}

// ---- Measures ------------------------------------------------------------------------------------------------------

/** The measures table with edit and delete, shared by one table's Measures tab and the layer-wide measure list. */
export function MeasuresTable({
  measures, anchor, onOpenObject, emptyMessage, testId,
}: {
  measures: SemanticMeasureAdmin[];
  /** Fixes the anchor of measures edited from here (a table's own tab); null for the layer-wide list. */
  anchor: PickedTable | null;
  /** Opens a measure's table; omitted where the table is already the one shown. */
  onOpenObject?: (key: string) => void;
  emptyMessage: string;
  testId: string;
}) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<SemanticMeasureAdmin | null>(null);
  const [pendingDelete, setPendingDelete] = useState<SemanticMeasureAdmin | null>(null);

  const remove = useMutation({
    mutationFn: (id: number) => semanticLayerApi.deleteMeasure(id),
    onSuccess: () => {
      toast.success("Measure deleted.");
      setPendingDelete(null);
      refreshSemanticLayer(queryClient);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const columns: Column<SemanticMeasureAdmin>[] = [
    { id: "name", header: "Name", render: (row) => <span className="font-mono text-[12px] font-medium">{row.name}</span> },
    ...(onOpenObject === undefined ? [] : [{
      id: "table",
      header: "Table",
      render: (row: SemanticMeasureAdmin) => (
        <button type="button" className="font-mono text-[12px] text-primary hover:underline" onClick={() => onOpenObject(row.objectKey)}>
          {row.objectName}
        </button>
      ),
    }]),
    { id: "expression", header: "Expression", render: (row) => <span className="whitespace-normal break-words font-mono text-[12px]">{row.expression}</span> },
    { id: "description", header: "Description", render: (row) => row.description ?? <span className="text-muted-foreground">-</span> },
    { id: "status", header: "Status", render: (row) => <ServedBadge problem={row.problem} />, width: 100 },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      width: 100,
      render: (row) => (
        <div className="flex justify-end gap-1">
          <Button variant="ghost" size="icon" className="size-7" aria-label={`Edit ${row.name}`} onClick={() => setEditing(row)}>
            <Pencil />
          </Button>
          <Button variant="ghost" size="icon" className="size-7" aria-label={`Delete ${row.name}`} onClick={() => setPendingDelete(row)}>
            <Trash2 />
          </Button>
        </div>
      ),
    },
  ];

  return (
    <>
      <DataTable<SemanticMeasureAdmin>
        columns={columns}
        rows={measures}
        rowKey={(row) => row.id}
        emptyMessage={emptyMessage}
        data-testid={testId}
      />
      {editing !== null && <MeasureDialog measure={editing} anchor={anchor} onClose={() => setEditing(null)} />}
      <ConfirmDialog
        open={pendingDelete !== null}
        title={`Delete ${pendingDelete?.name ?? "this measure"}?`}
        message="The assistant stops being offered this definition."
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => pendingDelete !== null && remove.mutate(pendingDelete.id)}
        onClose={() => setPendingDelete(null)}
      />
    </>
  );
}

function MeasuresTab({ detail, table, inLayer }: { detail: SemanticObjectAdmin; table: PickedTable; inLayer: boolean }) {
  const [creating, setCreating] = useState(false);
  return (
    <div className="flex flex-col gap-2" data-testid="semantic-measures">
      <div className="flex items-center gap-2">
        <p className="mr-auto text-[13px] text-muted-foreground">
          Named expressions over this table&apos;s allowed columns, reused verbatim by the assistant.
        </p>
        <Button size="xs" variant="outline" disabled={!inLayer} onClick={() => setCreating(true)} data-testid="semantic-add-measure">
          <Plus />
          Add measure
        </Button>
      </div>
      <MeasuresTable
        measures={detail.measures}
        anchor={table}
        emptyMessage={inLayer ? "No measure is defined over this table." : "Allow at least one column before defining measures."}
        testId="semantic-object-measures"
      />
      {creating && <MeasureDialog measure={null} anchor={table} onClose={() => setCreating(false)} />}
    </div>
  );
}

// ---- Examples ------------------------------------------------------------------------------------------------------

function ExamplesTab({ detail }: { detail: SemanticObjectAdmin }) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<SemanticExampleAdmin | null>(null);
  const [pendingDelete, setPendingDelete] = useState<SemanticExampleAdmin | null>(null);

  const remove = useMutation({
    mutationFn: (id: number) => semanticLayerApi.deleteExample(id),
    onSuccess: () => {
      toast.success("Saved answer deleted.");
      setPendingDelete(null);
      refreshSemanticLayer(queryClient);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  if (detail.examples.length === 0) {
    return (
      <EmptyState
        title="No example query reads this table yet"
        description="Examples are saved answers: questions someone confirmed an assistant answer to, matched to the tables their SQL reads."
      />
    );
  }

  return (
    <div className="flex flex-col gap-3" data-testid="semantic-examples">
      <p className="text-[13px] text-muted-foreground">
        Questions already answered with SQL reading this table. The assistant is shown the ones that pass the column
        allow-list, as precedent to mirror.
      </p>
      {detail.examples.map((example) => (
        <div key={example.id} className="flex flex-col gap-2 rounded-md border p-3" data-testid="semantic-example">
          <div className="flex flex-wrap items-center gap-2">
            <span className="mr-auto text-[13px] font-medium">{example.question}</span>
            <Badge variant="outline" className="text-[11px]">{example.provenance}</Badge>
            <ServedBadge problem={example.problem} />
            <Button
              variant="ghost"
              size="icon"
              className="size-7"
              aria-label={`Edit the saved answer to "${example.question}"`}
              onClick={() => setEditing(example)}
              data-testid="semantic-example-edit"
            >
              <Pencil />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              className="size-7"
              aria-label={`Delete the saved answer to "${example.question}"`}
              onClick={() => setPendingDelete(example)}
              data-testid="semantic-example-delete"
            >
              <Trash2 />
            </Button>
          </div>
          <pre className="overflow-x-auto whitespace-pre-wrap rounded-md bg-muted p-2 font-mono text-[12px]">{example.sql}</pre>
          <span className="text-xs text-muted-foreground">
            {example.confirmedBy === null ? "Confirmed " : `Confirmed by ${example.confirmedBy} `}
            <RelativeTime value={example.confirmedUtc} />
          </span>
        </div>
      ))}
      {editing !== null && <ExampleDialog example={editing} onClose={() => setEditing(null)} />}
      <ConfirmDialog
        open={pendingDelete !== null}
        title="Delete this saved answer?"
        message="The assistant stops reusing this query, and questions that matched it are answered from scratch again."
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => pendingDelete !== null && remove.mutate(pendingDelete.id)}
        onClose={() => setPendingDelete(null)}
      />
    </div>
  );
}

// ---- Power BI ------------------------------------------------------------------------------------------------------

/** What Power BI reports define on this table: read-only, since each report is the source of its definitions. */
function PowerBiTab({ detail }: { detail: SemanticObjectAdmin }) {
  if (detail.reportModels.length === 0) {
    return (
      <EmptyState
        title="No Power BI report loads from this table"
        description="A report's model appears here once a sync that ran pbix-extract has read it and one of its model tables resolved to this warehouse table."
      />
    );
  }

  return (
    <div className="flex flex-col gap-4" data-testid="semantic-report-models">
      <p className="text-[13px] text-muted-foreground">
        Measures, calculated columns, and relationships Power BI reports build on this table. The assistant is served only
        those whose every column is allowed; a column the report renamed counts as not allowed.
      </p>
      {detail.reportModels.map((model) => (
        <section
          key={`${model.subscriberKey}#${model.reportFile}#${model.modelTable}`}
          className="flex flex-col gap-3 rounded-md border p-3"
          data-testid="semantic-report-model"
        >
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[13px] font-medium">{model.subscriberName}</span>
            <span className="font-mono text-[12px] text-muted-foreground">{model.reportFile}</span>
            <Badge variant="outline" className="text-[11px]">{`model table ${model.modelTable}`}</Badge>
          </div>
          {model.fields.map((field) => (
            <div key={`${field.kind}:${field.name}`} className="flex flex-col gap-1" data-testid="semantic-report-field">
              <div className="flex flex-wrap items-center gap-2">
                <span className="mr-auto font-mono text-[12px] font-medium">{field.name}</span>
                <Badge variant="secondary" className="text-[11px]">
                  {field.kind === "measure" ? "measure" : "calculated column"}
                </Badge>
                <ServedBadge problem={field.problem} />
              </div>
              {field.description !== null && <p className="text-xs text-muted-foreground">{field.description}</p>}
              <pre className="overflow-x-auto whitespace-pre-wrap rounded-md bg-muted p-2 font-mono text-[12px]">{field.expression}</pre>
            </div>
          ))}
          {model.relationships.map((relationship) => (
            <div
              key={`${relationship.ownColumn ?? ""}>${relationship.otherModelTable}.${relationship.otherColumn ?? ""}`}
              className="flex flex-wrap items-center gap-2"
              data-testid="semantic-report-relationship"
            >
              <span className="mr-auto font-mono text-[12px]">
                {`${relationship.modelTable}[${relationship.ownColumn ?? "?"}] to ${relationship.otherModelTable}[${relationship.otherColumn ?? "?"}]`}
              </span>
              {relationship.cardinality !== null && (
                <Badge variant="secondary" className="text-[11px]">{relationship.cardinality}</Badge>
              )}
              {!relationship.isActive && <Badge variant="outline" className="text-[11px]">inactive</Badge>}
              <ServedBadge problem={relationship.problem} />
            </div>
          ))}
        </section>
      ))}
    </div>
  );
}

// ---- The panel -----------------------------------------------------------------------------------------------------

/**
 * The semantic layer editor for one object: whether it is in the layer (its allowed columns) and the business
 * context an assistant is served with it, across About / Columns / Relationships / Measures / Examples. Each
 * annotation shows whether it is currently served or withheld (and why), because a column denied later silently
 * withdraws everything that names it.
 */
export function SemanticObjectPanel({ objectKey }: { objectKey: string }) {
  const [tab, setTab] = useState<ObjectTab>("columns");
  useEffect(() => setTab("columns"), [objectKey]);

  const query = useQuery({
    queryKey: [SEMANTIC_ROOT, "object", objectKey],
    queryFn: () => semanticLayerApi.object(objectKey),
  });

  if (query.isPending) {
    return (
      <div className="flex flex-col gap-2">
        <Skeleton className="h-8 w-72 max-w-full" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="mt-2 h-64 w-full" />
      </div>
    );
  }
  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{errorText(query.error)}</p>;
  }

  const detail = query.data;
  const allowedCount = detail.columns.filter((column) => column.isAllowed).length;
  const inLayer = allowedCount > 0;
  const table: PickedTable = { key: detail.key, name: detail.name, database: detail.database, schema: detail.schema };
  const servedKey = detail.annotation.keyColumns.length > 0;

  return (
    <div data-testid="semantic-object-panel">
      <div className="mb-1 flex flex-wrap items-center gap-2">
        <h2 className="min-w-0 break-words font-mono text-base font-medium">{detail.name}</h2>
        <Badge variant="secondary">{detail.kind}</Badge>
        <Badge variant={inLayer ? "default" : "outline"} data-testid="semantic-object-coverage">
          {inLayer ? `In the layer: ${allowedCount}/${detail.columns.length} columns` : "Not in the layer"}
        </Badge>
        {servedKey && (
          <span className="flex items-center gap-1 text-xs text-muted-foreground">
            <KeyRound className="size-3.5 text-warning" />
            <Mono>{formatList(detail.annotation.keyColumns)}</Mono>
          </span>
        )}
        <div className="grow" />
        <Button variant="outline" size="xs" asChild>
          <Link to={`/catalog?node=${encodeURIComponent(encodeNodeId({ type: "object", objectKey: detail.key }))}`}>
            Open in catalog
          </Link>
        </Button>
      </div>
      <p className="mb-1 font-mono text-xs text-muted-foreground">{qualifiedName(detail.database, detail.schema, detail.name)}</p>
      {detail.annotation.businessName !== null && (
        <p className="mb-4 text-[13px]">{detail.annotation.businessName}</p>
      )}

      <Tabs value={tab} onValueChange={(value) => setTab(value as ObjectTab)} className="mt-3 gap-4">
        <TabsList variant="line">
          <TabsTrigger value="about" data-testid="semantic-tab-about">About</TabsTrigger>
          <TabsTrigger value="columns" data-testid="semantic-tab-columns">{`Columns (${allowedCount}/${detail.columns.length})`}</TabsTrigger>
          <TabsTrigger value="relationships" data-testid="semantic-tab-relationships">
            {`Relationships (${detail.curatedJoins.length})`}
          </TabsTrigger>
          <TabsTrigger value="measures" data-testid="semantic-tab-measures">{`Measures (${detail.measures.length})`}</TabsTrigger>
          <TabsTrigger value="examples" data-testid="semantic-tab-examples">{`Examples (${detail.examples.length})`}</TabsTrigger>
          <TabsTrigger value="powerbi" data-testid="semantic-tab-powerbi">{`Power BI (${detail.reportModels.length})`}</TabsTrigger>
        </TabsList>
        <TabsContent value="about"><AboutTab detail={detail} /></TabsContent>
        <TabsContent value="columns"><ColumnsTab detail={detail} /></TabsContent>
        <TabsContent value="relationships"><RelationshipsTab detail={detail} table={table} inLayer={inLayer} /></TabsContent>
        <TabsContent value="measures"><MeasuresTab detail={detail} table={table} inLayer={inLayer} /></TabsContent>
        <TabsContent value="examples"><ExamplesTab detail={detail} /></TabsContent>
        <TabsContent value="powerbi"><PowerBiTab detail={detail} /></TabsContent>
      </Tabs>
    </div>
  );
}
