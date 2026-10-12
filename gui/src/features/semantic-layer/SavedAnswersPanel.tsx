import { useCallback, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ChartColumn, FileCode2, Trash2, UserCheck, type LucideIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { semanticLayerApi } from "../../api/endpoints";
import type { SemanticExampleAdmin } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { DetailPair } from "../../components/DetailPair";
import { IconAction } from "../../components/IconAction";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { RichTooltip } from "../../components/RichTooltip";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";
import { useDebouncedValue } from "../../hooks/useDebouncedValue";
import { tableContainer, useScrollFit } from "../../hooks/useScrollFit";
import { useOwnedPanel } from "../../layout/workbench/useOwnedPanel";
import { SqlBlock } from "./AiContextDetail";
import { CopyQuestion, ServedMark, Stamp, ToneChip } from "./parts";
import { errorText, FIT_BELOW, FIT_MIN, FITTED_GRID, refreshSemanticLayer, SEMANTIC_ROOT } from "./shared";

/** The bottom panel content ids this surface owns. */
const PANEL = "saved-answer:";

/** The height of the paging row under the grid, which the fitted rows leave room for. */
const PAGER_HEIGHT = 41;

/** The provenance of an answer an AI context gives: removed from its file, never here. */
const FROM_CONTEXT = "ai-context";

/** Where a saved answer came from, as a word and a glyph; an unknown provenance shows as it is. */
const provenances: Record<string, { icon: LucideIcon; label: string }> = {
  "user-confirmed": { icon: UserCheck, label: "Confirmed" },
  powerbi: { icon: ChartColumn, label: "Power BI" },
  [FROM_CONTEXT]: { icon: FileCode2, label: "AI context" },
};

function ProvenanceChip({ provenance }: { provenance: string }) {
  const known = provenances[provenance];
  return (
    <span className="inline-flex" data-testid="saved-answer-provenance" data-provenance={provenance}>
      <ToneChip icon={known?.icon ?? FileCode2} tone="text-muted-foreground">{known?.label ?? provenance}</ToneChip>
    </span>
  );
}

/** One saved answer in the bottom panel: the question, its query in full, where it came from, and whether it is served. */
function SavedAnswerDetail({ row, canRemove, onRemove }: { row: SemanticExampleAdmin; canRemove: boolean; onRemove: () => void }) {
  return (
    <div className="flex flex-col gap-3 px-4 py-3" data-testid="saved-answer-detail">
      <div className="flex flex-wrap items-start gap-x-3 gap-y-1.5">
        <div className="flex min-w-0 flex-[1_1_16rem] items-start gap-1">
          <h2 className="min-w-0 break-words text-sm font-medium">{row.question}</h2>
          <CopyQuestion question={row.question} />
        </div>
        <ProvenanceChip provenance={row.provenance} />
        <ServedMark problem={row.problem} />
        {canRemove && row.provenance !== FROM_CONTEXT && (
          <Button variant="outline" size="xs" onClick={onRemove} data-testid="saved-answer-detail-remove">
            <Trash2 />
            Remove
          </Button>
        )}
      </div>
      {row.provenance === FROM_CONTEXT && (
        <p className="text-[12px] text-muted-foreground">
          An AI context file gives this answer. To change or remove it, edit that file: removing it takes effect at the
          next sync, and a change once an admin approves the context.
        </p>
      )}
      {row.problem != null && <p className="text-[12px] text-muted-foreground">{`Not served: ${row.problem}`}</p>}
      <div className="grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-x-6 gap-y-2">
        <DetailPair label="Confirmed">
          {row.confirmedBy ?? "unknown"} <span className="text-muted-foreground"><RelativeTime value={row.confirmedUtc} /></span>
        </DetailPair>
        <DetailPair label="Datasource">
          {row.sourceRef == null
            ? <span className="text-muted-foreground">worked out from the tables it reads</span>
            : <span className="font-mono text-[12px]">{row.sourceRef}</span>}
        </DetailPair>
      </div>
      <SqlBlock sql={row.sql} testId="saved-answer-sql" />
    </div>
  );
}

/**
 * Every saved answer: the question/query pairs people confirmed, a report gave, or an AI context file gives, which the
 * assistant reuses as the example queries of the tables they read (and runs without asking when a new question matches
 * one closely). Read-only: a row opens the answer in the bottom panel with its query in full. An admin may remove an
 * answer a person or a report gave; one an AI context gives is removed from its file.
 */
export function SavedAnswersPanel() {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const admin = hasScope("admin");
  const [search, setSearch] = useState("");
  const term = useDebouncedValue(search.trim(), 300);
  const [pendingRemove, setPendingRemove] = useState<SemanticExampleAdmin | null>(null);
  const { ownedId, show, close } = useOwnedPanel(PANEL);
  const grid = useRef<HTMLDivElement>(null);
  useScrollFit(grid, FIT_BELOW + PAGER_HEIGHT, FIT_MIN, tableContainer);

  const remove = useMutation({
    mutationFn: (id: number) => semanticLayerApi.deleteExample(id),
    onSuccess: (_, id) => {
      toast.success("Saved answer removed.", { description: "The assistant no longer reuses it." });
      if (ownedId === String(id)) {
        close();
      }
    },
    onError: (error) => toast.error(`The saved answer was not removed: ${errorText(error)}`),
    onSettled: () => {
      setPendingRemove(null);
      refreshSemanticLayer(queryClient);
    },
  });

  const open = useCallback((row: SemanticExampleAdmin) => show(
    String(row.id),
    "Saved answer",
    <SavedAnswerDetail row={row} canRemove={admin} onRemove={() => setPendingRemove(row)} />,
  ), [show, admin]);

  const columns: Column<SemanticExampleAdmin>[] = [
    {
      id: "question",
      header: "Question",
      fill: true,
      floor: 150,
      render: (row) => (
        <span className="flex min-w-0 items-center gap-1">
          <span className="min-w-0"><TruncatedText text={row.question} maxWidth={2000} className="font-medium" /></span>
          <CopyQuestion question={row.question} reveal />
        </span>
      ),
    },
    {
      id: "sql",
      header: "Query",
      fill: true,
      floor: 100,
      render: (row) => <TruncatedText text={row.sql} maxWidth={2000} mono className="text-muted-foreground" />,
    },
    { id: "provenance", header: "Source", render: (row) => <ProvenanceChip provenance={row.provenance} /> },
    { id: "served", header: "Served", align: "center", render: (row) => <ServedMark problem={row.problem} compact testId="saved-answer-served" /> },
    { id: "confirmed", header: "Confirmed", render: (row) => <Stamp who={row.confirmedBy} when={row.confirmedUtc} maxWidth={170} /> },
    ...(admin ? [{
      id: "actions",
      header: "",
      align: "right" as const,
      render: (row: SemanticExampleAdmin) => row.provenance === FROM_CONTEXT
        ? (
          <RichTooltip body="An AI context file gives this answer. Remove it from its context file." title="From an AI context">
            <span className="inline-flex size-6 items-center justify-center text-muted-foreground" data-testid="saved-answer-from-context">
              <FileCode2 className="size-4" aria-label="Given by an AI context file" />
            </span>
          </RichTooltip>
        )
        : (
          <IconAction
            label="Remove this saved answer"
            icon={<Trash2 />}
            onClick={(event) => {
              event.stopPropagation();
              setPendingRemove(row);
            }}
            data-testid="saved-answer-delete"
          />
        ),
    }] : []),
  ];

  return (
    <div className="flex flex-col gap-3" data-testid="semantic-saved-answers-panel">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Search questions and queries"
          label="Search saved answers"
          testId="saved-answers-search"
        />
        <p className="min-w-0 flex-[1_1_20rem] text-[12px] text-muted-foreground">
          Queries confirmed as the right answer to a question. The assistant reuses them for the tables they read.
        </p>
      </div>
      <div ref={grid} className={FITTED_GRID}>
        <PagedTable<SemanticExampleAdmin>
          queryKey={[SEMANTIC_ROOT, "examples", term]}
          fetchPage={(page, pageSize) => semanticLayerApi.examples({ search: term === "" ? undefined : term, page, pageSize })}
          columns={columns}
          rowKey={(row) => row.id}
          onRowClick={open}
          rowSx={(row) => (String(row.id) === ownedId ? { backgroundColor: "var(--accent)" } : undefined)}
          emptyMessage={term === ""
            ? "No answers have been saved yet. One is saved when someone confirms an assistant answer, a report gives one, or an approved AI context does."
            : "No saved answer matches this search."}
          data-testid="saved-answers-table"
        />
      </div>
      <ConfirmDialog
        open={pendingRemove !== null}
        title="Remove this saved answer?"
        message="The assistant stops reusing this query, and questions that matched it are answered from scratch again."
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => pendingRemove !== null && remove.mutate(pendingRemove.id)}
        onClose={() => setPendingRemove(null)}
      />
    </div>
  );
}
