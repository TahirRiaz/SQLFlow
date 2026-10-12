import { useQuery, type QueryClient } from "@tanstack/react-query";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { aiContextApi } from "../../api/endpoints";
import type { ReportSpecSummary } from "../../api/types";

/** The query-key root of every AI context and semantic layer read, so an approval refreshes the contexts, the tree,
 * the object, and the lists at once. */
export const SEMANTIC_ROOT = "semantic-layer";

/** One error-to-text mapping for every toast and inline error on these pages (the API's detail wins). */
export function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

/** Refreshes everything these pages read: approving or revoking a context, or removing an answer, can move objects in or
 * out of what is served, which changes the contexts, coverage counts and served status at once. */
export function refreshSemanticLayer(queryClient: QueryClient): void {
  void queryClient.invalidateQueries({ queryKey: [SEMANTIC_ROOT] });
}

/** database.schema.name, skipping the parts that are unknown. */
export function qualifiedName(database: string | null, schema: string | null, name: string): string {
  return [database, schema, name].filter((part): part is string => part !== null).join(".");
}

/** A count and its noun, plural when it is not one. */
export function plural(count: number, word: string): string {
  return `${count.toLocaleString()} ${word}${count === 1 ? "" : "s"}`;
}

/** A version hash as it is shown: its first twelve characters, enough to tell versions apart. */
export function shortVersion(version: string | null | undefined): string | null {
  return version == null ? null : version.slice(0, 12);
}

/**
 * Columns a little closer than a list's, with the card's own margin kept at both edges, so a grid of several columns
 * fits a narrow panel. Applied to the element that wraps a DataTable or PagedTable.
 */
export const COMPACT_GRID = cn(
  "min-w-0 [&_td]:px-2 [&_th]:px-2",
  "[&_td:first-child]:pl-3 [&_th:first-child]:pl-3 [&_td:last-child]:pr-3 [&_th:last-child]:pr-3",
);

/** A header row that stays in view while the rows under it scroll. */
export const STICKY_HEADER = "[&_th]:sticky [&_th]:top-0 [&_th]:z-10 [&_th]:bg-card [&_th]:shadow-[inset_0_-1px_0_var(--border)]";

/**
 * A grid that fits its panel: compact columns, its rows scrolling inside the room `useScrollFit` gives the table, under a
 * header that stays in view.
 */
export const FITTED_GRID = cn(COMPACT_GRID, "[&_[data-slot=table-container]]:overflow-y-auto", STICKY_HEADER);

/** The room kept under a fitted grid: the editor's own bottom padding. */
export const FIT_BELOW = 24;

/** The fewest pixels a fitted grid keeps, so a short window still shows a few rows. */
export const FIT_MIN = 160;

/** One line counting what a report holds. */
export function describeReport(summary: Pick<ReportSpecSummary, "pages" | "visuals" | "tables" | "measures">): string {
  return [
    plural(summary.pages, "page"),
    plural(summary.visuals, "visual"),
    plural(summary.tables, "model table"),
    plural(summary.measures, "measure"),
  ].join(", ");
}

/** The contexts and whether earlier GUI content waits to be exported: one read shared by the page header and the grid. */
export function useAiContexts() {
  return useQuery({ queryKey: [SEMANTIC_ROOT, "ai-contexts"], queryFn: () => aiContextApi.list() });
}

/** The query key of one context's detail, under the root every approval refreshes. */
export const aiContextKey = (name: string) => [SEMANTIC_ROOT, "ai-contexts", name] as const;

/** The query key of one context's file text, under the same root, so an approval or a sync refreshes it too. */
export const aiContextYamlKey = (name: string) => [SEMANTIC_ROOT, "ai-contexts", name, "yaml"] as const;
