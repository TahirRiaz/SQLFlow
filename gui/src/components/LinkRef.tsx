import { ExternalLink, Link2Off } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CopyButton } from "./CopyButton";
import { RichTooltip } from "./RichTooltip";
import { TruncatedText } from "./TruncatedText";

interface LinkRefProps {
  /** The declared location: an http(s) URL, or free text (a UNC path, a OneDrive folder, a share). */
  url: string | null | undefined;
  /** Caption for the hover panel, naming what the location is. */
  title?: string;
  /**
   * `icon` (the default) renders the link as a single glyph beside a copy button, for a table cell where a
   * 200-character URL would otherwise decide the column's width. `inline` also renders the URL as clipped
   * text, for a detail row with the room to show where the link actually goes.
   */
  variant?: "icon" | "inline";
  /** Hard cap in pixels on the URL text in the `inline` variant. */
  maxWidth?: number;
  /** Shown when `url` is null/empty (matches the tables' "-" placeholder). */
  placeholder?: string;
  testId?: string;
  copyTestId?: string;
}

/**
 * Whether a location can actually be opened from a browser. A location is free text and is just as often a
 * workbook path or a share as it is a report URL, and only http(s) survives a click.
 */
export function isFollowable(url: string): boolean {
  const value = url.trim().toLowerCase();
  return value.startsWith("http://") || value.startsWith("https://");
}

/**
 * The canonical way to render a location: the URL behind an open-in-new-tab glyph, its full text in a hover
 * panel, and an icon copy that yields it verbatim. A report URL runs to a couple of hundred characters of
 * opaque workspace and report GUIDs, none of which a reader scans; what they do with it is follow it or paste
 * it somewhere, so the column carries the two actions and the panel carries the string.
 *
 * A location that is not http(s) renders as truncated, copyable text instead. Rendering a UNC or file path as
 * an anchor produces a link that silently does nothing when clicked, which is worse than plain text because it
 * looks actionable.
 */
export function LinkRef({
  url, title = "Location", variant = "icon", maxWidth = 320, placeholder = "-", testId, copyTestId,
}: LinkRefProps) {
  if (url === null || url === undefined || url === "") {
    return <>{placeholder}</>;
  }

  // A location that is not http(s) is still a location: it is shown, but never as an anchor, because a link on a
  // UNC path silently does nothing when clicked and that is worse than plain text. In the icon variant it keeps
  // the column exactly as wide as a followable one, so a single share path cannot stretch the column the way an
  // unbounded URL used to stretch the table.
  if (!isFollowable(url)) {
    if (variant === "icon") {
      return (
        <span className="inline-flex max-w-full items-center gap-0.5 align-bottom">
          <RichTooltip body={url} title={`${title} (cannot be opened from a browser)`} mono>
            <span className="inline-flex size-6 items-center justify-center text-muted-foreground">
              <Link2Off className="size-4" aria-label="Location that cannot be opened from a browser" />
            </span>
          </RichTooltip>
          <CopyButton iconOnly label="Copy location" text={url} testId={copyTestId ?? "copy-location"} />
        </span>
      );
    }

    return (
      <TruncatedText text={url} mono maxWidth={maxWidth} title={title} copy copyTestId={copyTestId ?? "copy-location"} />
    );
  }

  return (
    <span className="inline-flex max-w-full items-center gap-0.5 align-bottom">
      <RichTooltip body={url} title={title} mono>
        <Button asChild variant="ghost" size="icon-xs" className="text-primary hover:text-primary">
          <a
            href={url}
            target="_blank"
            rel="noreferrer"
            // The glyph often sits inside a clickable row; opening the link must not also trigger the row.
            onClick={(event) => event.stopPropagation()}
            aria-label="Open location in a new tab"
            data-testid={testId ?? "open-location"}
          >
            <ExternalLink />
          </a>
        </Button>
      </RichTooltip>
      {variant === "inline" && (
        <a
          href={url}
          target="_blank"
          rel="noreferrer"
          onClick={(event) => event.stopPropagation()}
          className="min-w-0 text-primary hover:underline"
        >
          <TruncatedText text={url} mono maxWidth={maxWidth} title={title} />
        </a>
      )}
      <CopyButton iconOnly label="Copy location" text={url} testId={copyTestId ?? "copy-location"} />
    </span>
  );
}
