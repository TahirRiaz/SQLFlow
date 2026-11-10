import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Command, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { searchApi } from "../../api/endpoints";

/** Local debounce so the catalog search fires after typing pauses, not per keystroke. */
function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

/**
 * The in-toolbar search, over two scopes at once: the drawn graph and the whole catalog. Matching only the drawn
 * nodes would make the box useless for the thing people actually want - finding a table or a flow they cannot see,
 * because the canvas holds one project and the estate holds thousands of objects. So every query also runs against
 * the catalog's object and flow search. Picking a drawn node focuses and centers it in place; picking a catalog hit
 * re-seeds the graph on that node (via ?focus=), which draws its own upstream/downstream context. cmdk's own
 * filtering is off: the catalog hits are already server-ranked and must not be re-filtered away client side.
 * Options carry role="option" for accessibility.
 */
export function GraphSearch({ options, onPickNode, onPickCatalog }: {
  options: { id: string; name: string }[];
  onPickNode: (id: string) => void;
  onPickCatalog: (id: string, repoId?: string) => void;
}) {
  const [query, setQuery] = useState("");
  const trimmed = query.trim();
  const debounced = useDebounced(trimmed, 300);
  const open = trimmed !== "";
  // One character matches most of the estate, so the catalog call waits for a term that can actually narrow.
  const catalogEnabled = debounced.length >= 2;

  const drawn = useMemo(() => new Set(options.map((option) => option.id)), [options]);
  const localMatches = useMemo(() => {
    const needle = trimmed.toLowerCase();
    return needle === ""
      ? []
      : options.filter((option) => option.name.toLowerCase().includes(needle)).slice(0, 20);
  }, [options, trimmed]);

  const objectsQuery = useQuery({
    queryKey: ["lineage-search-objects", debounced],
    enabled: catalogEnabled,
    queryFn: () => searchApi.objects(debounced, { pageSize: 8 }),
  });
  const flowsQuery = useQuery({
    queryKey: ["lineage-search-flows", debounced],
    enabled: catalogEnabled,
    queryFn: () => searchApi.flows(debounced, { pageSize: 8 }),
  });

  // A catalog hit already on the canvas is dropped: the in-graph group above focuses it without a refetch, and
  // offering the same node twice would only invite a pointless re-seed.
  const objectHits = (objectsQuery.data?.items ?? []).filter((hit) => !drawn.has(hit.key));
  const flowHits = (flowsQuery.data?.items ?? []).filter((hit) => !drawn.has(hit.id));
  const searching = catalogEnabled && (objectsQuery.isFetching || flowsQuery.isFetching);
  const catalogError = objectsQuery.isError || flowsQuery.isError;
  const empty = localMatches.length === 0 && objectHits.length === 0 && flowHits.length === 0;

  const pickNode = (id: string) => {
    setQuery("");
    onPickNode(id);
  };
  const pickCatalog = (id: string, repoId?: string) => {
    setQuery("");
    onPickCatalog(id, repoId);
  };

  return (
    <Command
      shouldFilter={false}
      className="relative w-60 overflow-visible rounded-md border border-input bg-transparent **:data-[slot=command-input-wrapper]:h-8 **:data-[slot=command-input-wrapper]:border-b-0"
    >
      <CommandInput
        value={query}
        onValueChange={setQuery}
        placeholder="Search objects and flows"
        aria-label="Search objects and flows"
        data-testid="graph-node-search"
        className="h-8 py-0 text-[13px]"
      />
      {open && (
        <CommandList
          className="absolute top-full left-0 z-20 mt-1 max-h-96 w-96 overflow-y-auto rounded-md border border-border bg-popover shadow-md"
          data-testid="graph-search-results"
          // Keep the input focused while an option is clicked, so the list is not dismissed mid-click.
          onMouseDown={(event) => event.preventDefault()}
        >
          {localMatches.length > 0 && (
            <CommandGroup heading="In this graph">
              {localMatches.map((option) => (
                <CommandItem
                  key={option.id}
                  value={option.id}
                  onSelect={() => pickNode(option.id)}
                  className="font-mono text-[12px]"
                >
                  {option.name}
                </CommandItem>
              ))}
            </CommandGroup>
          )}
          {objectHits.length > 0 && (
            <CommandGroup heading="Objects in the catalog">
              {objectHits.map((hit) => (
                <CommandItem
                  key={hit.key}
                  value={hit.key}
                  onSelect={() => pickCatalog(hit.key)}
                  data-testid="graph-search-object"
                >
                  <div className="min-w-0 flex-1 overflow-hidden">
                    <div className="truncate font-mono text-[12px]">{hit.name}</div>
                    <div className="truncate text-xs text-muted-foreground">
                      {[hit.kind, hit.database ?? hit.serverRef, hit.schema].filter((part) => part !== null && part !== "").join(" · ")}
                    </div>
                  </div>
                </CommandItem>
              ))}
            </CommandGroup>
          )}
          {flowHits.length > 0 && (
            <CommandGroup heading="Flows in the catalog">
              {flowHits.map((hit) => (
                <CommandItem
                  key={hit.id}
                  value={hit.id}
                  onSelect={() => pickCatalog(hit.id, hit.repoId)}
                  data-testid="graph-search-flow"
                >
                  <div className="min-w-0 flex-1 overflow-hidden">
                    <div className="truncate font-mono text-[12px]">{hit.name}</div>
                    <div className="truncate text-xs text-muted-foreground">
                      {hit.kind} · {hit.repoName}
                    </div>
                  </div>
                </CommandItem>
              ))}
            </CommandGroup>
          )}
          {empty && (
            <div className="px-3 py-3 text-[13px] text-muted-foreground" data-testid="graph-search-status">
              {searching
                ? "Searching the catalog..."
                : catalogError
                  ? "The catalog search failed. Retry the query."
                  : catalogEnabled
                    ? "Nothing in the graph or the catalog matches."
                    : "Type at least two characters to search the catalog."}
            </div>
          )}
        </CommandList>
      )}
    </Command>
  );
}
