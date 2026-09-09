import type { ReactNode } from "react";
import { CircleHelp, FileText, Layers, Link2, MonitorPlay, Split, SquareFunction, Table2, Zap } from "lucide-react";

/** Display metadata for one object kind: the folder label the tree shows and the leaf icon. */
export interface KindMeta {
  /** The kind value as the catalog stores it (CatalogObject.Kind). */
  kind: string;
  /** The plural folder label ("Tables", "Views", ...). */
  plural: string;
  icon: ReactNode;
}

/** Every known object kind in display order; kinds the catalog invents later fall back to `metaForKind`. */
export const KIND_ORDER: readonly KindMeta[] = [
  { kind: "Table", plural: "Tables", icon: <Table2 className="size-4" /> },
  { kind: "View", plural: "Views", icon: <Layers className="size-4" /> },
  { kind: "Procedure", plural: "Procedures", icon: <Split className="size-4" /> },
  { kind: "Function", plural: "Functions", icon: <SquareFunction className="size-4" /> },
  { kind: "Trigger", plural: "Triggers", icon: <Zap className="size-4" /> },
  { kind: "Synonym", plural: "Synonyms", icon: <Link2 className="size-4" /> },
  { kind: "File", plural: "Files", icon: <FileText className="size-4" /> },
  { kind: "Subscriber", plural: "Subscribers", icon: <MonitorPlay className="size-4" /> },
  { kind: "Unknown", plural: "Unknown", icon: <CircleHelp className="size-4" /> },
];

const BY_KIND = new Map(KIND_ORDER.map((meta) => [meta.kind, meta]));

/** The display metadata for a kind, tolerating a value outside the known set (shown as-is with a neutral icon). */
export function metaForKind(kind: string): KindMeta {
  return BY_KIND.get(kind) ?? { kind, plural: kind, icon: <CircleHelp className="size-4" /> };
}

/** Sorts kind values into the display order above; unknown kinds sort after the known ones, alphabetically. */
export function compareKinds(a: string, b: string): number {
  const ia = KIND_ORDER.findIndex((meta) => meta.kind === a);
  const ib = KIND_ORDER.findIndex((meta) => meta.kind === b);
  if (ia >= 0 && ib >= 0) {
    return ia - ib;
  }
  if (ia >= 0) {
    return -1;
  }
  if (ib >= 0) {
    return 1;
  }
  return a.localeCompare(b);
}
