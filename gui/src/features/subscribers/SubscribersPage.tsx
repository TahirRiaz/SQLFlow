import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Code, MonitorPlay, Table2, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { lineageApi } from "../../api/endpoints";
import type { Subscriber } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { activeFilterClass, FilterBar } from "../../components/FilterBar";
import { LineageJumpButton } from "../../components/LineageJumpButton";
import { LinkRef } from "../../components/LinkRef";
import { NoteRef } from "../../components/NoteRef";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { TruncatedText } from "../../components/TruncatedText";
import { SubscriberDetails } from "./SubscriberDetails";

/** Local debounce for the free-text filter: the list re-queries 400ms after the user stops typing. */
function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

// Every free-text column is capped in pixels, because the table cells are `whitespace-nowrap` on an auto
// layout: one uncapped 200-character report URL makes the table wider than the editor and pushes the counts
// off-screen entirely, which is what this grid used to do. The caps are sized so all nine columns fit a
// workbench window without horizontal scroll, and nothing is lost by clipping: every capped cell reveals its
// full value in a hover panel and, where the exact string matters, hands it over with a copy button.
/** One count in the grid: the glyph for what is being counted, the number, and the words on hover. */
function Count(
  { icon, value, label, plural }: { icon: ReactNode; value: number; label: string; plural?: string },
) {
  const words = `${value} ${value === 1 ? label : plural ?? `${label}s`}`;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex items-center justify-end gap-1.5 align-bottom" aria-label={words}>
          <span className="text-muted-foreground [&>svg]:size-3.5">{icon}</span>
          <span className="tabular-nums">{value}</span>
        </span>
      </TooltipTrigger>
      <TooltipContent>{words}</TooltipContent>
    </Tooltip>
  );
}

const subscriberColumns: Column<Subscriber>[] = [
  {
    id: "name",
    header: "Subscriber",
    render: (row) => (
      <span className="flex min-w-0 items-center gap-1.5">
        <MonitorPlay className="size-4 shrink-0 text-muted-foreground" />
        <TruncatedText text={row.name} maxWidth={185} className="text-[13px] font-medium" />
      </span>
    ),
    width: 220,
  },
  { id: "type", header: "Type", render: (row) => <Badge variant="secondary">{row.type}</Badge>, width: 92 },
  {
    id: "owner",
    header: "Owner",
    render: (row) => <TruncatedText text={row.owner} mono maxWidth={126} />,
    width: 150,
  },
  // Description and notes say different things (what the report is FOR, versus what someone found wrong with
  // it), so both stay visible. Description takes the slack, since it is the one a reader scans by.
  {
    id: "description",
    header: "Description",
    render: (row) => (
      <TruncatedText text={row.description} maxWidth={300} title="Description" className="text-muted-foreground" />
    ),
  },
  {
    id: "notes",
    header: "Notes",
    // A glyph, not the first thirty characters: every note in this estate opens with the same boilerplate, so
    // a clipped column reads as one repeated string and costs the width Description needs.
    render: (row) => <NoteRef note={row.notes} testId="subscriber-note" />,
    align: "center",
    width: 72,
  },
  {
    id: "url",
    header: "Location",
    // Where the report lives, so the list answers "open it" as well as "what does it read". A report URL is a
    // couple of hundred characters of workspace and report GUIDs that nobody reads, so the cell carries the two
    // things anyone actually does with it, follow and copy, and the string itself lives in the hover panel.
    render: (row) => <LinkRef url={row.url} testId="subscriber-location" />,
    align: "center",
    width: 88,
  },
  // Two bare integers side by side cannot be told apart while scanning a row: "7  7" reads as one value
  // rendered twice, since neither number carries what it counts and the header is a row away. Each count
  // therefore leads with the glyph for the thing it counts, which is legible without moving the eye off the
  // row, and says it in full on hover.
  {
    id: "objects",
    header: "Reads",
    align: "right",
    render: (row) => <Count icon={<Table2 />} value={row.objectCount} label="object" />,
    width: 92,
  },
  {
    id: "queries",
    header: "Queries",
    align: "right",
    render: (row) => <Count icon={<Code />} value={row.queryCount} label="query" plural="queries" />,
    width: 96,
  },
  {
    id: "lineage",
    header: "",
    align: "right",
    // Jumps straight into the lineage graph seeded on this subscriber: what it reads, and (one hop further) the
    // flow that populates each of those tables, without first opening the drawer.
    render: (row) => (
      <LineageJumpButton target={{ kind: "subscriber", subscriberKey: row.key, label: row.name }} iconOnly />
    ),
    width: 56,
  },
];

