import { useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ChevronRight, KeyRound, Loader2, Lock, OctagonAlert, ShieldCheck, ShieldOff, SquareDot, SquareMinus, SquarePlus, type LucideIcon,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { prettyPrintSql } from "@/lib/sql";
import { isApiError } from "../../api/client";
import { aiContextApi } from "../../api/endpoints";
import type {
  AiContextChange, AiContextColumn, AiContextDetail, AiContextExample, AiContextTable, AiContextVersion,
} from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { RelativeTime } from "../../components/RelativeTime";
import { AiContextStatusChip, CellText, DescribedText, FileRef } from "./parts";
import { aiContextKey, COMPACT_GRID, errorText, plural, refreshSemanticLayer, shortVersion } from "./shared";

/** How each kind of change is drawn: a glyph in its tone, the words in the text tokens (DESIGN.md 3.2). */
const changeGlyphs: Record<AiContextChange["kind"], { icon: LucideIcon; tone: string }> = {
  added: { icon: SquarePlus, tone: "text-success" },
  changed: { icon: SquareDot, tone: "text-info" },
  removed: { icon: SquareMinus, tone: "text-destructive" },
};

/** "3 added, 1 changed, 2 removed", leaving out the kinds that do not occur. */
function countChanges(changes: AiContextChange[]): string {
  const kinds: AiContextChange["kind"][] = ["added", "changed", "removed"];
  return kinds
    .map((kind) => [kind, changes.filter((change) => change.kind === kind).length] as const)
    .filter(([, count]) => count > 0)
    .map(([kind, count]) => `${count} ${kind}`)
    .join(", ");
}

/** A titled part of the detail: an 11px caption over its content, the way the panel's other captions read. */
function Section({ title, count, children, testId }: { title: string; count?: number; children: ReactNode; testId?: string }) {
  return (
    <section className="flex flex-col gap-1.5" data-testid={testId}>
      <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        {title}
        {count !== undefined && <span className="ml-1.5 font-mono tabular-nums">{count}</span>}
      </h3>
      {children}
    </section>
  );
}

/** One fact of the detail's header line: an 11px caption beside its value, so the facts share one line where they fit. */
function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <span className="inline-flex min-w-0 items-baseline gap-1.5 text-[12px]">
      <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{label}</span>
      <span className="min-w-0 break-words">{children}</span>
    </span>
  );
}

/** A disclosure row: a chevron, a line that says what is inside, and the content once opened. */
function Disclosure({ summary, children, testId }: { summary: ReactNode; children: ReactNode; testId?: string }) {
  const [open, setOpen] = useState(false);
  return (
    <Collapsible open={open} onOpenChange={setOpen} className="rounded-md border border-border" data-testid={testId}>
      <CollapsibleTrigger className="flex w-full min-w-0 items-center gap-2 px-2 py-1.5 text-left hover:bg-accent/50">
        <ChevronRight className={cn("size-4 shrink-0 text-muted-foreground transition-transform duration-120", open && "rotate-90")} />
        {summary}
      </CollapsibleTrigger>
      <CollapsibleContent className="flex flex-col gap-3 border-t border-border px-3 py-2.5">{children}</CollapsibleContent>
    </Collapsible>
  );
}

/** The SQL of an example in the code viewer, sized to the query (pretty-printed, as the viewer shows it). */
export function SqlBlock({ sql, testId }: { sql: string; testId?: string }) {
  const lines = prettyPrintSql(sql).split("\n").length;
  return <CodeView value={sql} language="sql" lsp={false} height={Math.min(320, Math.max(64, lines * 18 + 26))} data-testid={testId} />;
}

