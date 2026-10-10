import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { semanticLayerApi } from "../../api/endpoints";
import { Mono } from "../../components/Mono";
import { errorText, qualifiedName, SEMANTIC_ROOT, useDebouncedValue } from "./shared";

/** A table chosen from the semantic layer. */
export interface PickedTable {
  key: string;
  name: string;
  database: string | null;
  schema: string | null;
}

/**
 * Picks one table that is IN the semantic layer (at least one allowed column), by name or business name. Only layer
 * tables are offered because everything a picker feeds (a measure's anchor, a join's other side) is refused by the
 * server over a table outside the layer anyway.
 */
export function LayerTablePicker({
  value, onChange, locked = false, testId,
}: {
  value: PickedTable | null;
  onChange: (table: PickedTable | null) => void;
  locked?: boolean;
  testId: string;
}) {
  const [term, setTerm] = useState("");
  const debounced = useDebouncedValue(term.trim(), 300);
  const searching = value === null && debounced.length >= 2;

  const results = useQuery({
    queryKey: [SEMANTIC_ROOT, "picker", debounced],
    queryFn: () => semanticLayerApi.objects({ name: debounced, inLayer: true, pageSize: 20 }),
    enabled: searching,
  });

  if (value !== null) {
    return (
      <div className="flex min-h-8 items-center gap-2" data-testid={`${testId}-selected`}>
        <Mono>{qualifiedName(value.database, value.schema, value.name)}</Mono>
        {!locked && (
          <Button variant="ghost" size="xs" onClick={() => onChange(null)} data-testid={`${testId}-change`}>
            Change
          </Button>
        )}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-1.5">
      <Input
        value={term}
        onChange={(event) => setTerm(event.target.value)}
        placeholder="Search tables in the semantic layer"
        aria-label="Search tables in the semantic layer"
        className="h-8"
        data-testid={`${testId}-search`}
      />
      {searching && (
        <div className="max-h-48 overflow-y-auto rounded-md border" data-testid={`${testId}-results`}>
          {results.isPending && <p className="px-2 py-1.5 text-[13px] text-muted-foreground">Searching…</p>}
          {results.isError && <p className="px-2 py-1.5 text-[13px] text-destructive">{errorText(results.error)}</p>}
          {results.data !== undefined && results.data.items.length === 0 && (
            <p className="px-2 py-1.5 text-[13px] text-muted-foreground">
              No table in the semantic layer matches. Allow at least one of a table&apos;s columns to bring it in.
            </p>
          )}
          {results.data?.items.map((item) => (
            <button
              type="button"
              key={item.key}
              className="flex w-full items-center justify-between gap-2 px-2 py-1.5 text-left text-[13px] hover:bg-accent"
              onClick={() => {
                onChange({ key: item.key, name: item.name, database: item.database, schema: item.schema });
                setTerm("");
              }}
            >
              <span className="min-w-0 truncate font-mono text-[12px]">
                {qualifiedName(item.database, item.schema, item.name)}
              </span>
              <span className="shrink-0 text-xs text-muted-foreground">
                {`${item.allowedColumns}/${item.totalColumns} columns`}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
