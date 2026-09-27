// What every flow kind looks like in the GUI: the name it goes by, its icon and its colour, so a pipeline's kind reads
// at a glance wherever the pipeline is shown (its page and tab, the pipelines tree, its runs). SQLFlow's own kinds are
// described here; a module describes the kinds it adds through its FlowKindContribution, and a kind neither knows is
// shown by its value with a neutral icon.

import {
  Braces, CalendarDays, Copy, FileInput, FileOutput, GitBranch, Globe, HeartPulse, Import, ListOrdered, Server,
  SquareFunction, TableProperties, Workflow, Zap,
} from "lucide-react";
import { kindContribution, type FlowKindIdentity, type FlowKindTone } from "./registry";

/**
 * The classes that put a tone on screen. The colour marks the glyph, the chip's fill and edge, and the edge of a card;
 * text stays in the text tokens, because slot 3 is below text contrast on the light card (DESIGN.md 3.4).
 * Written out whole so Tailwind finds every class in the source.
 */
export interface FlowKindToneClasses {
  /** The glyph's colour. */
  icon: string;
  /** The fill and border of a chip, or of the tile behind a larger glyph. */
  surface: string;
  /** A card's top edge, in the kind's colour. */
  edge: string;
}

const TONE_CLASSES: Readonly<Record<FlowKindTone, FlowKindToneClasses>> = {
  blue: { icon: "text-chart-1", surface: "border-chart-1/40 bg-chart-1/15", edge: "border-t-chart-1" },
  violet: { icon: "text-chart-7", surface: "border-chart-7/40 bg-chart-7/15", edge: "border-t-chart-7" },
  orange: { icon: "text-chart-6", surface: "border-chart-6/40 bg-chart-6/15", edge: "border-t-chart-6" },
  magenta: { icon: "text-chart-3", surface: "border-chart-3/40 bg-chart-3/15", edge: "border-t-chart-3" },
  neutral: { icon: "text-muted-foreground", surface: "border-border bg-muted", edge: "border-t-muted-foreground" },
};

/**
 * SQLFlow's own kinds, every one the document loader recognises (docs/reference/flow/overview.md), acquisition first and
 * then transform and utility: the order the pipelines filter offers them in.
 */
const BUILT_IN = new Map<string, FlowKindIdentity>([
  ["file", { label: "File", icon: FileInput, tone: "blue" }],
  ["ing", { label: "Ingestion", icon: Import, tone: "violet" }],
  ["api", { label: "API", icon: Globe, tone: "blue" }],
  ["cpy", { label: "Copy", icon: Copy, tone: "blue" }],
  ["sftp", { label: "SFTP", icon: Server, tone: "blue" }],
  ["exp", { label: "Export", icon: FileOutput, tone: "orange" }],
  ["trl", { label: "Translate", icon: Braces, tone: "orange" }],
  ["sp", { label: "Stored procedure", icon: SquareFunction, tone: "violet" }],
  ["inv", { label: "Invoke", icon: Zap, tone: "neutral" }],
  ["hc", { label: "Health check", icon: HeartPulse, tone: "neutral" }],
  ["scm", { label: "Source control", icon: GitBranch, tone: "neutral" }],
  ["sch", { label: "Schema registration", icon: TableProperties, tone: "neutral" }],
  ["batch", { label: "Batch", icon: ListOrdered, tone: "neutral" }],
  ["cal", { label: "Calendar", icon: CalendarDays, tone: "violet" }],
]);

/** SQLFlow's own kinds, in the order above. */
export function builtInFlowKinds(): readonly string[] {
  return [...BUILT_IN.keys()];
}

/**
 * How a flow kind is shown: as the module that adds it describes it, as SQLFlow describes its own, or, for a kind
 * neither knows (a module's kind that describes nothing, a value the catalog holds that this build predates), by its
 * value with a neutral icon.
 */
export function flowKindIdentity(kind: string): FlowKindIdentity {
  return kindContribution(kind)?.identity
    ?? BUILT_IN.get(kind)
    ?? { label: kind.trim() === "" ? "unknown" : kind, icon: Workflow, tone: "neutral" };
}

/** The classes that show a tone. */
export function flowKindToneClasses(tone: FlowKindTone): FlowKindToneClasses {
  return TONE_CLASSES[tone];
}
