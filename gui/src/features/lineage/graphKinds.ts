import { Boxes, Eye, FileText, Folder, Globe, Server, Table2, Users, type LucideIcon } from "lucide-react";
import { flowKindIdentity, flowKindToneClasses } from "../../modules/flowKinds";
import type { BaseNodeType } from "./graphModel";

/** How a node kind is shown wherever the graph names it: on a card, in the legend, in a list of members. */
export interface KindLook {
  label: string;
  icon: LucideIcon;
  /** The glyph's colour class. A flow kind wears its tone (DESIGN.md 3.5); data is neutral, so flows lead the eye. */
  iconClass: string;
}

const OBJECT_ICONS: Readonly<Record<string, LucideIcon>> = {
  table: Table2,
  view: Eye,
  file: FileText,
  api: Globe,
  sftp: Server,
  subscriber: Users,
};

export function kindLook(type: BaseNodeType | "group", kind: string): KindLook {
  if (type === "group") {
    return { label: "Folder", icon: Folder, iconClass: "text-muted-foreground" };
  }
  if (type === "flow") {
    const identity = flowKindIdentity(kind);
    return { label: identity.label, icon: identity.icon, iconClass: flowKindToneClasses(identity.tone).icon };
  }
  // A dataset of an external system is named by its system, which the server already wrote in words.
  return { label: kind, icon: OBJECT_ICONS[kind] ?? Boxes, iconClass: "text-muted-foreground" };
}

/** "3 flows", "1 object": a count with its noun, singular when it is one. */
export function counted(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? "" : "s"}`;
}
