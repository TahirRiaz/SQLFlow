import { useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ArrowRight, ChevronRight, CircleCheck, ClipboardCheck, Clock3, Columns3, EyeOff, FileCode2, GitCompareArrows, Info, KeyRound,
  Link2, Loader2, Lock, MessageSquareQuote, OctagonAlert, ScrollText, ShieldCheck, ShieldOff, Sigma, Sparkles, SquareDot,
  SquareMinus, SquarePlus, Table2, type LucideIcon,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { prettyPrintSql } from "@/lib/sql";
import { isApiError } from "../../api/client";
import { aiContextApi } from "../../api/endpoints";
import type {
  AiContextChange, AiContextColumn, AiContextDetail, AiContextExample, AiContextSummary, AiContextTable, AiContextVersion,
} from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { CodeView } from "../../components/CodeView";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { DiffView } from "../../components/DiffView";
import { RelativeTime } from "../../components/RelativeTime";
import { RichTooltip } from "../../components/RichTooltip";
import { AiContextStatusChip, CellText, CopyQuestion, DescribedText, FileRef } from "./parts";
import { aiContextKey, aiContextYamlKey, COMPACT_GRID, errorText, plural, refreshSemanticLayer, shortVersion } from "./shared";

/** How each kind of change is drawn: a glyph in its tone, the words in the text tokens (DESIGN.md 3.2). */
const changeGlyphs: Record<AiContextChange["kind"], { icon: LucideIcon; tone: string; label: string }> = {
  added: { icon: SquarePlus, tone: "text-success", label: "Added" },
  changed: { icon: SquareDot, tone: "text-info", label: "Changed" },
  removed: { icon: SquareMinus, tone: "text-destructive", label: "Removed" },
};

/** The order a reviewer reads changes in: what approving adds (and so opens to the assistant) first. */
const changeOrder: AiContextChange["kind"][] = ["added", "changed", "removed"];

/** "3 added, 1 changed, 2 removed", leaving out the kinds that do not occur. */
function countChanges(changes: AiContextChange[]): string {
  return changeOrder
    .map((kind) => [kind, changes.filter((change) => change.kind === kind).length] as const)
    .filter(([, count]) => count > 0)
    .map(([kind, count]) => `${count} ${kind}`)
    .join(", ");
}

/**
 * A bordered card of the detail: a title row (glyph, title, count, and whatever sits at its end) over its content. The
 * cards are what give the review its structure, each answering one question an admin asks before approving.
 */
function Card({ title, icon: Icon, tone, count, aside, children, testId }: {
  title: string;
  icon: LucideIcon;
  /** The glyph's tone; muted unless the card reports a state. */
  tone?: string;
  count?: number;
  aside?: ReactNode;
  children: ReactNode;
  testId?: string;
}) {
  return (
    <section className="min-w-0 overflow-hidden rounded-lg border border-border bg-card" data-testid={testId}>
      <header className="flex min-h-9 flex-wrap items-center gap-x-2 gap-y-1 border-b border-border px-3 py-1.5">
        <Icon className={cn("size-4 shrink-0", tone ?? "text-muted-foreground")} aria-hidden />
        <h3 className="text-[13px] font-medium">{title}</h3>
        {count !== undefined && (
          <span className="rounded-sm bg-muted px-1.5 font-mono text-[11px] tabular-nums text-muted-foreground">{count}</span>
        )}
        {aside !== undefined && <div className="ml-auto flex flex-wrap items-center gap-1.5">{aside}</div>}
      </header>
      {children}
    </section>
  );
}

/**
 * A disclosure row: a chevron, a line that says what is inside, and the content once opened. `actions` sit at the end of
 * the row beside the toggle, not in it, since a button cannot hold another.
 */
function Disclosure({ summary, actions, children, testId, framed = true }: {
  summary: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  testId?: string;
  /** A row of a list that draws its own separators goes without a frame of its own. */
  framed?: boolean;
}) {
  const [open, setOpen] = useState(false);
  return (
    <Collapsible open={open} onOpenChange={setOpen} className={cn(framed && "rounded-md border border-border")} data-testid={testId}>
      <div className="flex min-w-0 items-center hover:bg-accent/50">
        <CollapsibleTrigger className="flex min-w-0 flex-1 items-center gap-2 px-3 py-2 text-left">
          <ChevronRight className={cn("size-4 shrink-0 text-muted-foreground transition-transform duration-120", open && "rotate-90")} />
          {summary}
        </CollapsibleTrigger>
        {actions !== undefined && <div className="flex shrink-0 items-center gap-1 pr-2">{actions}</div>}
      </div>
      <CollapsibleContent className="flex flex-col gap-3 border-t border-border bg-background/60 px-3 py-3">{children}</CollapsibleContent>
    </Collapsible>
  );
}

/** The SQL of an example in the code viewer, sized to the query (pretty-printed, as the viewer shows it). */
export function SqlBlock({ sql, testId }: { sql: string; testId?: string }) {
  const lines = prettyPrintSql(sql).split("\n").length;
  return <CodeView value={sql} language="sql" lsp={false} height={Math.min(320, Math.max(64, lines * 18 + 26))} data-testid={testId} />;
}

/** One example question: the question on the row, its SQL once opened. */
export function ExampleDisclosure({ question, sql, sourceRef, extra, testId = "ai-context-example", framed = true }: {
  question: string;
  sql: string;
  sourceRef: string | null | undefined;
  /** What sits at the end of the row (where the example came from, whether it is served). */
  extra?: ReactNode;
  testId?: string;
  framed?: boolean;
}) {
  return (
    <Disclosure
      testId={testId}
      framed={framed}
      summary={(
        <>
          <span className="min-w-0 flex-1 truncate text-[13px]" title={question}>{question}</span>
          {extra}
        </>
      )}
      actions={<CopyQuestion question={question} />}
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

/**
 * A count that says what it counts (DESIGN.md 7.8): the thing's glyph and the number, the words on hover and to assistive
 * tech. A zero is left out, so a row shows only what its table has.
 */
function GlyphCount({ icon: Icon, count, noun }: { icon: LucideIcon; count: number; noun: string }) {
  if (count === 0) {
    return null;
  }

  const words = plural(count, noun);
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex items-center gap-1 font-mono text-[11px] tabular-nums text-muted-foreground" aria-label={words}>
          <Icon className="size-3.5 shrink-0" aria-hidden />
          {count}
        </span>
      </TooltipTrigger>
      <TooltipContent>{words}</TooltipContent>
    </Tooltip>
  );
}

/** A small caption over a part of an opened table, in sentence case. */
function Caption({ icon: Icon, children }: { icon: LucideIcon; children: ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-[12px] font-medium text-muted-foreground">
      <Icon className="size-3.5 shrink-0" aria-hidden />
      {children}
    </span>
  );
}

/** One table of a context version: a summary row, and once opened what the file says about it. */
function TableDisclosure({ table }: { table: AiContextTable }) {
  // A version approved before files listed denied columns was stored without the list.
  const denied = table.denied ?? [];

  return (
    <Disclosure
      testId="ai-context-table-section"
      framed={false}
      summary={(
        <>
          <Table2 className="size-4 shrink-0 text-muted-foreground" aria-hidden />
          <span className="min-w-0 shrink truncate font-mono text-[12px] font-medium" title={table.display}>{table.display}</span>
          {table.businessName != null && (
            <span className="min-w-0 truncate text-[12px] text-muted-foreground" title={table.businessName}>{table.businessName}</span>
          )}
          <span className="ml-auto flex shrink-0 items-center gap-3 pl-2">
            <GlyphCount icon={Columns3} count={table.columns.length} noun="open column" />
            <GlyphCount icon={Lock} count={denied.length} noun="denied column" />
            <GlyphCount icon={Sigma} count={table.measures.length} noun="measure" />
            <GlyphCount icon={Link2} count={table.joins.length} noun="join" />
          </span>
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
          <Caption icon={Lock}>Denied, never used by the assistant</Caption>
          {denied.map((column) => (
            <span key={column} className="rounded-sm border border-border bg-muted/60 px-1.5 font-mono text-[11px]">{column}</span>
          ))}
        </div>
      )}

      <OpenColumns table={table} />

      {table.measures.length > 0 && (
        <div className="flex flex-col gap-1.5">
          <Caption icon={Sigma}>Measures</Caption>
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
        <div className="flex flex-col gap-1.5">
          <Caption icon={Link2}>Joins</Caption>
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

/** Which version the assistant uses while this one waits, in full: the hover behind the version strip. */
function servedNote(detail: AiContextDetail): string {
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
      return `Version ${proposed} is approved, and the assistant uses it.`;
    case "changed":
      return `Version ${proposed} waits for approval. Until then the assistant uses the approved version ${approved}, `
        + "without anything the file has removed since; approving serves the new one instead.";
    case "invalid":
      return approved === null
        ? `Version ${proposed} cannot be approved, and nothing of this context is served.`
        : `Version ${proposed} cannot be approved; the assistant still uses the approved version ${approved}.`;
    default:
      return `Version ${proposed} is served only once an admin approves it.`;
  }
}

/** One side of the version strip: a caption, the version (or what stands in for one), and what is known about it. */
function VersionCell({ caption, icon: Icon, tone, version, empty, meta, aside, testId }: {
  caption: string;
  icon: LucideIcon;
  tone: string;
  version: string | null;
  /** What stands where the version would be when there is none. */
  empty: string;
  meta: ReactNode;
  /** What sits at the end of the caption. */
  aside?: ReactNode;
  testId: string;
}) {
  return (
    <div className="flex min-w-0 flex-col gap-1 px-4 py-2.5">
      <span className="flex items-center gap-1.5 text-[12px] font-medium text-muted-foreground">
        <Icon className={cn("size-3.5 shrink-0", tone)} aria-hidden />
        {caption}
        {aside}
      </span>
      {version === null
        ? <span className="text-[14px] text-muted-foreground" data-testid={testId}>{empty}</span>
        : <span className="font-mono text-[15px] font-semibold tracking-tight" data-testid={testId}>{version}</span>}
      <span className="flex min-w-0 flex-wrap items-baseline gap-x-1.5 text-[12px] text-muted-foreground">{meta}</span>
    </div>
  );
}

/** A dot between the facts of one line. */
const Dot = () => <span aria-hidden>·</span>;

/**
 * What the assistant uses now beside what the file proposes: the two versions an approval is a decision between, with
 * the arrow showing which way approving goes. The whole story in a sentence is on the hover of its info glyph.
 */
function VersionStrip({ detail }: { detail: AiContextDetail }) {
  const { summary } = detail;
  const servedNow = summary.approvedVersion;
  const proposed = summary.currentVersion;
  const same = servedNow !== null && servedNow === proposed;
  const note = (
    <RichTooltip body={servedNote(detail)} title="Which version is used">
      <button
        type="button"
        className="-my-1 rounded-sm p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground"
        aria-label="Which version is used"
        data-testid="ai-context-served-note"
      >
        <Info className="size-3.5" />
      </button>
    </RichTooltip>
  );

  const serving = servedNow === null
    ? (
      <VersionCell
        caption="Serving now"
        icon={EyeOff}
        tone="text-muted-foreground"
        version={null}
        empty="Nothing"
        meta={<span>Never approved, so the assistant uses nothing of it.</span>}
        aside={note}
        testId="ai-context-approved-version"
      />
    )
    : (
      <VersionCell
        caption="Serving now"
        icon={ShieldCheck}
        tone="text-success"
        version={shortVersion(servedNow)}
        empty=""
        meta={(
          <>
            <span className="min-w-0 truncate">{`Approved by ${summary.approvedBy ?? "unknown"}`}</span>
            {summary.approvedUtc != null && <><Dot /><RelativeTime value={summary.approvedUtc} absolute={false} /></>}
            <Dot />
            <span className="font-mono tabular-nums">{`${summary.servedTableCount} of ${summary.tableCount}`}</span>
            <span>tables served</span>
            {same && (
              <>
                <Dot />
                <span className="inline-flex items-center gap-1">
                  <CircleCheck className="size-3.5 shrink-0 text-success" aria-hidden />
                  the file proposes this version, read <RelativeTime value={summary.syncedUtc} absolute={false} />
                </span>
              </>
            )}
          </>
        )}
        aside={note}
        testId="ai-context-approved-version"
      />
    );

  // The version approved is the one the file proposes: there is no decision to show, so the strip is one cell.
  if (same) {
    return (
      <div className="rounded-lg border border-border bg-card" data-testid="ai-context-versions">
        {serving}
      </div>
    );
  }

  const proposedCell = proposed === null
    ? (
      <VersionCell
        caption="Proposed by the file"
        icon={OctagonAlert}
        tone="text-destructive"
        version={null}
        empty="None: the file does not read"
        meta={<span>Fix the problems below; the next sync proposes a version.</span>}
        testId="ai-context-proposed-version"
      />
    )
    : (
      <VersionCell
        caption="Proposed by the file"
        icon={summary.status === "invalid" ? OctagonAlert : Clock3}
        tone={summary.status === "invalid" ? "text-destructive" : "text-warning"}
        version={shortVersion(proposed)}
        empty=""
        meta={(
          <>
            <span>{summary.status === "invalid" ? "Cannot be approved" : "Waits for approval"}</span>
            <Dot />
            <span className="whitespace-nowrap">read <RelativeTime value={summary.syncedUtc} absolute={false} /></span>
            <Dot />
            <span className="font-mono tabular-nums">{summary.tableCount}</span>
            <span>{summary.tableCount === 1 ? "table" : "tables"}</span>
          </>
        )}
        testId="ai-context-proposed-version"
      />
    );

  return (
    <div className="rounded-lg border border-border bg-card" data-testid="ai-context-versions">
      <div className="grid grid-cols-1 @2xl:grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)]">
        {serving}
        <div className="flex items-center justify-center border-y border-border bg-muted/40 py-1 text-muted-foreground @2xl:border-x @2xl:border-y-0 @2xl:px-2 @2xl:py-0">
          <ArrowRight className="size-4 rotate-90 @2xl:rotate-0" aria-label="approving serves" />
        </div>
        {proposedCell}
      </div>
    </div>
  );
}

/**
 * A problem as the sync reports it (`<file>:<line>: <what>`), split into the line and what is wrong there. The file is
 * the context's own, already in the header, so only the line is kept; a problem naming another file keeps its name.
 */
function splitProblem(problem: string, path: string): { location: string | null; message: string } {
  const match = /^(\S+?\.ya?ml)(?::(\d+))?: ([\s\S]+)$/.exec(problem);
  if (match === null) {
    return { location: null, message: problem };
  }

  const [, file, line, message] = match;
  if (file === path) {
    return { location: line === undefined ? null : `line ${line}`, message };
  }

  return { location: line === undefined ? file : `${file}, line ${line}`, message };
}

/** Why the version cannot be approved: each problem with the line of the file it is on. */
function ProblemsCard({ problems, path }: { problems: string[]; path: string }) {
  return (
    <Card title="Why it cannot be approved" icon={OctagonAlert} tone="text-destructive" count={problems.length} testId="ai-context-problems">
      <p className="px-3 pt-2 text-[12px] text-muted-foreground">Fix these in the file; the next sync proposes a new version to approve.</p>
      <ul className="flex flex-col divide-y divide-border">
        {problems.map((problem) => {
          const { location, message } = splitProblem(problem, path);
          return (
            <li key={problem} className="flex min-w-0 items-start gap-2.5 px-3 py-2">
              <span className="min-w-0 flex-1 break-words text-[13px]">{message}</span>
              {location !== null && (
                <span className="shrink-0 rounded-sm border border-border bg-muted/60 px-1.5 font-mono text-[11px] text-muted-foreground">{location}</span>
              )}
            </li>
          );
        })}
      </ul>
    </Card>
  );
}

/** A neutral chip with a change kind's glyph and how many there are, for the head of the changes card. */
function ChangeCount({ kind, count }: { kind: AiContextChange["kind"]; count: number }) {
  const glyph = changeGlyphs[kind];
  const Icon = glyph.icon;
  return (
    <span className="inline-flex items-center gap-1 rounded-md border border-border bg-muted/60 px-1.5 py-0.5 text-[11px]">
      <Icon className={cn("size-3.5 shrink-0", glyph.tone)} aria-hidden />
      <span className="font-mono tabular-nums">{count}</span>
      <span className="text-muted-foreground">{kind}</span>
    </span>
  );
}

/** What approving the proposed version does: each change, what it adds (and so opens to the assistant) first. */
function ChangesCard({ changes, firstApproval, approvable }: { changes: AiContextChange[]; firstApproval: boolean; approvable: boolean }) {
  const ordered = changeOrder.flatMap((kind) => changes.filter((change) => change.kind === kind));
  const title = approvable
    ? firstApproval ? "What approving serves" : "What approving changes"
    : firstApproval ? "What this version would serve" : "What this version would change";
  return (
    <Card
      title={title}
      icon={ShieldCheck}
      testId="ai-context-changes"
      aside={changeOrder.map((kind) => {
        const count = changes.filter((change) => change.kind === kind).length;
        return count === 0 ? null : <ChangeCount key={kind} kind={kind} count={count} />;
      })}
    >
      <ul className="flex flex-col divide-y divide-border">
        {ordered.map((change, i) => {
          const glyph = changeGlyphs[change.kind];
          const Icon = glyph.icon;
          return (
            <li
              key={`${change.kind}|${change.subject}|${i}`}
              className="flex min-w-0 items-start gap-2.5 px-3 py-2"
              data-testid="ai-context-change"
              data-kind={change.kind}
            >
              <Icon className={cn("mt-0.5 size-4 shrink-0", glyph.tone)} aria-label={glyph.label} />
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="break-words text-[13px]">{change.subject}</span>
                {change.detail != null && <span className="break-words font-mono text-[11px] text-muted-foreground">{change.detail}</span>}
              </span>
            </li>
          );
        })}
      </ul>
    </Card>
  );
}

/** The content of one version: its instructions, tables and examples, each a card of its own. */
function VersionContent({ version, served }: { version: AiContextVersion; served: boolean }) {
  return (
    <>
      <Card
        title={served ? "Tables the assistant uses" : "Tables in this version"}
        icon={Table2}
        count={version.tables.length}
        testId="ai-context-tables"
      >
        {version.tables.length === 0
          ? <p className="px-3 py-2 text-[12px] text-muted-foreground">This version lists no table.</p>
          : (
            <div className="flex flex-col divide-y divide-border">
              {version.tables.map((table) => <TableDisclosure key={table.objectKey} table={table} />)}
            </div>
          )}
      </Card>
      {version.instructions != null && (
        <Card title="Instructions" icon={ScrollText} testId="ai-context-instructions">
          <div className="mx-3 my-2.5 whitespace-pre-wrap break-words border-l-2 border-input pl-3 text-[12px] leading-relaxed">
            {version.instructions}
          </div>
        </Card>
      )}
      {version.examples.length > 0 && (
        <Card title="Example questions" icon={MessageSquareQuote} count={version.examples.length} testId="ai-context-examples">
          <div className="flex flex-col divide-y divide-border">
            {version.examples.map((example: AiContextExample) => (
              <ExampleDisclosure key={example.question} question={example.question} sql={example.sql} sourceRef={example.sourceRef} framed={false} />
            ))}
          </div>
        </Card>
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

/** Why Approve cannot be pressed for this context now, or null when it can. */
function approveBlockedReason(detail: AiContextDetail): string | null {
  const { summary } = detail;
  if (summary.currentVersion == null) {
    return "Its file does not read, so there is no version to approve. Fix the problems it lists; the next sync proposes one.";
  }

  if (detail.problems.length > 0) {
    return "This version has problems. Fix them in the file; the next sync proposes a new version to approve.";
  }

  return summary.status === "awaiting-approval" || summary.status === "changed" ? null : "This version is already approved.";
}

/** The height a comparison takes for `lines` lines: the whole of a short file, a readable window onto a long one. */
const diffHeight = (lines: number) => Math.min(900, Math.max(280, lines * 19 + 48));

/**
 * The context's file itself, as the last sync read it: the YAML an admin reviews beside what the review makes of it, with
 * the language engine's hover docs and checks. When the file differs from the one approved, the changes since approval
 * are one switch away.
 */
function ContextFileView({ summary }: { summary: AiContextSummary }) {
  const [compare, setCompare] = useState(false);
  const query = useQuery({ queryKey: aiContextYamlKey(summary.name), queryFn: () => aiContextApi.yaml(summary.name) });

  if (query.isPending) {
    return (
      <div className="flex flex-1 flex-col gap-2 p-4">
        <Skeleton className="h-5 w-96 max-w-full" />
        <Skeleton className="min-h-60 flex-1 rounded-lg" />
      </div>
    );
  }

  if (query.isError) {
    return (
      <div className="p-4">
        {isApiError(query.error)
          ? <CorrelationError error={query.error} />
          : <p className="text-[13px] text-destructive">{errorText(query.error)}</p>}
      </div>
    );
  }

  const current = query.data.current ?? null;
  const approved = query.data.approved ?? null;
  const comparable = current !== null && approved !== null && approved !== current;
  const showing = current ?? approved;

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2 p-4" data-testid="ai-context-file-view">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
        <span className="inline-flex min-w-0 items-center gap-1.5 text-[12px] text-muted-foreground">
          <FileCode2 className="size-3.5 shrink-0" aria-hidden />
          {current === null
            ? approved === null
              ? "The last sync could not read the file."
              : "The last sync could not read the file; this is the file as it was approved."
            : <>As the last sync read it, <RelativeTime value={summary.syncedUtc} absolute={false} /></>}
        </span>
        {comparable && (
          <Tabs value={compare ? "changes" : "file"} onValueChange={(value) => setCompare(value === "changes")} className="ml-auto">
            <TabsList className="h-8">
              <TabsTrigger value="file" className="px-2.5 text-[12px]" data-testid="ai-context-file-whole">
                <FileCode2 />
                Whole file
              </TabsTrigger>
              <TabsTrigger value="changes" className="px-2.5 text-[12px]" data-testid="ai-context-file-changes">
                <GitCompareArrows />
                Changes since approval
              </TabsTrigger>
            </TabsList>
          </Tabs>
        )}
      </div>
      {showing === null
        ? <p className="text-[13px] text-muted-foreground">Fix the problems the review lists; the next sync reads the file again.</p>
        : comparable && compare
          ? (
            <DiffView
              original={approved}
              modified={current}
              language="yaml"
              height={diffHeight(Math.max(approved.split("\n").length, current.split("\n").length))}
              sideLabels={{
                original: `Approved ${shortVersion(summary.approvedVersion) ?? ""}`.trim(),
                modified: `Proposed ${shortVersion(summary.currentVersion) ?? ""}`.trim(),
              }}
              data-testid="ai-context-file-diff"
            />
          )
          : <CodeView value={showing} language="yaml" fill data-testid="ai-context-yaml" />}
    </div>
  );
}

/** The head of the review, which stays in view while it scrolls: what the context is, where it stands, the switch between
 * the review and the file, and the actions. */
function ReviewHeader({ summary, views, actions }: { summary: AiContextSummary; views: ReactNode; actions: ReactNode }) {
  return (
    <div className="sticky top-0 z-10 flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-border bg-card px-4 py-2.5">
      <div className="flex size-9 shrink-0 items-center justify-center rounded-lg border border-border bg-muted/60">
        <Sparkles className="size-4 text-foreground" aria-hidden />
      </div>
      <div className="flex min-w-0 flex-[1_1_20rem] flex-col gap-0.5">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="font-mono text-[15px] font-semibold">{summary.name}</h2>
          <AiContextStatusChip status={summary.status} />
        </div>
        <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-0.5">
          {summary.description != null && (
            <p className="min-w-0 max-w-full truncate text-[13px] text-muted-foreground" title={summary.description}>{summary.description}</p>
          )}
          <div className="flex min-w-0 flex-[1_1_16rem] items-center gap-1.5">
            <FileCode2 className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
            <div className="min-w-0 flex-1"><FileRef repoName={summary.repoName} path={summary.path} /></div>
          </div>
        </div>
      </div>
      <div className="flex shrink-0 flex-wrap items-center gap-2">
        {views}
        {actions}
      </div>
    </div>
  );
}

/**
 * One AI context in the bottom panel, laid out as a review: where it stands and the actions in a header that stays in
 * view, the version the assistant uses beside the one its file proposes, why that cannot be approved or what approving
 * it changes, and what the version holds. An admin approves the version shown (the request carries its hash, so a file
 * that changed meanwhile is refused rather than approved unseen) or revokes the approval; nothing here edits the
 * context, whose file is where it is written.
 */
export function AiContextDetailPanel({ name }: { name: string }) {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const admin = hasScope("admin");
  const [confirming, setConfirming] = useState<"approve" | "revoke" | null>(null);
  const [view, setView] = useState<"review" | "file">("review");

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
      <div className="flex flex-col gap-3 p-4" data-testid="ai-context-detail">
        <div className="flex items-center gap-3">
          <Skeleton className="size-10 rounded-lg" />
          <Skeleton className="h-6 w-72 max-w-full" />
        </div>
        <Skeleton className="h-20 w-full rounded-lg" />
        <Skeleton className="h-32 w-full rounded-lg" />
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
  const approveBlocked = approveBlockedReason(detail);
  const changeCount = countChanges(detail.changes);
  const review = detail.problems.length > 0 || (summary.status !== "approved" && detail.changes.length > 0);

  const actions = admin && (
    <div className="flex items-center gap-2">
      {summary.approvedVersion != null && (
        <Button
          variant="outline"
          size="sm"
          disabled={revoke.isPending || approve.isPending}
          onClick={() => setConfirming("revoke")}
          data-testid="ai-context-revoke"
        >
          {revoke.isPending ? <Loader2 className="animate-spin" /> : <ShieldOff />}
          Revoke
        </Button>
      )}
      {/* An approved version has nothing to approve; a version that cannot be approved says why on hover. */}
      {summary.status !== "approved" && (
        <Gated reason={approveBlocked}>
          <Button
            size="sm"
            disabled={approveBlocked !== null || approve.isPending || revoke.isPending}
            onClick={() => setConfirming("approve")}
            data-testid="ai-context-approve"
          >
            {approve.isPending ? <Loader2 className="animate-spin" /> : <ShieldCheck />}
            {proposed != null && approveBlocked === null ? `Approve ${shortVersion(proposed)}` : "Approve"}
          </Button>
        </Gated>
      )}
    </div>
  );

  const views = (
    <TabsList className="h-8">
      <TabsTrigger value="review" className="px-2.5 text-[12px]" data-testid="ai-context-view-review">
        <ClipboardCheck />
        Review
      </TabsTrigger>
      <TabsTrigger value="file" className="px-2.5 text-[12px]" data-testid="ai-context-view-file">
        <FileCode2 />
        File
      </TabsTrigger>
    </TabsList>
  );

  return (
    <Tabs
      value={view}
      onValueChange={(value) => setView(value === "file" ? "file" : "review")}
      className="@container flex min-h-full flex-col gap-0 bg-background"
      data-testid="ai-context-detail"
    >
      <ReviewHeader summary={summary} views={views} actions={actions} />

      <TabsContent value="file" className="flex min-h-0 flex-1 flex-col">
        <ContextFileView summary={summary} />
      </TabsContent>

      <TabsContent value="review" className="flex flex-col gap-3 p-4">
        <VersionStrip detail={detail} />

        <div className={cn("grid items-start gap-3", review && "@4xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]")}>
          {review && (
            <div className="flex min-w-0 flex-col gap-3">
              {detail.problems.length > 0 && <ProblemsCard problems={detail.problems} path={summary.path} />}
              {summary.status !== "approved" && detail.changes.length > 0 && (
                <ChangesCard
                  changes={detail.changes}
                  firstApproval={summary.approvedVersion == null}
                  approvable={approveBlocked === null}
                />
              )}
            </div>
          )}
          {shown != null && (
            <div className="flex min-w-0 flex-col gap-3">
              <VersionContent version={shown} served={summary.status === "approved"} />
            </div>
          )}
        </div>
      </TabsContent>

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
    </Tabs>
  );
}
