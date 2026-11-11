// The workbench's designs (DESIGN.md 3.7): the look its colour tokens and title bar mark take. SQLFlow's own design is
// always offered; a module may add designs of its own, each with a light and a dark mode, and may name one of them, in one
// mode, as what a person sees until they pick. What a person wears is remembered in this browser and put on again at
// their next visit. Kept free of imports, so the unit suite reads it.

export type ThemeMode = "light" | "dark";

export const THEME_MODES: readonly ThemeMode[] = ["light", "dark"];

/** Where the browser keeps the mode a person wears; the name SQLFlow has always kept it under. */
export const MODE_STORAGE_KEY = "sqlflow.theme";

/** Where the browser keeps the design a person wears. */
export const DESIGN_STORAGE_KEY = "sqlflow.design";

/**
 * The part of an image a mark shows, for a mark cut from a larger picture (the symbol of a lockup that also holds a
 * wordmark): the whole image's size and the part's box, in one set of units (an SVG's viewBox units, say).
 */
export interface DesignMarkCrop {
  imageWidth: number;
  imageHeight: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

/** An image the title bar shows as the product's mark. */
export interface DesignMark {
  /** The image's URL: a path the build serves, or an absolute https URL. */
  src: string;
  /** The part of the image to show; omitted, the whole image. */
  crop?: DesignMarkCrop;
}

/** A design a module adds. */
export interface DesignContribution {
  /**
   * Lowercase letters, digits and hyphens, starting with a letter, unique across modules and other than `sqlflow`: the
   * value `<html data-sf-design>` takes while the design is on, which the module's stylesheet keys the design's tokens on,
   * and what the browser remembers.
   */
  id: string;
  /** The design's name in the title bar's design menu. */
  label: string;
  /**
   * Stylesheets the design needs beyond the build's own, by absolute https URL (a typeface a font service serves): put in
   * the page the first time the design is on, and never for a person who does not wear it.
   */
  stylesheets?: readonly string[];
  /** The title bar's mark in each mode while the design is on; without it, the branding's logo. */
  marks?: Readonly<Record<ThemeMode, DesignMark>>;
  /**
   * Makes this design, in this mode, what a person wears until they pick one; at most one design may. Without one, a
   * person starts in SQLFlow's design, in the mode their operating system prefers.
   */
  firstMode?: ThemeMode;
}

/** A design the workbench offers. */
export interface Design {
  id: string;
  label: string;
  stylesheets: readonly string[];
  marks: Readonly<Record<ThemeMode, DesignMark>> | null;
}

/** What the workbench wears: a design, in a mode. */
export interface Appearance {
  design: string;
  mode: ThemeMode;
}

/** What the browser holds of a person's appearance, as read: either part may be missing or not a value it can be. */
export interface StoredAppearance {
  design: string | null;
  mode: string | null;
}

/** The appearance to wear, and whether it is the operating system's preference, which is followed live and not kept. */
export interface ResolvedAppearance {
  appearance: Appearance;
  followsSystem: boolean;
}

export const SQLFLOW_DESIGN: Design = { id: "sqlflow", label: "SQLFlow", stylesheets: [], marks: null };

const DESIGN_ID = /^[a-z][a-z0-9-]*$/;

let offered: readonly Design[] = [SQLFLOW_DESIGN];
let first: Appearance | null = null;

export function isThemeMode(value: unknown): value is ThemeMode {
  return value === "light" || value === "dark";
}

function checkStylesheet(moduleId: string, designId: string, url: string): void {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    throw new Error(`GUI module '${moduleId}' gives the design '${designId}' the stylesheet '${url}', which is not an absolute URL.`);
  }

  if (parsed.protocol !== "https:") {
    throw new Error(`GUI module '${moduleId}' gives the design '${designId}' the stylesheet '${url}', which is not served over https.`);
  }
}

function checkMark(moduleId: string, designId: string, mode: ThemeMode, mark: DesignMark | undefined): void {
  const what = `GUI module '${moduleId}' gives the design '${designId}' a ${mode} title bar mark`;
  if (mark === undefined || mark.src.trim() === "") {
    throw new Error(`${what} with no image; a design with marks names one for each mode.`);
  }

  const crop = mark.crop;
  if (crop === undefined) {
    return;
  }

  const sizes = [crop.imageWidth, crop.imageHeight, crop.width, crop.height];
  const offsets = [crop.x, crop.y];
  if (!sizes.every((size) => Number.isFinite(size) && size > 0) || !offsets.every((offset) => Number.isFinite(offset) && offset >= 0)) {
    throw new Error(`${what} whose crop is not a box: its sizes must be positive and its offsets zero or more.`);
  }

  if (crop.x + crop.width > crop.imageWidth || crop.y + crop.height > crop.imageHeight) {
    throw new Error(
      `${what} whose crop (${crop.x}, ${crop.y}, ${crop.width} by ${crop.height}) runs past the image (${crop.imageWidth} by ${crop.imageHeight}).`,
    );
  }
}

