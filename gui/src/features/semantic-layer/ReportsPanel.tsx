import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Check, Eye, Pencil, Plus, Trash2, Upload, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { semanticLayerApi } from "../../api/endpoints";
import type { SemanticReportPage, SemanticReportQuestion, SemanticReportSpec } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { DataTable, type Column } from "../../components/DataTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ReportUploadDialog } from "./ReportUploadDialog";
import { describeReport, errorText, refreshSemanticLayer, SEMANTIC_ROOT } from "./shared";

function OriginBadge({ report }: { report: SemanticReportSpec }) {
  const [label, hint] = report.origin === "upload"
    ? ["uploaded", "Added by a person. It stays until someone deletes it."]
    : ["kept by sync", "A sync extracted this report from the .pbix the repository declares and kept a copy, so a "
      + "sync that cannot extract it (the control plane never can) still serves it. The next sync that can extract "
      + "the report refreshes it."];
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex"><Badge variant="outline" className="text-[11px]">{label}</Badge></span>
      </TooltipTrigger>
      <TooltipContent className="max-w-sm">{hint}</TooltipContent>
    </Tooltip>
  );
}

/** The longest question the catalog stores. */
const MAX_QUESTION_LENGTH = 400;

/** One question on a visual: shown with where it came from, and edited or deleted in place. */
function QuestionRow({ entry, onChanged }: { entry: SemanticReportQuestion; onChanged: () => void }) {
  const [draft, setDraft] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: (question: string) => semanticLayerApi.updateReportQuestion(entry.id, question),
    onSuccess: () => {
      setDraft(null);
      onChanged();
    },
    onError: (error) => toast.error(errorText(error)),
  });
  const remove = useMutation({
    mutationFn: () => semanticLayerApi.deleteReportQuestion(entry.id),
    onSuccess: onChanged,
    onError: (error) => toast.error(errorText(error)),
  });

  if (draft !== null) {
    const trimmed = draft.trim();
    const submit = () => {
      if (trimmed.length === 0 || trimmed === entry.question) {
        setDraft(null);
        return;
      }
      save.mutate(trimmed);
    };
    return (
      <li className="flex items-center gap-1" data-testid="semantic-report-question-editing">
        <Input
          value={draft}
          maxLength={MAX_QUESTION_LENGTH}
          autoFocus
          className="h-7 text-[12px]"
          aria-label="Question text"
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              submit();
            } else if (event.key === "Escape") {
              event.preventDefault();
              setDraft(null);
            }
          }}
        />
        <Button variant="ghost" size="icon" className="size-7" aria-label="Save question"
          disabled={trimmed.length === 0 || save.isPending} onClick={submit}>
          <Check />
        </Button>
        <Button variant="ghost" size="icon" className="size-7" aria-label="Cancel editing"
          disabled={save.isPending} onClick={() => setDraft(null)}>
          <X />
        </Button>
      </li>
    );
  }

  return (
    <li className="group flex items-center gap-2 text-[12px]" data-testid="semantic-report-question">
      <span className="mr-auto">{entry.question}</span>
      {entry.origin === "manual"
        ? (
          <Tooltip>
            <TooltipTrigger asChild>
              <span className="inline-flex"><Badge variant="secondary" className="text-[10px]">curated</Badge></span>
            </TooltipTrigger>
            <TooltipContent>
              {entry.updatedBy ?? "A person"} wrote this
              {entry.updatedUtc !== null && <> <RelativeTime value={entry.updatedUtc} /></>}. Generation never replaces it.
            </TooltipContent>
          </Tooltip>
        )
        : <Badge variant="outline" className="text-[10px] text-muted-foreground">generated</Badge>}
      <Button variant="ghost" size="icon" className="size-6" aria-label={`Edit "${entry.question}"`}
        onClick={() => setDraft(entry.question)} data-testid="semantic-report-question-edit">
        <Pencil />
      </Button>
      <Button variant="ghost" size="icon" className="size-6" aria-label={`Delete "${entry.question}"`}
        disabled={remove.isPending} onClick={() => remove.mutate()} data-testid="semantic-report-question-delete">
        <Trash2 />
      </Button>
    </li>
  );
}

