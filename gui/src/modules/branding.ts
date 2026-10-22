// The product's branding: the name and attribution the title bar and login page carry, the logos, the login page's
// pitch and the data its brand panel draws flowing in, and the document title. The defaults are SQLFlow's own. A module
// build may brand the product (at most one module may), and a deployment may override the text and logo paths in
// config.json. Kept free of imports, so the unit suite reads it.

export interface BrandLogos {
  /** The full lockup: the login page's brand panel and its narrow-screen plaque. */
  full: string;
  /** The mark on the dark title bar. */
  white: string;
  /** The square mark. */
  icon: string;
}

export interface LoginCopy {
  /** The pitch's first line, the brand panel's headline. */
  headline: string;
  /** The pitch's second line, set under the headline in the accent. */
  headlineAccent: string;
  /** The one sentence under the pitch. */
  tagline: string;
  /**
   * What the brand panel draws flowing into the product, a word or two each: the kinds of data it takes. Drawn in this
   * order, and repeated when there are fewer than the picture has records for.
   */
  streams: readonly string[];
}

export interface Branding {
  /** The product's name, shown in the title bar, the login card, and wherever the product names itself. */
  productName: string;
  /** The "powered by" lockup beside the product name in the title bar; null for none. */
  attribution: string | null;
  documentTitle: string;
  logos: BrandLogos;
  login: LoginCopy;
}

/** What a module sets; anything it leaves out keeps the default. */
export interface BrandingContribution {
  productName?: string;
  attribution?: string | null;
  documentTitle?: string;
  logos?: Partial<BrandLogos>;
  login?: Partial<LoginCopy>;
}

/** What a deployment may override in config.json: the text and asset paths, not the data the brand panel draws. */
export interface RuntimeBrandingOverrides {
  productName?: string;
  attribution?: string | null;
  documentTitle?: string;
  logos?: Partial<BrandLogos>;
  login?: { headline?: string; headlineAccent?: string; tagline?: string };
}

const SQLFLOW_BRANDING: Branding = {
  productName: "SQLFlow",
  attribution: null,
  documentTitle: "SQLFlow",
  logos: {
    full: "/brand/logo-full.png",
    white: "/brand/logo-white.png",
    icon: "/brand/logo-icon.png",
  },
  login: {
    headline: "Move data with confidence.",
    headlineAccent: "Follow it end to end.",
    tagline: "Land files, load tables and trace lineage across your estate, from one control plane.",
    // The file formats SQLFlow reads (docs/reference/concepts/file-source-pipeline.md).
    streams: ["CSV", "Parquet", "JSON", "Excel", "XML"],
  },
};

let moduleBranding: Branding = SQLFLOW_BRANDING;
let current: Branding = SQLFLOW_BRANDING;

function merge(base: Branding, change: BrandingContribution): Branding {
  return {
    productName: change.productName ?? base.productName,
    attribution: change.attribution !== undefined ? change.attribution : base.attribution,
    documentTitle: change.documentTitle ?? base.documentTitle,
    logos: {
      full: change.logos?.full ?? base.logos.full,
      white: change.logos?.white ?? base.logos.white,
      icon: change.logos?.icon ?? base.logos.icon,
    },
    login: {
      headline: change.login?.headline ?? base.login.headline,
      headlineAccent: change.login?.headlineAccent ?? base.login.headlineAccent,
      tagline: change.login?.tagline ?? base.login.tagline,
      streams: change.login?.streams ?? base.login.streams,
    },
  };
}

/** Applies the branding of the registered modules. Called by registerModules. */
export function installModuleBranding(contributions: readonly { moduleId: string; branding: BrandingContribution }[]): void {
  if (contributions.length > 1) {
    throw new Error(
      `Only one GUI module may brand the product; ${contributions.map((c) => `'${c.moduleId}'`).join(" and ")} all do.`,
    );
  }

  moduleBranding = contributions.length === 1 ? merge(SQLFLOW_BRANDING, contributions[0].branding) : SQLFLOW_BRANDING;
  current = moduleBranding;
}

/** Applies a deployment's config.json overrides on top of the build's branding. */
export function applyRuntimeBranding(overrides: RuntimeBrandingOverrides | undefined): void {
  current = overrides === undefined ? moduleBranding : merge(moduleBranding, overrides);
}

/** The branding in effect. */
export function branding(): Branding {
  return current;
}

function text(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() !== "" ? value : undefined;
}

function section(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

/**
 * Reads the `branding` section of config.json. Only non-blank strings are taken (and an explicit null attribution, which
 * removes the lockup); any other value is ignored, so a malformed deployment file falls back to the build's branding
 * instead of breaking the page.
 */
export function parseRuntimeBranding(value: unknown): RuntimeBrandingOverrides | undefined {
  const root = section(value);
  if (root === undefined) {
    return undefined;
  }

  const overrides: RuntimeBrandingOverrides = {};
  const productName = text(root.productName);
  if (productName !== undefined) {
    overrides.productName = productName;
  }

  const attribution = root.attribution === null ? null : text(root.attribution);
  if (attribution !== undefined) {
    overrides.attribution = attribution;
  }

  const documentTitle = text(root.documentTitle);
  if (documentTitle !== undefined) {
    overrides.documentTitle = documentTitle;
  }

  const logos = section(root.logos);
  if (logos !== undefined) {
    overrides.logos = { full: text(logos.full), white: text(logos.white), icon: text(logos.icon) };
  }

  const login = section(root.login);
  if (login !== undefined) {
    overrides.login = { headline: text(login.headline), headlineAccent: text(login.headlineAccent), tagline: text(login.tagline) };
  }

  return overrides;
}