/**
 * Installs the designs of the registered modules, after SQLFlow's own. Called by registerModules, which runs once, so
 * every design is checked before the first render and a conflicting one fails at startup with a message naming it.
 */
export function installModuleDesigns(contributions: readonly { moduleId: string; designs: readonly DesignContribution[] }[]): void {
  const owners = new Map<string, string>([[SQLFLOW_DESIGN.id, "SQLFlow"]]);
  const labels = new Map<string, string>([[SQLFLOW_DESIGN.label.toLowerCase(), "SQLFlow"]]);
  const designs: Design[] = [SQLFLOW_DESIGN];
  let firstOwner: string | null = null;
  let firstAppearanceFound: Appearance | null = null;

  for (const { moduleId, designs: added } of contributions) {
    for (const design of added) {
      if (!DESIGN_ID.test(design.id)) {
        throw new Error(
          `GUI module '${moduleId}' adds the design '${design.id}'; a design id is lowercase letters, digits and hyphens, starting with a letter.`,
        );
      }

      const owner = owners.get(design.id);
      if (owner !== undefined) {
        throw new Error(`GUI module '${moduleId}' adds the design '${design.id}', which ${owner === "SQLFlow" ? "SQLFlow" : `module '${owner}'`} already adds.`);
      }

      owners.set(design.id, moduleId);
      const label = design.label.trim();
      if (label === "") {
        throw new Error(`GUI module '${moduleId}' adds the design '${design.id}' with a blank label.`);
      }

      const labelOwner = labels.get(label.toLowerCase());
      if (labelOwner !== undefined) {
        throw new Error(
          `GUI module '${moduleId}' labels the design '${design.id}' '${label}', which ${labelOwner === "SQLFlow" ? "SQLFlow's design" : `a design of module '${labelOwner}'`} is already called.`,
        );
      }

      labels.set(label.toLowerCase(), moduleId);
      for (const url of design.stylesheets ?? []) {
        checkStylesheet(moduleId, design.id, url);
      }

      if (design.marks !== undefined) {
        for (const mode of THEME_MODES) {
          checkMark(moduleId, design.id, mode, design.marks[mode]);
        }
      }

      if (design.firstMode !== undefined) {
        if (!isThemeMode(design.firstMode)) {
          throw new Error(
            `GUI module '${moduleId}' starts people in the design '${design.id}' in the mode '${String(design.firstMode)}'; the modes are light and dark.`,
          );
        }

        if (firstOwner !== null) {
          throw new Error(
            `GUI module '${moduleId}' starts people in the design '${design.id}', and ${firstOwner} already starts them in another.`,
          );
        }

        firstOwner = `module '${moduleId}'`;
        firstAppearanceFound = { design: design.id, mode: design.firstMode };
      }

      designs.push({ id: design.id, label, stylesheets: [...(design.stylesheets ?? [])], marks: design.marks ?? null });
    }
  }

  offered = designs;
  first = firstAppearanceFound;
}

/** The designs the workbench offers: SQLFlow's, then the modules' in registration order. */
export function offeredDesigns(): readonly Design[] {
  return offered;
}

/** The design with this id, or SQLFlow's when none is offered under it. */
export function designById(id: string): Design {
  return offered.find((design) => design.id === id) ?? SQLFLOW_DESIGN;
}

/**
 * The appearance a person wears, from what the browser keeps of their last one:
 *
 * - the design they wore, while it is still offered; otherwise the design a module starts people in, or SQLFlow's;
 * - the mode they wore; otherwise, in the design a module starts people in, its mode; otherwise the operating system's
 *   preference, which is then followed live (`followsSystem`) rather than kept, as SQLFlow has always done.
 */
export function pickAppearance(
  stored: StoredAppearance,
  prefersDark: boolean,
  designs: readonly Pick<Design, "id">[],
  firstAppearance: Appearance | null,
): ResolvedAppearance {
  const design = stored.design !== null && designs.some((offeredDesign) => offeredDesign.id === stored.design)
    ? stored.design
    : firstAppearance?.design ?? SQLFLOW_DESIGN.id;
  if (isThemeMode(stored.mode)) {
    return { appearance: { design, mode: stored.mode }, followsSystem: false };
  }

  if (firstAppearance !== null && design === firstAppearance.design) {
    return { appearance: { design, mode: firstAppearance.mode }, followsSystem: false };
  }

  return { appearance: { design, mode: prefersDark ? "dark" : "light" }, followsSystem: true };
}

/** pickAppearance over the designs the registered modules offer. */
export function resolveAppearance(stored: StoredAppearance, prefersDark: boolean): ResolvedAppearance {
  return pickAppearance(stored, prefersDark, offered, first);
}
