import { readLocalStorageState } from "@/hooks/useLocalStorageState";

/**
 * The graph's whole state (scope, view, focus, expansions) lives in its query string, and it rewrites that
 * string with `replace: true` so the browser's history holds no earlier drawing to go back to. Leaving for the
 * object explorer would therefore drop the graph the operator had built. The graph remembers its query string
 * here, and the explorer's "Graph view" button restores it, so the round trip lands back on the same drawing.
 */
const GRAPH_SEARCH_KEY = "sqlflow.lineage.graph.search";

/** Called by the graph whenever its query string changes; an empty string forgets the remembered drawing. */
export function rememberGraphSearch(search: string): void {
  try {
    window.localStorage.setItem(GRAPH_SEARCH_KEY, JSON.stringify(search));
  } catch {
    // Storage being unavailable (private mode, quota) only costs the restore; navigation still works.
  }
}

/** The path back to the graph: the last drawing when one was remembered, otherwise the bare landing. */
export function graphViewPath(): string {
  const search = readLocalStorageState(GRAPH_SEARCH_KEY, "");
  return search === "" ? "/lineage" : `/lineage?${search}`;
}