/** One example question: the question on the row, its SQL once opened. */
export function ExampleDisclosure({ question, sql, sourceRef, extra, testId = "ai-context-example" }: {
  question: string;
  sql: string;
  sourceRef: string | null | undefined;
  /** What sits at the end of the row (where the example came from, whether it is served). */
  extra?: ReactNode;
  testId?: string;
}) {
  return (
    <Disclosure
      testId={testId}
      summary={(
        <>
          <span className="min-w-0 flex-1 truncate text-[13px]">{question}</span>
          {extra}
        </>
      )}
    >
      <SqlBlock sql={sql} testId="ai-context-example-sql" />
      <p className="text-xs text-muted-foreground">
        {sourceRef == null
          ? "No datasource: the assistant works it out from the tables the query reads."
          : <>Runs on <span className="font-mono">{sourceRef}</span></>}
      </p>
    </Disclosure>
  );
}

/** The open columns of a context's table: name, the key marked, and what the file says each means. */
function OpenColumns({ table }: { table: AiContextTable }) {
  const key = new Set(table.key.map((column) => column.toLowerCase()));
  const columns: Column<AiContextColumn>[] = [
    {
      id: "name",
      header: "Column",
      render: (row) => (
        <span className="inline-flex max-w-full items-center gap-1.5">
          <CellText text={row.name} maxWidth={200} mono />
          {key.has(row.name.toLowerCase()) && <KeyRound className="size-3.5 shrink-0 text-warning" aria-label="Key column" />}
        </span>
      ),
    },
    {
      id: "description",
      header: "Description",
      fill: true,
      floor: 120,
      render: (row) => <DescribedText description={row.description} synonyms={row.synonyms} />,
    },
  ];

  return (
    <div className={COMPACT_GRID}>
      <DataTable<AiContextColumn>
        columns={columns}
        rows={table.columns}
        rowKey={(row) => row.name}
        emptyMessage="Every column of this table is denied."
        data-testid="ai-context-columns"
      />
    </div>
  );
}

/** A join as the file declares it: each column pair, in the file's order. */
function joinText(table: AiContextTable, join: AiContextTable["joins"][number]): string {
  return join.columns
    .map((column, i) => `${table.display}.${column} = ${join.toDisplay}.${join.toColumns[i] ?? "?"}`)
    .join(" and ");
}

/** One table of a context version: a summary row, and once opened what the file says about it. */
function TableDisclosure({ table }: { table: AiContextTable }) {
  // A version approved before files listed denied columns was stored without the list.
  const denied = table.denied ?? [];
  const counts = [
    plural(table.columns.length, "open column"),
    denied.length > 0 ? `${denied.length} denied` : null,
    table.measures.length > 0 ? plural(table.measures.length, "measure") : null,
    table.joins.length > 0 ? plural(table.joins.length, "join") : null,
  ].filter((part): part is string => part !== null);

  return (
    <Disclosure
      testId="ai-context-table-section"
      summary={(
        <>
          <span className="shrink-0 font-mono text-[12px] font-medium">{table.display}</span>
          {table.businessName != null && <span className="min-w-0 truncate text-[12px] text-muted-foreground">{table.businessName}</span>}
          <span className="ml-auto shrink-0 pl-2 text-[11px] text-muted-foreground">{counts.join(" · ")}</span>
        </>
      )}
    >
      {(table.description != null || table.synonyms.length > 0 || table.key.length > 0) && (
        <div className="grid grid-cols-[repeat(auto-fit,minmax(12rem,1fr))] gap-x-6 gap-y-2">
          {table.description != null && (
            <div className="col-span-full">
              <DetailPair label="Description"><span className="whitespace-pre-wrap">{table.description}</span></DetailPair>
            </div>
          )}
          {table.synonyms.length > 0 && <DetailPair label="Synonyms">{table.synonyms.join(", ")}</DetailPair>}
          {table.key.length > 0 && <DetailPair label="Key"><span className="font-mono text-[12px]">{table.key.join(", ")}</span></DetailPair>}
        </div>
      )}

      {denied.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 text-[12px]" data-testid="ai-context-denied">
          <span className="inline-flex items-center gap-1 text-muted-foreground">
            <Lock className="size-3.5 shrink-0" aria-hidden />
            Denied (the assistant cannot use these):
          </span>
          {denied.map((column) => (
            <span key={column} className="rounded-sm border border-border bg-muted/60 px-1.5 font-mono text-[11px]">{column}</span>
          ))}
        </div>
      )}

      <OpenColumns table={table} />

      {table.measures.length > 0 && (
        <div className="flex flex-col gap-1">
          <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Measures</span>
          {table.measures.map((measure) => (
            <div key={measure.name} className="flex flex-col text-[12px]" data-testid="ai-context-measure">
              <span className="break-words font-mono">
                <span className="font-medium">{measure.name}</span>
                <span className="text-muted-foreground">{" = "}</span>
                {measure.expression}
              </span>
              {measure.description != null && <span className="text-muted-foreground">{measure.description}</span>}
            </div>
          ))}
        </div>
      )}

      {table.joins.length > 0 && (
        <div className="flex flex-col gap-1">
          <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Joins</span>
          {table.joins.map((join) => (
            <div key={`${join.toObjectKey}|${join.columns.join(",")}|${join.toColumns.join(",")}`} className="flex flex-col text-[12px]" data-testid="ai-context-join">
              <span className="break-words font-mono">
                {joinText(table, join)}
                <span className="ml-1.5 font-sans text-[11px] text-muted-foreground">{join.joinType === "Left" ? "left join" : "inner join"}</span>
              </span>
              {join.description != null && <span className="text-muted-foreground">{join.description}</span>}
            </div>
          ))}
        </div>
      )}
    </Disclosure>
  );
}