/** The input that adds a person's question to a visual. */
function AddQuestion({ repoId, visualKey, onChanged }: { repoId: string; visualKey: string; onChanged: () => void }) {
  const [text, setText] = useState("");
  const add = useMutation({
    mutationFn: (question: string) => semanticLayerApi.addReportQuestion({ repoId, visualKey, question }),
    onSuccess: () => {
      setText("");
      onChanged();
    },
    onError: (error) => toast.error(errorText(error)),
  });
  const trimmed = text.trim();
  const submit = () => {
    if (trimmed.length > 0) {
      add.mutate(trimmed);
    }
  };

  return (
    <div className="mt-1 flex items-center gap-1">
      <Input
        value={text}
        maxLength={MAX_QUESTION_LENGTH}
        placeholder="Add a question this visual answers"
        className="h-7 text-[12px]"
        aria-label="New question"
        onChange={(event) => setText(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            submit();
          }
        }}
        data-testid="semantic-report-question-new"
      />
      <Button variant="ghost" size="sm" className="h-7" disabled={trimmed.length === 0 || add.isPending}
        onClick={submit} data-testid="semantic-report-question-add">
        <Plus />
        Add
      </Button>
    </div>
  );
}

/**
 * The business questions of each visual of a report, page by page, generated at sync or written by a person. They are
 * what question search matches a typed question against, so this is where a curator sees and shapes what the
 * assistant will recognize. A person's questions (added, or generated and then edited) are never replaced by
 * generation; deleting every question on a visual lets the next sync generate fresh ones while generation is on.
 */
