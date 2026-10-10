import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Pencil, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { semanticLayerApi } from "../../api/endpoints";
import type { SemanticExampleAdmin } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ExampleDialog } from "./ExampleDialog";
import { errorText, refreshSemanticLayer, SEMANTIC_ROOT, ServedBadge, useDebouncedValue } from "./shared";

/**
 * Every saved answer across the layer: the question/query pairs people confirmed, which the assistant reuses as the
 * example queries of the tables they read (and runs without asking when a new question matches one closely). Answers
 * are ADDED only by confirming one in a conversation; this tab is where an admin reviews what was stored, corrects a
 * query, or deletes an answer that should no longer be precedent. A table's own Examples tab shows the same answers for
 * that table.
 */
export function SavedAnswersPanel() {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState("");
  const term = useDebouncedValue(search.trim(), 300);
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

  const columns: Column<SemanticExampleAdmin>[] = [
    {
      id: "question",
      header: "Question",
      render: (row) => <span className="whitespace-normal break-words text-[13px] font-medium">{row.question}</span>,
    },
    {
      id: "sql",
      header: "Query",
      render: (row) => (
        <span className="line-clamp-2 whitespace-normal break-all font-mono text-[12px] text-muted-foreground">{row.sql}</span>
      ),
    },
    {
      id: "datasource",
      header: "Datasource",
      width: 200,
      render: (row) => row.sourceRef === null
        ? <span className="text-muted-foreground">-</span>
        : <span className="break-all font-mono text-[12px]">{row.sourceRef}</span>,
    },
    {
      id: "saved",
      header: "Saved by",
      width: 170,
      render: (row) => (
        <span className="text-[12px]">
          {row.confirmedBy ?? "-"}{" "}
          <span className="text-muted-foreground"><RelativeTime value={row.confirmedUtc} /></span>
        </span>
      ),
    },
    { id: "status", header: "Status", width: 100, render: (row) => <ServedBadge problem={row.problem} /> },
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
            aria-label={`Edit the saved answer to "${row.question}"`}
            onClick={(event) => {
              event.stopPropagation();
              setEditing(row);
            }}
            data-testid="saved-answer-edit"
          >
            <Pencil />
          </Button>
          <Button
            variant="ghost"
            size="icon"
            className="size-7"
            aria-label={`Delete the saved answer to "${row.question}"`}
            onClick={(event) => {
              event.stopPropagation();
              setPendingDelete(row);
            }}
            data-testid="saved-answer-delete"
          >
            <Trash2 />
          </Button>
        </div>
      ),
    },
  ];

  return (
    <div className="flex flex-col gap-3" data-testid="semantic-saved-answers-panel">
      <p className="text-[13px] text-muted-foreground">
        Queries people confirmed as the right answer to a question. The assistant reuses them as example queries for the
        tables they read, and runs one without asking when a new question matches it closely.
      </p>
      <div className="max-w-sm">
        <Input
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder="Search questions and queries"
          aria-label="Search saved answers"
          className="h-8"
          data-testid="saved-answers-search"
        />
      </div>
      <PagedTable<SemanticExampleAdmin>
        queryKey={[SEMANTIC_ROOT, "examples", term]}
        fetchPage={(page, pageSize) => semanticLayerApi.examples({ search: term === "" ? undefined : term, page, pageSize })}
        columns={columns}
        rowKey={(row) => row.id}
        onRowClick={(row) => setEditing(row)}
        emptyMessage={term === ""
          ? "No answers have been saved yet. One is saved when someone confirms an assistant answer."
          : "No saved answer matches this search."}
        data-testid="saved-answers-table"
      />
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
