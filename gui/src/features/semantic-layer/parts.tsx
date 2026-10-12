import type { ReactNode } from "react";
import { CircleCheck, Clock3, EyeOff, FileDiff, OctagonAlert, ShieldCheck, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import type { AiContextStatus } from "../../api/types";
import { RelativeTime } from "../../components/RelativeTime";
import { CopyButton } from "../../components/CopyButton";
import { RichTooltip } from "../../components/RichTooltip";
import { TruncatedText } from "../../components/TruncatedText";
import { useClipped } from "../../components/useClipped";

/**
 * A word in a neutral chip led by a glyph in a status tone (DESIGN.md 3.2): the tone marks the state, the chip and the
 * word stay in the text tokens, so a column of them never reads as a block of tinted pills.
 */
export function ToneChip({ icon: Icon, tone, children, testId }: {
  icon: LucideIcon;
  /** The text utility of the glyph's tone (`text-success`, `text-warning`, ...). */
  tone: string;
  children: ReactNode;
  testId?: string;
}) {
  return (
    <Badge variant="secondary" className="rounded-md border-border bg-muted/60 text-[11px] text-foreground" data-testid={testId}>
      <Icon className={cn("shrink-0", tone)} aria-hidden />
      {children}
    </Badge>
  );
}

const statusVisuals: Record<AiContextStatus, { icon: LucideIcon; tone: string; label: string; hint: string }> = {
  "awaiting-approval": {
    icon: Clock3,
    tone: "text-warning",
    label: "Awaiting approval",
    hint: "Never approved. Nothing of it is served until an admin approves the version its file proposes.",
  },
  approved: {
    icon: ShieldCheck,
    tone: "text-success",
    label: "Approved",
    hint: "The version its file proposes is the approved one, and the assistant uses it.",
  },
  changed: {
    icon: FileDiff,
    tone: "text-warning",
    label: "Changed",
    hint: "Its file proposes a new version. The approved one is still served, without anything the file removed, until an admin approves the new one.",
  },
  invalid: {
    icon: OctagonAlert,
    tone: "text-destructive",
    label: "Invalid",
    hint: "The version its file proposes has problems and cannot be approved. An approved version, if there is one, is still served.",
  },
};

/**
 * A copy button for a question, to paste it into the chat or anywhere else. With `reveal` it stays out of sight until
 * the grid row it sits in is hovered or focused, so a column of questions does not carry a column of buttons.
 */
export function CopyQuestion({ question, reveal = false }: { question: string; reveal?: boolean }) {
  const button = <CopyButton iconOnly label="Copy the question" text={question} testId="copy-question" />;
  return reveal
    ? (
      <span className="inline-flex shrink-0 opacity-0 transition-opacity duration-120 focus-within:opacity-100 group-hover/row:opacity-100">
        {button}
      </span>
    )
    : button;
}

/**
 * Where an AI context stands, as a neutral chip with its tone glyph; what the state means on hover. With `collapse`, the
 * word gives way to the glyph alone when the grid it sits in is narrow, staying for assistive tech and in the hover.
 */
export function AiContextStatusChip({ status, collapse = false }: { status: AiContextStatus; collapse?: boolean }) {
  const visual = statusVisuals[status];
  return (
    <RichTooltip body={visual.hint} title={visual.label}>
      <span className="inline-flex" data-testid="ai-context-status" data-status={status}>
        <ToneChip icon={visual.icon} tone={visual.tone}>
          <span className={cn(collapse && "@max-3xl/table:sr-only")}>{visual.label}</span>
        </ToneChip>
      </span>
    </RichTooltip>
  );
}

/**
 * A context's file as `repo: path`, on one line in whatever width it is given, cut from the left so its end stays in
 * view: the repository and the folders repeat down a column, and the file name is what tells files apart. The whole
 * location is on hover whenever any of it is hidden.
 */
export function FileRef({ repoName, path }: { repoName: string; path: string }) {
  const cut = path.lastIndexOf("/");
  const full = `${repoName}: ${path}`;
  const [ref, clipped] = useClipped(full);

  const line = (
    // Right-to-left direction puts the ellipsis at the start, and text-left keeps a path that fits where a path starts;
    // the bdi keeps the path itself reading left to right.
    <span ref={ref} className="block truncate text-left font-mono text-[12px] [direction:rtl]" data-testid="ai-context-file">
      <bdi>
        <span className="text-muted-foreground">{`${repoName}: ${path.slice(0, cut + 1)}`}</span>
        {path.slice(cut + 1)}
      </bdi>
    </span>
  );

  return clipped ? <RichTooltip body={full} title="File" mono>{line}</RichTooltip> : line;
}

/**
 * What a column means, on one line clipped to its cell, with the whole description and the column's synonyms on hover:
 * the hover comes only when it adds something (the line is clipped, or there are synonyms the line does not show).
 */
export function DescribedText({ description, synonyms }: { description: string | null | undefined; synonyms: readonly string[] }) {
  const text = description ?? "";
  const [ref, clipped] = useClipped(text);
  if (text === "" && synonyms.length === 0) {
    return <span className="text-muted-foreground">-</span>;
  }

  const line = (
    <span ref={ref} className={cn("block truncate text-[12px]", text === "" && "text-muted-foreground")} data-testid="column-description">
      {text === "" ? `Also called ${synonyms.join(", ")}` : text}
    </span>
  );
  if (!clipped && synonyms.length === 0 && !text.includes("\n")) {
    return line;
  }

  const body = [text, synonyms.length === 0 ? "" : `Also called: ${synonyms.join(", ")}`].filter((part) => part !== "").join("\n\n");
  return <RichTooltip body={body} title="Description">{line}</RichTooltip>;
}

/**
 * A value clipped at a pixel cap in a cell the table sizes by its content. TruncatedText caps itself at a share of its
 * cell, which a table measuring its columns cannot honour, so the column would take the whole value's width; the cap is
 * set here on the wrapper too, which the measuring does honour.
 */
export function CellText({ text, maxWidth, mono, className }: { text: string | null | undefined; maxWidth: number; mono?: boolean; className?: string }) {
  return (
    <span className="inline-block max-w-full align-bottom" style={{ maxWidth }}>
      <TruncatedText text={text} maxWidth={maxWidth} mono={mono} className={className} />
    </span>
  );
}

/**
 * Who did something and how long ago, on one line in the width it is given: the name gives way first and the time stays
 * whole. `maxWidth` caps it in a cell sized by its content; without it, it takes the width of the cell it is in.
 */
export function Stamp({ who, when, maxWidth }: { who: string | null | undefined; when: string; maxWidth?: number }) {
  return (
    <span className="flex min-w-0 max-w-full items-baseline gap-1.5 text-[12px]" style={maxWidth === undefined ? undefined : { maxWidth }}>
      {who != null && <span className="min-w-0"><TruncatedText text={who} maxWidth={maxWidth ?? 480} /></span>}
      <span className="shrink-0 whitespace-nowrap text-muted-foreground"><RelativeTime value={when} absolute={false} /></span>
    </span>
  );
}

/**
 * Whether something an object or report defines is served to the assistant: a quiet mark when it is, and when it is not,
 * a muted "not served" whose reason is on hover. Nothing here is edited; the reason says what to change in the file.
 * `compact` is the glyph alone, for a grid column, with the word (and the reason) on hover.
 */
export function ServedMark({ problem, compact = false, testId }: {
  problem: string | null | undefined;
  compact?: boolean;
  testId?: string;
}) {
  const served = problem == null;
  const mark = (
    <span
      className="inline-flex items-center gap-1 align-bottom text-[11px] text-muted-foreground"
      data-testid={testId}
      data-served={served ? "true" : "false"}
    >
      {served
        ? <CircleCheck className="size-3.5 shrink-0 text-success" aria-label={compact ? "Served" : undefined} aria-hidden={!compact} />
        : <EyeOff className="size-3.5 shrink-0 text-warning" aria-label={compact ? "Not served" : undefined} aria-hidden={!compact} />}
      {!compact && (served ? "served" : "not served")}
    </span>
  );

  if (served) {
    return compact ? <RichTooltip body="The assistant is given it." title="Served">{mark}</RichTooltip> : mark;
  }

  return <RichTooltip body={problem} title="Not served">{mark}</RichTooltip>;
}
