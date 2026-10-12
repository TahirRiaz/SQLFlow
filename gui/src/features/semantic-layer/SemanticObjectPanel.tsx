import { useRef, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { CircleCheck, CircleSlash, KeyRound, Lock, Sparkles } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { semanticLayerApi } from "../../api/endpoints";
import type { ColumnPolicyState, SemanticObjectAdmin } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { tableContainer, useScrollFit } from "../../hooks/useScrollFit";
import { encodeNodeId } from "../catalog/nodeIds";
import { ExampleDisclosure } from "./AiContextDetail";
import { CellText, DescribedText, ServedMark, ToneChip } from "./parts";
import { errorText, FIT_MIN, FITTED_GRID, plural, qualifiedName, SEMANTIC_ROOT } from "./shared";

/** The explorer pane's own padding, which the fitted columns leave under them. */
const PANE_PADDING = 12;

/** A part of the object's detail: a caption with its count over the content, or one quiet line when there is nothing. */
function Part({ title, count, empty, children, testId }: {
  title: string;
  count: number;
  /** What the line says when the part holds nothing. */
  empty: string;
  children: ReactNode;
  testId: string;
}) {
  return (
    <section className="flex flex-col gap-1.5" data-testid={testId}>
      <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        {title}
        <span className={count === 0 ? "ml-1.5 font-mono" : "ml-1.5 font-mono text-foreground"}>{count}</span>
      </h3>
      {count === 0 ? <p className="text-[12px] text-muted-foreground">{empty}</p> : children}
    </section>
  );
}

/** Whether the assistant may use a column, as a glyph with the word on hover; a key column carries the key glyph too. */
function OpenMark({ column, isKey }: { column: ColumnPolicyState; isKey: boolean }) {
  const label = column.isAllowed
    ? `Open to the assistant${column.context == null ? "" : ` through AI context '${column.context}'`}`
    : "Closed: no approved AI context opens it";
  return (
    <span className="inline-flex items-center gap-1">
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="inline-flex" data-testid="semantic-column-open" data-open={column.isAllowed ? "true" : "false"}>
            {column.isAllowed
              ? <CircleCheck className="size-3.5 text-success" aria-label={label} />
              : <Lock className="size-3.5 text-muted-foreground" aria-label={label} />}
          </span>
        </TooltipTrigger>
        <TooltipContent>{label}</TooltipContent>
      </Tooltip>
      {isKey && (
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="inline-flex"><KeyRound className="size-3.5 text-warning" aria-label="Key column" /></span>
          </TooltipTrigger>
          <TooltipContent>Key column</TooltipContent>
        </Tooltip>
      )}
    </span>
  );
}

/**
 * The object's columns, compact enough to fit the explorer's pane: name, type, whether it is open, what it means. The rows
 * scroll inside the room the pane shows under the object's header, so the first screen of the pane is the columns.
 */
function ColumnsGrid({ detail }: { detail: SemanticObjectAdmin }) {
  const grid = useRef<HTMLDivElement>(null);
  useScrollFit(grid, PANE_PADDING, FIT_MIN, tableContainer);
  const key = new Set(detail.annotation.keyColumns.map((column) => column.toLowerCase()));
  const columns: Column<ColumnPolicyState>[] = [
    {
      id: "name",
      header: "Column",
      render: (row) => <CellText text={row.columnName} maxWidth={136} mono className="font-medium" />,
    },
    {
      id: "type",
      header: "Type",
      render: (row) => (
        <CellText text={`${row.dataType ?? "-"}${row.nullable ? " null" : ""}`} maxWidth={88} mono className="text-muted-foreground" />
      ),
    },
    {
      id: "open",
      header: "In context",
      render: (row) => <OpenMark column={row} isKey={key.has(row.columnName.toLowerCase())} />,
    },
    {
      id: "description",
      header: "Description",
      fill: true,
      floor: 80,
      render: (row) => <DescribedText description={row.description} synonyms={row.synonyms} />,
    },
  ];

  return (
    <div ref={grid} className={FITTED_GRID}>
      <DataTable<ColumnPolicyState>
        columns={columns}
        rows={detail.columns}
        rowKey={(row) => row.columnName}
        emptyMessage="This object has no catalogued columns yet; a connected catalog sync fills the column dictionary."
        data-testid="semantic-columns"
      />
    </div>
  );
}

/** One served-or-not line: what it is in mono, a quiet note under it, and whether the assistant is given it. */
function ServedLine({ text, note, problem, testId }: { text: string; note?: ReactNode; problem: string | null; testId: string }) {
  return (
    <div className="flex min-w-0 flex-col gap-0.5" data-testid={testId}>
      <div className="flex min-w-0 items-start gap-2">
        <span className="min-w-0 flex-1 break-words font-mono text-[12px]">{text}</span>
        <span className="shrink-0"><ServedMark problem={problem} /></span>
      </div>
      {note !== undefined && <span className="text-[12px] text-muted-foreground">{note}</span>}
    </div>
  );
}

/**
 * What the assistant may use of one object, read-only: whether an approved AI context serves it (and which), its columns
 * with which are open, what its context says it means, and the joins, measures, example queries and Power BI models
 * around it, each with whether it is served and, when it is not, why. Changing any of it is done in the context's file:
 * what the file takes away stops at the next sync, and what it adds or changes waits for an admin's approval.
 */
export function SemanticObjectPanel({ objectKey }: { objectKey: string }) {
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
  const { annotation } = detail;
  const openCount = detail.columns.filter((column) => column.isAllowed).length;
  const fieldCount = detail.reportModels.reduce((sum, model) => sum + model.fields.length + model.relationships.length, 0);

  return (
    <div className="flex flex-col gap-4" data-testid="semantic-object-panel">
      <div className="flex flex-col gap-1" data-testid="semantic-object-header">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="min-w-0 break-words font-mono text-base font-medium">{detail.name}</h2>
          <Badge variant="secondary" className="text-[11px]">{detail.kind}</Badge>
          <span data-testid="semantic-object-context">
            {detail.context == null
              ? <ToneChip icon={CircleSlash} tone="text-muted-foreground">Not in any AI context</ToneChip>
              : <ToneChip icon={Sparkles} tone="text-success">{`In AI context '${detail.context}'`}</ToneChip>}
          </span>
          <div className="grow" />
          <Button variant="outline" size="xs" asChild>
            <Link to={`/catalog?node=${encodeURIComponent(encodeNodeId({ type: "object", objectKey: detail.key }))}`}>
              Open in catalog
            </Link>
          </Button>
        </div>
        <p className="break-words text-xs text-muted-foreground">
          <span className="break-all font-mono">{qualifiedName(detail.database, detail.schema, detail.name)}</span>
          {annotation.businessName != null && <span>{`, known as ${annotation.businessName}`}</span>}
        </p>
        <p className="text-[12px] text-muted-foreground" data-testid="semantic-object-source">
          {detail.context == null
            ? "No approved AI context lists this table, so the assistant cannot use it. To open it, list it under tables: in an AI context file and approve that context."
            : `${plural(openCount, "column")} of ${detail.columns.length} open. To change what the assistant may use here, edit the file of AI context '${detail.context}'; what it takes away stops at the next sync, and what it adds waits until an admin approves it.`}
        </p>
      </div>

      <Part title="Columns" count={detail.columns.length} empty="No catalogued columns yet." testId="semantic-columns-part">
        <ColumnsGrid detail={detail} />
      </Part>

      {(annotation.description != null || annotation.synonyms.length > 0
        || annotation.keyColumns.length > 0 || detail.interpretedKeyColumns != null) && (
        <div className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-x-6 gap-y-2" data-testid="semantic-about">
          {annotation.synonyms.length > 0 && <DetailPair label="Synonyms">{annotation.synonyms.join(", ")}</DetailPair>}
          {annotation.keyColumns.length > 0 && (
            <DetailPair label="Key"><span className="font-mono text-[12px]">{annotation.keyColumns.join(", ")}</span></DetailPair>
          )}
          {detail.interpretedKeyColumns != null && (
            <DetailPair label={`Key in the code (${detail.interpretedKeyOrigin?.toLowerCase() ?? "unknown"})`}>
              <span className="font-mono text-[12px]">{detail.interpretedKeyColumns}</span>
            </DetailPair>
          )}
          {annotation.description != null && (
            <div className="col-span-full">
              <DetailPair label="Description"><span className="whitespace-pre-wrap">{annotation.description}</span></DetailPair>
            </div>
          )}
        </div>
      )}

      <Part
        title="Joins"
        count={detail.curatedJoins.length + detail.discoveredJoins.length}
        empty="No join declared in a context, and nothing in the codebase joins this table to another."
        testId="semantic-relationships"
      >
        {detail.curatedJoins.map((join) => (
          <ServedLine
            key={join.id}
            text={join.fromColumns.map((column, i) => `${join.fromObjectName}.${column} = ${join.toObjectName}.${join.toColumns[i] ?? "?"}`).join(" and ")}
            note={[join.joinType === "Left" ? "Left join" : "Inner join", join.description]
              .filter((part): part is string => part != null).join(". ")}
            problem={join.problem}
            testId="semantic-curated-join"
          />
        ))}
        {detail.discoveredJoins.map((row) => (
          <ServedLine
            key={`${row.join.otherObjectKey}|${row.join.ownColumns.join(",")}|${row.join.otherColumns.join(",")}`}
            text={`${qualifiedName(row.join.otherDatabase, row.join.otherSchema, row.join.otherName)} on ${row.join.on}`}
            note={`Found in ${plural(row.join.occurrences ?? 0, "script")} of the codebase (${[...row.join.joinTypes, ...(row.join.isRangeJoin ? ["range"] : [])].join(", ")})`}
            problem={row.problem}
            testId="semantic-discovered-join"
          />
        ))}
      </Part>

      <Part title="Measures" count={detail.measures.length} empty="No measure is defined over this table." testId="semantic-measures">
        {detail.measures.map((measure) => (
          <ServedLine
            key={measure.id}
            text={`${measure.name} = ${measure.expression}`}
            note={measure.description ?? undefined}
            problem={measure.problem}
            testId="semantic-measure"
          />
        ))}
      </Part>

      <Part
        title="Examples"
        count={detail.examples.length}
        empty="No saved answer or context example reads this table."
        testId="semantic-examples"
      >
        {detail.examples.map((example) => (
          <ExampleDisclosure
            key={example.id}
            question={example.question}
            sql={example.sql}
            sourceRef={example.sourceRef}
            extra={<span className="shrink-0"><ServedMark problem={example.problem} /></span>}
            testId="semantic-example"
          />
        ))}
      </Part>

      <Part title="Power BI" count={fieldCount} empty="No Power BI report builds on this table." testId="semantic-report-models">
        {detail.reportModels.map((model) => (
          <div
            key={`${model.subscriberKey}#${model.reportFile}#${model.modelTable}`}
            className="flex flex-col gap-1.5 rounded-md border border-border p-2"
            data-testid="semantic-report-model"
          >
            <div className="flex flex-wrap items-baseline gap-x-2 text-[12px]">
              <span className="font-medium">{model.subscriberName}</span>
              <span className="font-mono text-muted-foreground">{`${model.reportFile}, model table ${model.modelTable}`}</span>
            </div>
            {model.fields.map((field) => (
              <ServedLine
                key={`${field.kind}:${field.name}`}
                text={`${field.name} = ${field.expression}`}
                note={[field.kind === "measure" ? "Measure" : "Calculated column", field.description]
                  .filter((part): part is string => part != null).join(". ")}
                problem={field.problem}
                testId="semantic-report-field"
              />
            ))}
            {model.relationships.map((relationship) => (
              <ServedLine
                key={`${relationship.ownColumn ?? ""}>${relationship.otherModelTable}.${relationship.otherColumn ?? ""}`}
                text={`${relationship.modelTable}[${relationship.ownColumn ?? "?"}] to ${relationship.otherModelTable}[${relationship.otherColumn ?? "?"}]`}
                note={[relationship.cardinality, relationship.isActive ? null : "inactive"].filter((part): part is string => part != null).join(", ") || undefined}
                problem={relationship.problem}
                testId="semantic-report-relationship"
              />
            ))}
          </div>
        ))}
      </Part>
    </div>
  );
}