/** Which version the content below is, and whether the assistant uses it: what an admin must know before approving. */
function versionCaption(detail: AiContextDetail): string {
  const { summary } = detail;
  const proposed = shortVersion(summary.currentVersion);
  const approved = shortVersion(summary.approvedVersion);
  if (detail.current == null) {
    return approved === null
      ? "Its file does not read, so it proposes no version and nothing of it is served."
      : `Its file does not read now. The approved version ${approved} is shown, and the assistant still uses it.`;
  }

  switch (summary.status) {
    case "approved":
      return `Version ${proposed}, approved: the assistant uses it.`;
    case "changed":
      return `Proposed version ${proposed}, waiting for approval. The assistant still uses the approved version ${approved}, `
        + "without anything the file has removed since; approving serves this one instead.";
    case "invalid":
      return approved === null
        ? `Proposed version ${proposed}. It cannot be approved, and nothing of this context is served.`
        : `Proposed version ${proposed}. It cannot be approved; the assistant still uses the approved version ${approved}.`;
    default:
      return `Proposed version ${proposed}. Nothing of it is served until it is approved.`;
  }
}

/** The content of one version: its instructions, tables and examples. */
function VersionContent({ version }: { version: AiContextVersion }) {
  return (
    <>
      {version.instructions != null && (
        <Section title="Instructions" testId="ai-context-instructions">
          <div className="whitespace-pre-wrap break-words rounded-md border border-border bg-muted/40 px-3 py-2 text-[12px]">
            {version.instructions}
          </div>
        </Section>
      )}
      <Section title="Tables" count={version.tables.length} testId="ai-context-tables">
        {version.tables.length === 0
          ? <p className="text-[12px] text-muted-foreground">This version lists no table.</p>
          : version.tables.map((table) => <TableDisclosure key={table.objectKey} table={table} />)}
      </Section>
      {version.examples.length > 0 && (
        <Section title="Examples" count={version.examples.length} testId="ai-context-examples">
          {version.examples.map((example: AiContextExample) => (
            <ExampleDisclosure key={example.question} question={example.question} sql={example.sql} sourceRef={example.sourceRef} />
          ))}
        </Section>
      )}
    </>
  );
}

/** A button an admin cannot press right now, with why on hover (a disabled button takes no pointer events). */
function Gated({ reason, children }: { reason: string | null; children: ReactNode }) {
  if (reason === null) {
    return <>{children}</>;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex">{children}</span>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">{reason}</TooltipContent>
    </Tooltip>
  );
}

