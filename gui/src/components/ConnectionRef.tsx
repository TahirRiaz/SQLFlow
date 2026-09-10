import { CopyButton } from "./CopyButton";
import { RichTooltip } from "./RichTooltip";
import { TruncatedText } from "./TruncatedText";

interface ConnectionRefProps {
  /** The raw source/target value: a `${env:...}` / `${keyvault:...}` reference, a literal, or null. */
  value: string | null | undefined;
  /** Hard cap in pixels on the rendered identifier, so one long reference never stretches a column. */
  maxWidth?: number;
  /** Shown when `value` is null/empty (matches the tables' "-" placeholder). */
  placeholder?: string;
  copyTestId?: string;
}

/** A parsed secret reference: the scheme's short badge label and the meaningful identifier to surface. */
interface ParsedRef {
  label: string;
  display: string;
}

/**
 * Reduce a `${scheme:locator}` reference to the part worth reading. The scheme wrapper and the shared
 * `SQLFLOW_CONN_` env prefix are boilerplate that only bloats a grid, and a key vault locator's meaning is its
 * secret name, not the vault it lives in. Returns null for anything that is not a whole reference (literals,
 * `@alias` names), which then render as plain truncated text.
 */
function parseRef(value: string): ParsedRef | null {
  const match = /^\$\{(env|keyvault):(.+)\}$/.exec(value.trim());
  if (match === null) {
    return null;
  }

  const [, scheme, locator] = match;
  if (scheme === "keyvault") {
    // ${keyvault:vault/secret} -> the secret name, the last path segment.
    const segments = locator.split("/");
    return { label: "kv", display: segments[segments.length - 1] || locator };
  }

  // ${env:SQLFLOW_CONN_NAME} -> "NAME"; the canonical prefix is on every connection env var, so it is noise.
  const stripped = locator.replace(/^SQLFLOW_CONN_/, "");
  return { label: "env", display: stripped || locator };
}

/**
 * The canonical way to render a pipeline source/target reference in a table cell or detail row. A
 * `${env:...}` / `${keyvault:...}` reference becomes a compact labelled chip showing only the distinctive
 * identifier, with the full reference in a hover tooltip and an icon copy that yields it verbatim. Literals and
 * anything unparsed fall back to a truncated, copyable value, so a long reference can never blow out the layout
 * yet the exact string is always one hover or click away.
 */
export function ConnectionRef({ value, maxWidth = 200, placeholder = "-", copyTestId }: ConnectionRefProps) {
  if (value === null || value === undefined || value === "") {
    return <>{placeholder}</>;
  }

  const parsed = parseRef(value);
  if (parsed === null) {
    return <TruncatedText text={value} mono maxWidth={maxWidth} copy copyTestId={copyTestId} />;
  }

  return (
    <span className="inline-flex max-w-full items-center gap-1.5 rounded-md border border-border/60 bg-muted/40 py-0.5 pr-0.5 pl-2 align-bottom text-[11px]">
      <span className="font-medium uppercase tracking-wide text-muted-foreground">{parsed.label}</span>
      <RichTooltip body={value} title="Reference" mono>
        <span
          className="inline-block max-w-full overflow-hidden text-ellipsis whitespace-nowrap align-bottom font-mono text-[11px] text-foreground"
          style={{ maxWidth }}
        >
          {parsed.display}
        </span>
      </RichTooltip>
      <CopyButton iconOnly label="Copy reference" text={value} testId={copyTestId ?? "copy-connection-ref"} />
    </span>
  );
}
