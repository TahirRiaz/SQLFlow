import { useCallback, useRef } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { CloudUpload, GitBranch, MessageSquareText, Trash2 } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "../../api/client";
import { semanticLayerApi } from "../../api/endpoints";
import type { SemanticReportPage, SemanticReportQuestion, SemanticReportSpec, SemanticReportSubscriber } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { IconAction } from "../../components/IconAction";
import { RichTooltip } from "../../components/RichTooltip";
import { TruncatedText } from "../../components/TruncatedText";
import { tableContainer, useScrollFit } from "../../hooks/useScrollFit";
import { useOwnedPanel } from "../../layout/workbench/useOwnedPanel";
import { CellText, CopyQuestion, ServedMark, Stamp, ToneChip } from "./parts";
import {
  COMPACT_GRID, describeReport, errorText, FIT_BELOW, FIT_MIN, FITTED_GRID, plural, SEMANTIC_ROOT,
} from "./shared";

/** The bottom panel content ids this surface owns. */
const PANEL = "report:";

const reportKey = (id: number) => [SEMANTIC_ROOT, "reports", id] as const;

/** Where a stored report came from, with what that means for how long it stays. */
function OriginChip({ report }: { report: SemanticReportSpec }) {
  const [label, hint, icon] = report.origin === "upload"
    ? ["Published", "Stored by a person, with sqlflow powerbi publish or an earlier upload. It stays until it is removed.", CloudUpload] as const
    : ["Kept by sync", "A sync extracted this report from the .pbix the repository declares and kept a copy, so a sync "
      + "that cannot extract it still serves it. The next sync that can extract the report refreshes it.", GitBranch] as const;
  return (
    <RichTooltip body={hint} title={label}>
      <span className="inline-flex"><ToneChip icon={icon} tone="text-muted-foreground">{label}</ToneChip></span>
    </RichTooltip>
  );
}

/** One question on a visual: where it came from, and for an admin a remove. */
function QuestionRow({ entry, reportId, admin }: { entry: SemanticReportQuestion; reportId: number; admin: boolean }) {
  const queryClient = useQueryClient();
  const remove = useMutation({
    mutationFn: () => semanticLayerApi.deleteReportQuestion(entry.id),
    onSuccess: () => toast.success("Question removed.", {
      description: "A visual left with no question gets fresh ones at the next sync while its subscriber asks for them.",
    }),
    onError: (error) => toast.error(`The question was not removed: ${errorText(error)}`),
    onSettled: () => void queryClient.invalidateQueries({ queryKey: reportKey(reportId) }),
  });

  return (
    <li className="flex min-w-0 items-center gap-2 text-[12px]" data-testid="semantic-report-question">
      <span className="min-w-0 flex-1 break-words">{entry.question}</span>
      <span className="shrink-0 text-[11px] text-muted-foreground" data-origin={entry.origin}>
        {entry.origin === "manual" ? "written by a person" : "generated"}
      </span>
      <CopyQuestion question={entry.question} />
      {admin && (
        <IconAction
          label={`Remove "${entry.question}"`}
          icon={<Trash2 />}
          disabled={remove.isPending}
          onClick={() => remove.mutate()}
          data-testid="semantic-report-question-delete"
        />
      )}
    </li>
  );
}

