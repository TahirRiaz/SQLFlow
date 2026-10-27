// How a module lays out the whole menu: the groups the activity bar and the side bar show, in order, and the entries each
// lists, straight under it or under one of its sections. Kept apart from the navigation model, with nothing to import, so
// the unit suite reads it.

/** A labelled part of a laid-out group: an id unique within the group, its label, and its entries' paths in order. */
export interface MenuSectionLayout {
  id: string;
  label: string;
  entries: readonly string[];
}

/** A group of a laid-out menu. `Icon` is the icon type the workbench draws. */
export interface MenuGroupLayout<Icon> {
  id: string;
  label: string;
  icon: Icon;
  /** Renders only for sessions holding this scope. */
  requiresScope?: string;
  /** Anchored at the bottom of the activity bar, after every top group. */
  bottom?: boolean;
  /** The paths of the entries listed straight under the group, before its sections, in order. */
  entries?: readonly string[];
  sections?: readonly MenuSectionLayout[];
}

/** A whole menu: its groups, the top ones first, in the order the activity bar and the side bar show them. */
export interface MenuLayout<Icon> {
  groups: readonly MenuGroupLayout<Icon>[];
}

/** A group as the layout places it: its sections (without their entries) and its entries, each carrying its section. */
export interface LaidOutGroup<Icon, Entry> {
  group: MenuGroupLayout<Icon>;
  sections: { id: string; label: string }[];
  items: Entry[];
}

/**
 * Places every entry where the layout lists it, each carrying the id of its section, none for an entry listed straight
 * under its group. Reports through `fail`, which throws, a group or section without an id or a label or laid out twice,
 * one listing nothing, a top group after a bottom one, a path no entry has or one listed twice, and the entries the
 * layout leaves out, so a page added later cannot drop out of the menu unnoticed.
 */
export function layOutMenu<Icon, Entry extends { to: string; section?: string }>(
  entries: readonly Entry[],
  layout: MenuLayout<Icon>,
  fail: (message: string) => never,
): LaidOutGroup<Icon, Entry>[] {
  const byPath = new Map(entries.map((entry) => [entry.to, entry]));
  const listed = new Set<string>();
  const groupIds = new Set<string>();
  let firstBottom: string | null = null;

  const place = (path: string, where: string, section: string | undefined): Entry => {
    const entry = byPath.get(path) ?? fail(`lists '${path}' in ${where}, which is not the path of a navigation entry.`);
    if (listed.has(path)) {
      fail(`lists the navigation entry '${path}' twice, the second time in ${where}.`);
    }

    listed.add(path);
    return { ...entry, section };
  };

  const laidOut = layout.groups.map((group): LaidOutGroup<Icon, Entry> => {
    if (group.id.trim() === "" || group.label.trim() === "") {
      fail("lays out a navigation group without an id or a label.");
    }

    if (groupIds.has(group.id)) {
      fail(`lays out the navigation group '${group.id}' twice.`);
    }

    groupIds.add(group.id);
    if (group.bottom === true) {
      firstBottom ??= group.id;
    } else if (firstBottom !== null) {
      fail(`lays out the top group '${group.id}' after the bottom group '${firstBottom}'.`);
    }

    const items = (group.entries ?? []).map((path) => place(path, `the group '${group.id}'`, undefined));
    const sections: { id: string; label: string }[] = [];
    for (const section of group.sections ?? []) {
      if (section.id.trim() === "" || section.label.trim() === "") {
        fail(`lays out a section of the navigation group '${group.id}' without an id or a label.`);
      }

      if (sections.some((existing) => existing.id === section.id)) {
        fail(`lays out the section '${section.id}' of the navigation group '${group.id}' twice.`);
      }

      if (section.entries.length === 0) {
        fail(`lays out the section '${section.id}' of the navigation group '${group.id}' with no entry.`);
      }

      sections.push({ id: section.id, label: section.label });
      const where = `the section '${section.id}' of the group '${group.id}'`;
      items.push(...section.entries.map((path) => place(path, where, section.id)));
    }

    if (items.length === 0) {
      fail(`lays out the navigation group '${group.id}' with no entry.`);
    }

    return { group, sections, items };
  });

  const left = entries.filter((entry) => !listed.has(entry.to)).map((entry) => `'${entry.to}'`);
  if (left.length > 0) {
    fail(`leaves ${left.length === 1 ? "the navigation entry" : "the navigation entries"} ${left.join(", ")} out of its menu.`);
  }

  return laidOut;
}