/**
 * One AI context in the bottom panel: where it stands, which version the assistant uses, and the version its file
 * proposes in full, with what approving it would change and why it cannot be approved when it cannot. An admin approves
 * the version shown (the request carries its hash, so a file that changed meanwhile is refused rather than approved
 * unseen) or revokes the approval; nothing here edits the context, whose file is where it is written.
 */
export function AiContextDetailPanel({ name }: { name: string }) {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const admin = hasScope("admin");
  const [confirming, setConfirming] = useState<"approve" | "revoke" | null>(null);

  const query = useQuery({ queryKey: aiContextKey(name), queryFn: () => aiContextApi.get(name) });

  const approve = useMutation({
    mutationFn: (version: string) => aiContextApi.approve(name, version),
    onSuccess: (detail) => {
      toast.success(`Approved ${name} version ${shortVersion(detail.summary.approvedVersion) ?? ""}.`, {
        description: "The assistant uses it from its next question.",
      });
    },
    onError: (error) => toast.error(`${name} was not approved: ${errorText(error)}`),
    onSettled: () => {
      setConfirming(null);
      refreshSemanticLayer(queryClient);
    },
  });

  const revoke = useMutation({
    mutationFn: () => aiContextApi.revoke(name),
    onSuccess: () => {
      toast.success(`Revoked the approval of ${name}.`, {
        description: "The assistant no longer uses anything it served. Approving it again serves it again.",
      });
    },
    onError: (error) => toast.error(`${name} was not revoked: ${errorText(error)}`),
    onSettled: () => {
      setConfirming(null);
      refreshSemanticLayer(queryClient);
    },
  });

  if (query.isPending) {
    return (
      <div className="flex flex-col gap-2 p-4" data-testid="ai-context-detail">
        <Skeleton className="h-6 w-72 max-w-full" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="mt-2 h-32 w-full" />
      </div>
    );
  }

  if (query.isError) {
    return (
      <div className="p-4" data-testid="ai-context-detail">
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <p className="text-[13px] text-destructive">{errorText(query.error)}</p>}
      </div>
    );
  }

  const detail = query.data;
  const { summary } = detail;
  const shown = detail.current ?? detail.approved;
  const proposed = summary.currentVersion;
  const pendingApproval = summary.status === "awaiting-approval" || summary.status === "changed";
  const approveBlocked = proposed == null
    ? "Its file does not read, so there is no version to approve. Fix the problems it lists; the next sync proposes one."
    : detail.problems.length > 0
      ? "This version has problems. Fix them in the file; the next sync proposes a new version to approve."
      : !pendingApproval
        ? "This version is already approved."
        : null;
  const changeCount = countChanges(detail.changes);

  return (
    <div className="flex flex-col gap-4 px-4 py-3" data-testid="ai-context-detail">
      <div className="flex flex-col gap-1.5">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
          <h2 className="font-mono text-sm font-semibold">{summary.name}</h2>
          <AiContextStatusChip status={summary.status} />
          <div className="min-w-0 flex-[1_1_12rem]">
            <FileRef repoName={summary.repoName} path={summary.path} />
          </div>
          {admin && (
            <div className="flex shrink-0 items-center gap-2">
              {summary.approvedVersion != null && (
                <Button
                  variant="outline"
                  size="xs"
                  disabled={revoke.isPending || approve.isPending}
                  onClick={() => setConfirming("revoke")}
                  data-testid="ai-context-revoke"
                >
                  {revoke.isPending ? <Loader2 className="animate-spin" /> : <ShieldOff />}
                  Revoke
                </Button>
              )}
              <Gated reason={approveBlocked}>
                <Button
                  size="xs"
                  disabled={approveBlocked !== null || approve.isPending || revoke.isPending}
                  onClick={() => setConfirming("approve")}
                  data-testid="ai-context-approve"
                >
                  {approve.isPending ? <Loader2 className="animate-spin" /> : <ShieldCheck />}
                  Approve
                </Button>
              </Gated>
            </div>
          )}
        </div>
        {summary.description != null && <p className="text-[13px] text-muted-foreground">{summary.description}</p>}
      </div>

      <div className="flex flex-wrap gap-x-6 gap-y-1.5" data-testid="ai-context-facts">
        <Fact label="Proposed">
          <span className="font-mono" data-testid="ai-context-proposed-version">{shortVersion(proposed) ?? "none"}</span>
        </Fact>
        <Fact label="Approved">
          {summary.approvedVersion == null
            ? <span className="text-muted-foreground">never</span>
            : (
              <>
                <span className="font-mono" data-testid="ai-context-approved-version">{shortVersion(summary.approvedVersion)}</span>
                {summary.approvedUtc != null && (
                  <span className="text-muted-foreground">
                    {` by ${summary.approvedBy ?? "unknown"}, `}
                    <RelativeTime value={summary.approvedUtc} absolute={false} />
                  </span>
                )}
              </>
            )}
        </Fact>
        <Fact label="Tables served">
          <span className={cn("font-mono tabular-nums", summary.servedTableCount === 0 && "text-muted-foreground")}>
            {`${summary.servedTableCount} of ${summary.tableCount}`}
          </span>
        </Fact>
        <Fact label="Synced">
          <span className="text-muted-foreground"><RelativeTime value={summary.syncedUtc} absolute={false} /></span>
        </Fact>
      </div>

      <p className="text-[13px]" data-testid="ai-context-served-note">{versionCaption(detail)}</p>

      {detail.problems.length > 0 && (
        <Section title="Problems" count={detail.problems.length} testId="ai-context-problems">
          <p className="text-[12px] text-muted-foreground">Why this version cannot be approved. Fix them in the file; the next sync proposes a new version.</p>
          <ul className="flex flex-col gap-1">
            {detail.problems.map((problem) => (
              <li key={problem} className="flex items-start gap-1.5 text-[12px]">
                <OctagonAlert className="mt-px size-3.5 shrink-0 text-destructive" aria-hidden />
                <span className="break-words">{problem}</span>
              </li>
            ))}
          </ul>
        </Section>
      )}

      {summary.status !== "approved" && detail.changes.length > 0 && (
        <Section
          title={summary.approvedVersion == null ? "Served on approval" : "Changes on approval"}
          count={detail.changes.length}
          testId="ai-context-changes"
        >
          <ul className="flex flex-col gap-1">
            {detail.changes.map((change, i) => {
              const glyph = changeGlyphs[change.kind];
              const Icon = glyph.icon;
              return (
                <li key={`${change.kind}|${change.subject}|${i}`} className="flex min-w-0 items-start gap-1.5 text-[12px]" data-testid="ai-context-change" data-kind={change.kind}>
                  <Icon className={cn("mt-px size-3.5 shrink-0", glyph.tone)} aria-label={change.kind} />
                  <span className="min-w-0 break-words">
                    {change.subject}
                    {change.detail != null && <span className="text-muted-foreground">{`: ${change.detail}`}</span>}
                  </span>
                </li>
              );
            })}
          </ul>
        </Section>
      )}

      {shown != null && <VersionContent version={shown} />}

      <ConfirmDialog
        open={confirming === "approve"}
        title={`Approve ${summary.name}?`}
        message={`The assistant starts using version ${shortVersion(proposed) ?? ""}`
          + (changeCount === "" ? "." : `: ${plural(detail.changes.length, "change")} (${changeCount}).`)
          + " A later change to the file waits for approval again."}
        confirmLabel="Approve"
        busy={approve.isPending}
        onConfirm={() => proposed != null && approve.mutate(proposed)}
        onClose={() => setConfirming(null)}
      />
      <ConfirmDialog
        open={confirming === "revoke"}
        title={`Revoke the approval of ${summary.name}?`}
        message={`The assistant stops using everything this context serves at once: ${plural(summary.servedTableCount, "table")}, `
          + "their measures and joins, its examples and its instructions. The file stays; approving it again serves it again."}
        confirmLabel="Revoke"
        danger
        busy={revoke.isPending}
        onConfirm={() => revoke.mutate()}
        onClose={() => setConfirming(null)}
      />
    </div>
  );
}