function ReportQuestions({ repoId, pages, generationEnabled, onChanged }: {
  repoId: string;
  pages: SemanticReportPage[];
  generationEnabled: boolean;
  onChanged: () => void;
}) {
  const visuals = pages.flatMap((page) => page.visuals);
  const answered = visuals.filter((visual) => visual.questionEntries.length > 0).length;

  if (pages.length === 0) {
    return (
      <p className="text-[13px] text-muted-foreground" data-testid="semantic-report-questions-empty">
        No sync has served this report yet, so it has no visuals to ask questions about. Sync the repo to read it.
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-3" data-testid="semantic-report-questions">
      <p className="text-[12px] text-muted-foreground">
        {`${answered} of ${visuals.length} visuals have questions. `}
        {generationEnabled
          ? "A sync generates them for new, changed, or unanswered visuals and keeps the rest."
          : "Question generation is off (the switch above the reports list), so a sync adds no new ones."}
        {" Questions you add or edit are kept as written."}
      </p>
      <div className="flex max-h-[480px] flex-col gap-4 overflow-y-auto pr-1">
        {pages.map((page) => (
          <section key={`${page.reportFile}#${page.ordinal}`} className="flex flex-col gap-2">
            <h3 className="text-[13px] font-medium">{page.displayName}</h3>
            {page.visuals.length === 0
              ? <p className="text-[12px] text-muted-foreground">No question-asking visuals on this page.</p>
              : page.visuals.map((visual) => (
                <div key={visual.visualKey} className="rounded-md border p-2" data-testid="semantic-report-visual">
                  <div className="flex items-center gap-2">
                    <span className="text-[13px]">{visual.title ?? "Untitled visual"}</span>
                    <Badge variant="outline" className="text-[11px]">{visual.visualType}</Badge>
                  </div>
                  {visual.questionEntries.length === 0
                    ? <p className="mt-1 text-[12px] text-muted-foreground">No questions yet.</p>
                    : (
                      <ul className="mt-1 flex flex-col gap-0.5">
                        {visual.questionEntries.map((entry) => (
                          <QuestionRow key={entry.id} entry={entry} onChanged={onChanged} />
                        ))}
                      </ul>
                    )}
                  <AddQuestion repoId={repoId} visualKey={visual.visualKey} onChanged={onChanged} />
                </div>
              ))}
          </section>
        ))}
      </div>
    </div>
  );
}

/**
 * The runtime switch for sync-time question generation. It overrides the deployment's configured default until reset,
 * and cannot turn generation on where the deployment has no Anthropic key. Turning it on queues the syncs that fill in
 * the questions, since only a sync generates them.
 */
function QuestionGenerationSwitch() {
  const queryClient = useQueryClient();
  const state = useQuery({
    queryKey: [SEMANTIC_ROOT, "reports", "question-generation"],
    queryFn: () => semanticLayerApi.questionGeneration(),
  });

  const set = useMutation({
    mutationFn: (enabled: boolean | null) => semanticLayerApi.setQuestionGeneration(enabled),
    onSuccess: (result) => {
      toast.success(result.enabled ? "Question generation is on." : "Question generation is off.", {
        description: result.syncsQueued > 0
          ? `Queued ${result.syncsQueued} repo sync${result.syncsQueued === 1 ? "" : "s"} to generate the missing questions.`
          : result.enabled
            ? "The next sync of each repo generates questions for visuals that have none."
            : "Syncs keep the questions already generated and add no new ones.",
      });
      refreshSemanticLayer(queryClient);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  if (state.isError) {
    return <p className="text-[13px] text-destructive">{errorText(state.error)}</p>;
  }

  if (state.data === undefined) {
    return <Skeleton className="h-14 w-full" />;
  }

  const current = state.data;
  const source = current.override === null
    ? `Following the deployment default (${current.deploymentDefault ? "on" : "off"}).`
    : `Set ${current.override ? "on" : "off"} by ${current.updatedBy ?? "an admin"}, overriding the deployment `
      + `default (${current.deploymentDefault ? "on" : "off"}).`;

  return (
    <div className="flex items-center gap-3 rounded-md border p-3" data-testid="semantic-question-generation">
      <Switch
        id="semantic-question-generation-switch"
        checked={current.enabled}
        disabled={!current.available || set.isPending}
        onCheckedChange={(checked) => set.mutate(checked)}
        aria-label="Generate business questions at sync"
        data-testid="semantic-question-generation-switch"
      />
      <div className="mr-auto flex flex-col">
        <label htmlFor="semantic-question-generation-switch" className="text-[13px] font-medium">
          Generate business questions at sync
        </label>
        <span className="text-[12px] text-muted-foreground">
          {current.available
            ? (
              <>
                The assistant matches typed questions against the ones generated for each report visual. {source}
                {current.updatedUtc !== null && (
                  <> Changed <RelativeTime value={current.updatedUtc} />.</>
                )}
              </>
            )
            : "Unavailable: this deployment has no Anthropic key (ControlPlane:Assistant:Anthropic:ApiKey)."}
        </span>
      </div>
      {current.available && current.override !== null && (
        <Button
          variant="ghost"
          size="sm"
          disabled={set.isPending}
          onClick={() => set.mutate(null)}
          data-testid="semantic-question-generation-reset"
        >
          Use deployment default
        </Button>
      )}
    </div>
  );
}

function ReportDetailDialog({ id, onClose }: { id: number; onClose: () => void }) {
  const queryClient = useQueryClient();
  const detail = useQuery({
    queryKey: [SEMANTIC_ROOT, "reports", id],
    queryFn: () => semanticLayerApi.report(id),
  });
  const capabilities = useQuery({
    queryKey: [SEMANTIC_ROOT, "reports", "capabilities"],
    queryFn: () => semanticLayerApi.reportCapabilities(),
  });

  return (
    <Dialog open onOpenChange={(next) => !next && onClose()}>
      <DialogContent className="sm:max-w-4xl" data-testid="semantic-report-detail">
        <DialogHeader>
          <DialogTitle>{detail.data?.report.reportFile ?? "Report"}</DialogTitle>
          <DialogDescription>
            {detail.data === undefined
              ? "Loading the stored specification."
              : `${detail.data.report.subscriberName} in ${detail.data.report.repoName}: ${describeReport(detail.data.summary)}. `
                + `${detail.data.summary.resolvedTables} of its model tables load from a warehouse table.`}
          </DialogDescription>
        </DialogHeader>
        {detail.isError && <p className="text-[13px] text-destructive">{errorText(detail.error)}</p>}
        {detail.data === undefined ? <Skeleton className="h-96 w-full" /> : (
          <div className="flex flex-col gap-3">
            {detail.data.summary.warnings.length > 0 && (
              <ul className="list-disc rounded-md border border-warning/60 p-2 pl-6 text-[12px] text-warning">
                {detail.data.summary.warnings.map((warning) => <li key={warning}>{warning}</li>)}
              </ul>
            )}
            <Tabs defaultValue="questions">
              <TabsList>
                <TabsTrigger value="questions" data-testid="semantic-report-questions-tab">Questions</TabsTrigger>
                <TabsTrigger value="spec">Specification</TabsTrigger>
              </TabsList>
              <TabsContent value="questions">
                <ReportQuestions
                  repoId={detail.data.report.repoId}
                  pages={detail.data.pages}
                  generationEnabled={capabilities.data?.questionGenerationEnabled === true}
                  onChanged={() => void queryClient.invalidateQueries({ queryKey: [SEMANTIC_ROOT, "reports", id] })}
                />
              </TabsContent>
              <TabsContent value="spec">
                <CodeView value={detail.data.spec} language="yaml" lsp={false} height={480} />
              </TabsContent>
            </Tabs>
          </div>
        )}
        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/**
 * The Power BI reports the semantic layer holds. A report's pages, visuals and model reach the catalog through its
 * specification; this is where a person adds one the repository does not carry, by uploading the .pbix (read by the
 * isolated extractor, never by the control plane) or a specification made with `sqlflow powerbi extract`. The copies a
 * sync kept of reports the repository declares are listed too, so it is visible what the assistant is served.
 */
export function ReportsPanel() {
  const queryClient = useQueryClient();
  const [uploading, setUploading] = useState(false);
  const [viewing, setViewing] = useState<number | null>(null);
  const [pendingDelete, setPendingDelete] = useState<SemanticReportSpec | null>(null);

  const reports = useQuery({ queryKey: [SEMANTIC_ROOT, "reports"], queryFn: () => semanticLayerApi.reports() });

  const remove = useMutation({
    mutationFn: (id: number) => semanticLayerApi.deleteReport(id),
    onSuccess: (result, id) => {
      const removed = reports.data?.find((report) => report.id === id);
      toast.success(`Report '${removed?.reportFile ?? id}' removed.`, {
        description: result.syncQueued
          ? "A sync was queued to drop what it contributed."
          : "Run a sync of the repo to drop what it contributed.",
      });
      setPendingDelete(null);
      refreshSemanticLayer(queryClient);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const columns: Column<SemanticReportSpec>[] = [
    {
      id: "report",
      header: "Report",
      render: (row) => <span className="break-all font-mono text-[12px] font-medium">{row.reportFile}</span>,
    },
    {
      id: "subscriber",
      header: "Subscriber",
      render: (row) => (
        <div className="flex flex-col">
          <span className="text-[13px]">{row.subscriberName}</span>
          <span className="text-[11px] text-muted-foreground">{row.repoName}</span>
        </div>
      ),
    },
    { id: "origin", header: "Origin", width: 120, render: (row) => <OriginBadge report={row} /> },
    {
      id: "contents",
      header: "Contents",
      render: (row) => <span className="text-[12px] text-muted-foreground">{describeReport(row)}</span>,
    },
    {
      id: "updated",
      header: "Stored",
      width: 170,
      render: (row) => (
        <span className="text-[12px]">
          {row.updatedBy ?? "sync"}{" "}
          <span className="text-muted-foreground"><RelativeTime value={row.updatedUtc} /></span>
        </span>
      ),
    },
    {
      id: "status",
      header: "Status",
      width: 110,
      render: (row) => row.subscriberDeclared
        ? <Badge variant="secondary" className="text-[11px]">served</Badge>
        : (
          <Tooltip>
            <TooltipTrigger asChild>
              <span className="inline-flex">
                <Badge variant="outline" className="border-warning/60 text-[11px] text-warning">not served</Badge>
              </span>
            </TooltipTrigger>
            <TooltipContent className="max-w-sm">
              The repository no longer declares this subscriber, so the report is not read by any sync. Declare it again
              in subscribers.yaml, or delete the report.
            </TooltipContent>
          </Tooltip>
        ),
    },
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
            aria-label={`View the questions and specification of ${row.reportFile}`}
            onClick={(event) => {
              event.stopPropagation();
              setViewing(row.id);
            }}
            data-testid="semantic-report-view"
          >
            <Eye />
          </Button>
          <Button
            variant="ghost"
            size="icon"
            className="size-7"
            aria-label={`Remove ${row.reportFile}`}
            onClick={(event) => {
              event.stopPropagation();
              setPendingDelete(row);
            }}
            data-testid="semantic-report-delete"
          >
            <Trash2 />
          </Button>
        </div>
      ),
    },
  ];

  return (
    <div className="flex flex-col gap-3" data-testid="semantic-reports-panel">
      <div className="flex items-start gap-3">
        <p className="mr-auto max-w-3xl text-[13px] text-muted-foreground">
          Power BI reports behind the repositories' Power BI subscribers. Their measures, relationships and visuals are
          what the assistant learns the business vocabulary from. Add a report here when it is not committed to the
          repository; one that is committed as a <span className="font-mono">.pbix.yaml</span> specification is read
          from there and needs no upload.
        </p>
        <Button size="sm" onClick={() => setUploading(true)} data-testid="semantic-report-upload">
          <Upload />
          Upload report
        </Button>
      </div>
      <QuestionGenerationSwitch />
      {reports.isError && <p className="text-[13px] text-destructive">{errorText(reports.error)}</p>}
      <DataTable<SemanticReportSpec>
        columns={columns}
        rows={reports.data}
        rowKey={(row) => row.id}
        onRowClick={(row) => setViewing(row.id)}
        minWidth={900}
        emptyMessage="No report is held here yet. Upload one, or commit a specification next to subscribers.yaml."
        data-testid="semantic-reports-table"
      />
      {uploading && <ReportUploadDialog onClose={() => setUploading(false)} />}
      {viewing !== null && <ReportDetailDialog id={viewing} onClose={() => setViewing(null)} />}
      <ConfirmDialog
        open={pendingDelete !== null}
        title={`Remove ${pendingDelete?.reportFile ?? "this report"}?`}
        message={pendingDelete?.origin === "extracted"
          ? "The next sync that cannot extract this report will stop serving it. A sync that can extract it stores it again."
          : "The report's pages, visuals and model stop being served once the repo syncs."}
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => pendingDelete !== null && remove.mutate(pendingDelete.id)}
        onClose={() => setPendingDelete(null)}
      />
    </div>
  );
}