/** The business questions of each visual of a report, page by page: what question search matches a typed question to. */
function ReportQuestions({ reportId, pages, admin }: { reportId: number; pages: SemanticReportPage[]; admin: boolean }) {
  if (pages.length === 0) {
    return (
      <p className="text-[13px] text-muted-foreground" data-testid="semantic-report-questions-empty">
        No sync has served this report yet, so it has no visuals to ask questions about.
      </p>
    );
  }

  const visuals = pages.flatMap((page) => page.visuals);
  const answered = visuals.filter((visual) => visual.questionEntries.length > 0).length;
  return (
    <div className="flex flex-col gap-3" data-testid="semantic-report-questions">
      <p className="text-[12px] text-muted-foreground">
        {`${answered} of ${plural(visuals.length, "visual")} have questions. A sync generates them for new, changed or `
          + "unanswered visuals when the subscriber's library sets generateQuestions: true."}
      </p>
      {pages.map((page) => (
        <section key={`${page.reportFile}#${page.ordinal}`} className="flex flex-col gap-1.5">
          <h3 className="text-[13px] font-medium">{page.displayName}</h3>
          {page.visuals.length === 0
            ? <p className="text-[12px] text-muted-foreground">No question-asking visuals on this page.</p>
            : page.visuals.map((visual) => (
              <div key={visual.visualKey} className="rounded-md border border-border p-2" data-testid="semantic-report-visual">
                <div className="flex items-center gap-2">
                  <span className="min-w-0 truncate text-[13px]">{visual.title ?? "Untitled visual"}</span>
                  <span className="shrink-0 font-mono text-[11px] text-muted-foreground">{visual.visualType}</span>
                </div>
                {visual.questionEntries.length === 0
                  ? <p className="mt-1 text-[12px] text-muted-foreground">No questions yet.</p>
                  : (
                    <ul className="mt-1 flex flex-col gap-0.5">
                      {visual.questionEntries.map((entry) => (
                        <QuestionRow key={entry.id} entry={entry} reportId={reportId} admin={admin} />
                      ))}
                    </ul>
                  )}
              </div>
            ))}
        </section>
      ))}
    </div>
  );
}

/** One stored report in the bottom panel: what it holds, its visuals' questions, and its specification. */
function ReportDetail({ id }: { id: number }) {
  const { hasScope } = useAuth();
  const detail = useQuery({ queryKey: reportKey(id), queryFn: () => semanticLayerApi.report(id) });

  if (detail.isPending) {
    return <div className="p-4"><Skeleton className="h-48 w-full" /></div>;
  }
  if (detail.isError) {
    return (
      <div className="p-4">
        {isApiError(detail.error) ? <CorrelationError error={detail.error} /> : <p className="text-[13px] text-destructive">{errorText(detail.error)}</p>}
      </div>
    );
  }

  const { report, summary } = detail.data;
  return (
    <div className="flex flex-col gap-3 px-4 py-3" data-testid="semantic-report-detail">
      <div className="flex flex-col gap-0.5">
        <h2 className="break-all font-mono text-sm font-semibold">{report.reportFile}</h2>
        <p className="text-[12px] text-muted-foreground">
          {`${report.subscriberName} in ${report.repoName}: ${describeReport(summary)}. `
            + `${summary.resolvedTables} of its model tables load from a warehouse table.`}
        </p>
      </div>
      {summary.warnings.length > 0 && (
        <ul className="list-disc pl-5 text-[12px] text-muted-foreground">
          {summary.warnings.map((warning) => <li key={warning}>{warning}</li>)}
        </ul>
      )}
      <Tabs defaultValue="questions" className="gap-3">
        <TabsList variant="line">
          <TabsTrigger value="questions" data-testid="semantic-report-questions-tab">Questions</TabsTrigger>
          <TabsTrigger value="spec">Specification</TabsTrigger>
        </TabsList>
        <TabsContent value="questions">
          <ReportQuestions reportId={id} pages={detail.data.pages} admin={hasScope("admin")} />
        </TabsContent>
        <TabsContent value="spec">
          <CodeView value={detail.data.spec} language="yaml" lsp={false} height={360} />
        </TabsContent>
      </Tabs>
    </div>
  );
}

const subscriberColumns: Column<SemanticReportSubscriber>[] = [
  {
    id: "name",
    header: "Subscriber",
    fill: true,
    floor: 140,
    render: (row) => <TruncatedText text={row.name} maxWidth={2000} className="font-medium" />,
  },
  { id: "repo", header: "Repository", render: (row) => <CellText text={row.repoName} maxWidth={180} mono /> },
  { id: "owner", header: "Owner", render: (row) => <CellText text={row.owner} maxWidth={140} /> },
  {
    id: "questions",
    header: "Questions",
    render: (row) => row.generateQuestions
      ? <ToneChip icon={MessageSquareText} tone="text-success" testId="subscriber-generates-questions">Generates questions</ToneChip>
      : <span className="text-[12px] text-muted-foreground" data-testid="subscriber-generates-questions">Off</span>,
  },
];

