import { CopyButton } from "./CopyButton";
import { RichTooltip } from "./RichTooltip";

interface PathRefProps {
  /** The full relative path; only its file name (last segment) is shown, the whole path lives in the tooltip. */
  path: string | null | undefined;
  /** Hard cap in pixels on the rendered file name, so one long name never stretches a column. */
  maxWidth?: number;
  /** Shown when `path` is null/empty (matches the tables' "-" placeholder). */
  placeholder?: string;
  copyTestId?: string;
}

/**
 * Render a repo-relative path as just its file name, with the full path revealed on hover and an icon copy that
 * yields the whole path verbatim. A grid only needs the distinctive file name to scan by; the directory prefix
 * is shared boilerplate that bloats the column, so it moves to the tooltip and the clipboard.
 */
export function PathRef({ path, maxWidth = 240, placeholder = "-", copyTestId }: PathRefProps) {
  if (path === null || path === undefined || path === "") {
    return <>{placeholder}</>;
  }

  // Paths use forward slashes; the last non-empty segment is the file name.
  const segments = path.split("/");
  const fileName = segments[segments.length - 1] || path;

  return (
    <span className="inline-flex max-w-full items-center gap-1 align-bottom">
      <RichTooltip body={path} title="Path" mono>
        <span
          className="inline-block max-w-full overflow-hidden text-ellipsis whitespace-nowrap align-bottom font-mono text-[12px]"
          style={{ maxWidth }}
        >
          {fileName}
        </span>
      </RichTooltip>
      <CopyButton iconOnly label="Copy path" text={path} testId={copyTestId ?? "copy-path"} />
    </span>
  );
}