/**
 * The consumption side of the estate: who reads the warehouse. Every row is a declared data subscriber (a
 * report, workbook, notebook, or application) whose queries were parsed into lineage, so the counts here are
 * what the graph actually proved, not a hand-kept inventory. Opening one shows everything it reads and the
 * queries behind each link; the reverse question, "who consumes this table", is the Consumers tab on any
 * object in the catalog.
 */
export default function SubscribersPage() {
  const [params, setParams] = useSearchParams();
  const selected = params.get("key");
  const [search, setSearch] = useState("");
  const [type, setType] = useState("all");
  const debouncedSearch = useDebounced(search, 400);

  const subscribers = useQuery({
    queryKey: ["subscribers", type, debouncedSearch],
    queryFn: () => lineageApi.subscribers({
      type: type === "all" ? undefined : type,
      search: debouncedSearch === "" ? undefined : debouncedSearch,
    }),
  });

  // The type filter offers exactly the types the estate declares: the field is free text by design (it was a
  // free nvarchar in the legacy model), so a fixed list would hide whatever this estate actually uses.
  const types = useMemo(
    () => [...new Set((subscribers.data ?? []).map((row) => row.type))].sort((a, b) => a.localeCompare(b)),
    [subscribers.data],
  );

  const select = (key: string | null) => {
    const next = new URLSearchParams(params);
    if (key === null) {
      next.delete("key");
    } else {
      next.set("key", key);
    }

    setParams(next, { replace: true });
  };

  return (
    <Page>
      <PageHeader
        title="Subscribers"
        subtitle="Who consumes the warehouse. Declared in a subscribers.yaml; every query is parsed into lineage."
      />

      <FilterBar>
        <Input
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder="Search name, owner, description, notes, location ..."
          className={search === "" ? undefined : activeFilterClass}
          data-testid="subscriber-search"
        />
        <Select value={type} onValueChange={setType}>
          <SelectTrigger className={type === "all" ? "w-44" : `w-44 ${activeFilterClass}`} data-testid="subscriber-type">
            <SelectValue placeholder="Type" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All types</SelectItem>
            {types.map((value) => <SelectItem key={value} value={value}>{value}</SelectItem>)}
          </SelectContent>
        </Select>
        {(search !== "" || type !== "all") && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => { setSearch(""); setType("all"); }}
            data-testid="subscriber-clear-filters"
          >
            <X className="size-4" />
            Clear
          </Button>
        )}
      </FilterBar>

      {subscribers.isPending ? (
        <Skeleton className="h-64 w-full" />
      ) : subscribers.isError ? (
        isApiError(subscribers.error)
          ? <CorrelationError error={subscribers.error} />
          : <p className="text-[13px] text-destructive">{String(subscribers.error)}</p>
      ) : subscribers.data.length === 0 ? (
        <EmptyState
          title="No subscribers declared."
          description="Add a subscribers.yaml to a repo to record who reads the warehouse; its queries become lineage on the next sync."
        />
      ) : (
        <DataTable<Subscriber>
          columns={subscriberColumns}
          rows={subscribers.data}
          rowKey={(row) => row.key}
          onRowClick={(row) => select(row.key)}
          emptyMessage="No subscriber matches the filters."
          data-testid="subscribers-table"
        />
      )}

      <Sheet open={selected !== null} onOpenChange={(open) => { if (!open) select(null); }}>
        <SheetContent className="w-full overflow-y-auto sm:max-w-3xl" data-testid="subscriber-drawer">
          <SheetTitle className="sr-only">Subscriber</SheetTitle>
          <SheetDescription className="sr-only">What this subscriber reads, and the queries behind it.</SheetDescription>
          <SheetClose />
          <div className="p-4">{selected !== null && <SubscriberDetails subscriberKey={selected} />}</div>
        </SheetContent>
      </Sheet>
    </Page>
  );
}