const reportColumns: Column<SemanticReportSpec>[] = [
  {
    id: "report",
    header: "Report",
    fill: true,
    floor: 140,
    render: (row) => <TruncatedText text={row.reportFile} maxWidth={2000} mono className="font-medium" />,
  },
  {
    id: "subscriber",
    header: "Subscriber",
    render: (row) => <CellText text={`${row.subscriberName} (${row.repoName})`} maxWidth={150} />,
  },
  { id: "origin", header: "Origin", render: (row) => <OriginChip report={row} /> },
  { id: "stored", header: "Stored", render: (row) => <Stamp who={row.updatedBy ?? "sync"} when={row.updatedUtc} maxWidth={170} /> },
  {
    id: "served",
    header: "Served",
    align: "center",
    render: (row) => (
      <ServedMark
        compact
        problem={row.subscriberDeclared
          ? null
          : "The repository no longer declares this subscriber, so no sync reads the report. Declare it again in the subscriber library, or remove the report with the CLI."}
      />
    ),
  },
];

/**
 * The Power BI side of what the assistant knows, read-only: the Power BI subscribers the repositories declare (and
 * whether each library asks a sync to generate business questions), and the reports stored for them. A report is added
 * or changed by committing its specification next to the subscriber library, or with `sqlflow powerbi publish`; a row
 * opens the report in the bottom panel with its visuals' questions, which an admin may remove one by one.
 */
export function ReportsPanel() {
  const { ownedId, show } = useOwnedPanel(PANEL);
  const grid = useRef<HTMLDivElement>(null);
  useScrollFit(grid, FIT_BELOW, FIT_MIN, tableContainer);

  const subscribers = useQuery({
    queryKey: [SEMANTIC_ROOT, "reports", "subscribers"],
    queryFn: () => semanticLayerApi.reportSubscribers(),
  });
  const capabilities = useQuery({
    queryKey: [SEMANTIC_ROOT, "reports", "capabilities"],
    queryFn: () => semanticLayerApi.reportCapabilities(),
  });
  const reports = useQuery({ queryKey: [SEMANTIC_ROOT, "reports"], queryFn: () => semanticLayerApi.reports() });

  const open = useCallback(
    (row: SemanticReportSpec) => show(String(row.id), `Power BI report · ${row.reportFile}`, <ReportDetail id={row.id} />),
    [show],
  );

  return (
    <div className="flex flex-col gap-4" data-testid="semantic-reports-panel">
      <p className="max-w-4xl text-[12px] text-muted-foreground">
        The reports behind the repositories&apos; Power BI subscribers: their measures, relationships and visuals are what
        the assistant learns business terms from. To add or change one, commit its specification next to the subscriber
        library, or store it with <span className="font-mono">sqlflow powerbi publish</span>.
      </p>

      <section className="flex flex-col gap-1.5">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Reports</h3>
        {reports.isError ? (
          isApiError(reports.error) ? <CorrelationError error={reports.error} /> : <p className="text-[13px] text-destructive">{errorText(reports.error)}</p>
        ) : (
          <div ref={grid} className={FITTED_GRID}>
            <DataTable<SemanticReportSpec>
              columns={reportColumns}
              rows={reports.data}
              rowKey={(row) => row.id}
              onRowClick={open}
              rowSx={(row) => (String(row.id) === ownedId ? { backgroundColor: "var(--accent)" } : undefined)}
              emptyMessage="No report is stored. Commit a specification next to the subscriber library, or publish one with sqlflow powerbi publish."
              data-testid="semantic-reports-table"
            />
          </div>
        )}
      </section>

      <section className="flex flex-col gap-1.5">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Subscribers</h3>
        {capabilities.data !== undefined && !capabilities.data.questionGenerationAvailable && (
          <p className="text-[12px] text-muted-foreground" data-testid="semantic-question-generation-unavailable">
            This deployment has no Anthropic key, so no questions are generated, whatever a library sets.
          </p>
        )}
        {subscribers.isError ? (
          isApiError(subscribers.error) ? <CorrelationError error={subscribers.error} /> : <p className="text-[13px] text-destructive">{errorText(subscribers.error)}</p>
        ) : (
          <div className={COMPACT_GRID}>
            <DataTable<SemanticReportSubscriber>
              columns={subscriberColumns}
              rows={subscribers.data}
              rowKey={(row) => `${row.repoId}::${row.subscriberKey}`}
              emptyMessage="No repository declares a Power BI subscriber. Add one to a subscriber library (type: PowerBI) and sync the repository."
              skeletonRows={2}
              data-testid="semantic-report-subscribers"
            />
          </div>
        )}
      </section>
    </div>
  );
}
