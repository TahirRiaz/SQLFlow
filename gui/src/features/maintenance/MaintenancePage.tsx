import { useCallback, useMemo, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { BrushCleaning, Loader2, RefreshCw, TrendingUp } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import { ApiError, isApiError } from "../../api/client";
import { maintenanceApi } from "../../api/endpoints";
import type { StorageArea, StorageReport, StorageTable, StorageVerdict } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column, type TableGrouping } from "../../components/DataTable";
import { ExplainMark } from "../../components/ExplainTip";
import { IconAction } from "../../components/IconAction";
import { KpiCard } from "../../components/KpiCard";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RichTooltip } from "../../components/RichTooltip";
import { SearchInput } from "../../components/SearchInput";
import { useLocalStorageState } from "../../hooks/useLocalStorageState";
import { pollingInterval } from "../../hooks/usePolling";
import { tableContainer, useScrollFit } from "../../hooks/useScrollFit";
import { STORAGE_KEY } from "./storageQueries";
import { CleanupSheet } from "./CleanupSheet";
import { StorageAreaSheet } from "./StorageAreaSheet";
import { VerdictGlyph, VerdictMark, dateTimeText, shortDateTimeText } from "./storagePresentation";
import {
  cleanupSummary, holdingText, matchesSearch, sizeText, verdictLabels, verdictMeaning, verdictOrder, verdictShort,
} from "./storageText";

type GroupBy = "verdict" | "subject";

/**
 * The grid fits the room the workbench leaves under the page's summary, and its rows scroll inside it under a header that
 * stays in view, so the tiles and the search stay on screen while a long list is read.
 */
const FITTED_GRID = "min-w-0 [&_[data-slot=table-container]]:overflow-y-auto [&_th]:sticky [&_th]:top-0 [&_th]:z-10 [&_th]:bg-card [&_th]:shadow-[inset_0_-1px_0_var(--border)]";

/** The room kept under the grid (the editor's bottom padding), and the fewest pixels it keeps on a short window. */
const FIT_BELOW = 24;
const FIT_MIN = 240;

/** The heading the tables no area describes are listed under. */
const UNDESCRIBED = "Not described";

/** One row of the grid: a described area, or a table of the database nobody describes. */
type Row =
  | { kind: "area"; area: StorageArea }
  | { kind: "table"; table: StorageTable };

const rowBytes = (row: Row) => (row.kind === "area" ? row.area.bytes : row.table.bytes);
const rowKey = (row: Row) => (row.kind === "area" ? row.area.id : `table:${row.table.schema}.${row.table.name}`);

function groupOf(row: Row, groupBy: GroupBy): string {
  if (row.kind === "table") {
    return UNDESCRIBED;
  }

  return groupBy === "verdict" ? row.area.verdict : row.area.group;
}

/**
 * The grid's rows in display order. By verdict: safe, optional, then keep, so what may go comes first. By subject: the
 * subjects by the space they take, largest first. Either way the largest entry leads its group, so where the space goes
 * reads from the top down, and the tables no area describes come last.
 */
function orderRows(rows: Row[], groupBy: GroupBy): Row[] {
  const totals = new Map<string, number>();
  for (const row of rows) {
    const group = groupOf(row, groupBy);
    totals.set(group, (totals.get(group) ?? 0) + rowBytes(row));
  }

  const rank = (group: string) => {
    if (group === UNDESCRIBED) {
      return Number.MAX_SAFE_INTEGER;
    }

    return groupBy === "verdict" ? verdictOrder.indexOf(group as StorageVerdict) : -(totals.get(group) ?? 0);
  };

  return [...rows].sort((a, b) => {
    const ga = groupOf(a, groupBy);
    const gb = groupOf(b, groupBy);
    if (ga !== gb) {
      return rank(ga) - rank(gb) || ga.localeCompare(gb);
    }

    return rowBytes(b) - rowBytes(a);
  });
}

function GroupHeader({ group, groupBy, rows }: { group: string; groupBy: GroupBy; rows: Row[] }) {
  const bytes = rows.reduce((sum, row) => sum + rowBytes(row), 0);
  const verdict = groupBy === "verdict" && group !== UNDESCRIBED ? (group as StorageVerdict) : null;
  const owners = [...new Set(rows.flatMap((row) => (row.kind === "area" ? [row.area.owner] : [])))];
  const count = `${rows.length} ${group === UNDESCRIBED ? (rows.length === 1 ? "table" : "tables") : rows.length === 1 ? "area" : "areas"}`;
  return (
    <div className="flex min-w-0 flex-1 items-center gap-2.5" data-testid={`maintenance-group-${group}`}>
      {verdict !== null && <VerdictGlyph verdict={verdict} />}
      <span className="shrink-0 text-[13px] font-medium">{verdict === null ? group : verdictLabels[verdict]}</span>
      <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">{sizeText(bytes)}</span>
      <span className="truncate text-[11px] text-muted-foreground">
        {count}
        {verdict !== null
          ? ` · ${verdictShort[verdict]}`
          : group === UNDESCRIBED ? " · in the database, but no area accounts for them" : owners.length > 0 && ` · ${owners.join(", ")}`}
      </span>
    </div>
  );
}

/** A share of the whole as people read it: "42%", "0.3%", "under 0.1%". */
function shareText(bytes: number, total: number): string {
  if (total <= 0 || bytes <= 0) {
    return "nothing";
  }

  const percent = (bytes / total) * 100;
  if (percent < 0.1) {
    return "under 0.1%";
  }

  return `${percent < 10 ? percent.toFixed(1) : Math.round(percent)}%`;
}

/**
 * An entry's size, with a hairline bar of its share of the largest entry of its group, so the bars compare what sits side by
 * side (a group of kilobytes is not drawn as specks next to gigabytes elsewhere); its share of everything stored is on hover.
 */
function SizeCell({ bytes, groupLargest, stored }: { bytes: number; groupLargest: number; stored: number }) {
  const share = groupLargest <= 0 ? 0 : Math.max(bytes > 0 ? 3 : 0, Math.round((bytes / groupLargest) * 100));
  return (
    <RichTooltip title="Share of what is stored" body={`${sizeText(bytes)} is ${shareText(bytes, stored)} of the ${sizeText(stored)} stored.`}>
      <div className="ml-auto flex w-[96px] flex-col items-end gap-1">
        <span className={cn("font-mono text-xs tabular-nums", bytes === 0 && "text-muted-foreground")}>{sizeText(bytes)}</span>
        <span className="h-1 w-full overflow-hidden rounded-full bg-muted" aria-hidden>
          <span className="block h-full rounded-full bg-primary/60" style={{ width: `${share}%` }} />
        </span>
      </div>
    </RichTooltip>
  );
}

function CleanupCell({ area }: { area: StorageArea }) {
  const { headline, detail, grows } = cleanupSummary(area);
  const cell = (
    <div className="flex min-w-0 flex-col" style={{ maxWidth: 232 }}>
      <span className="truncate text-[13px]">{headline}</span>
      {detail !== null && (
        <span className="flex items-center gap-1 truncate text-[11px] text-muted-foreground">
          {grows && <TrendingUp className="size-3 shrink-0 text-warning" aria-hidden />}
          {detail}
        </span>
      )}
    </div>
  );
  return area.lifecycle === null ? cell : <RichTooltip title="How it leaves the database" body={area.lifecycle}>{cell}</RichTooltip>;
}

function LastCleanupCell({ area }: { area: StorageArea }) {
  if (area.running) {
    return (
      <span className="flex items-center gap-1.5 text-xs">
        <Loader2 className="size-3.5 animate-spin text-primary" aria-hidden />
        Cleaning up
      </span>
    );
  }

  const last = area.lastCleanup;
  if (last === null) {
    return null;
  }

  return (
    <RichTooltip
      title={last.trigger === "automatic" ? "Automatic cleanup" : `Cleaned up by ${last.actor ?? "an admin"}`}
      body={`${dateTimeText(last.startedUtc)}: ${last.removed.toLocaleString()} removed${last.error === null ? "." : `, then it stopped. ${last.error}`}`}
    >
      <div className="flex flex-col">
        <span className="font-mono text-xs tabular-nums">{shortDateTimeText(last.completedUtc ?? last.startedUtc)}</span>
        <span className={cn("text-[11px] tabular-nums", last.error === null ? "text-muted-foreground" : "text-destructive")}>
          {last.error === null ? `${last.removed.toLocaleString()} removed` : "did not finish"}
        </span>
      </div>
    </RichTooltip>
  );
}

/** Each verdict's bytes and areas, and how many areas of it have a limit set. */
function totalsOf(report: StorageReport) {
  const totals = Object.fromEntries(verdictOrder.map((v) => [v, { bytes: 0, areas: 0, limited: 0 }])) as
    Record<StorageVerdict, { bytes: number; areas: number; limited: number }>;
  for (const area of report.areas) {
    const total = totals[area.verdict];
    total.bytes += area.bytes;
    total.areas += 1;
    total.limited += area.retention?.days == null ? 0 : 1;
  }

  return totals;
}

function intervalText(seconds: number): string {
  if (seconds % 3600 === 0) {
    const hours = seconds / 3600;
    return hours === 1 ? "every hour" : `every ${hours} hours`;
  }

  return seconds % 60 === 0 ? `every ${seconds / 60} minutes` : `every ${seconds} seconds`;
}

/**
 * Settings > Maintenance: what the database holds and what may be removed, for the person who runs the system. Every table
 * of the catalog's database is accounted for in areas a person recognises (SQLFlow's own and each module's), and each area
 * carries one verdict: safe to remove (nothing anyone needs is lost), optional (removable at a cost the area states) or
 * keep. The page answers the question in one sentence first, then its tiles filter by verdict, and the grid lists every
 * area, by verdict or by subject, the largest first. An area opens in a side sheet that explains it and, where it takes a
 * limit, previews and runs its cleanup. Nothing is removed by default: an admin sets how long an area's data stays, and the
 * automatic cleanup applies it.
 */
export default function MaintenancePage() {
  const { hasScope } = useAuth();
  const admin = hasScope("admin");
  const queryClient = useQueryClient();
  const [filter, setFilter] = useLocalStorageState<string>("maintenance.verdict", "");
  const [groupBy, setGroupBy] = useLocalStorageState<GroupBy>("maintenance.groupBy", "verdict");
  const [search, setSearch] = useState("");
  const [openId, setOpenId] = useState<string | null>(null);
  // The cleanup panel, opened from the page (every area safe to remove ticked) or for one area (that area alone).
  const [cleanup, setCleanup] = useState<{ open: boolean; openedFor: string | null }>({ open: false, openedFor: null });
  const openCleanup = useCallback((openedFor: string | null) => {
    setOpenId(null);
    setCleanup({ open: true, openedFor });
  }, []);
  const grid = useRef<HTMLDivElement | null>(null);
  useScrollFit(grid, FIT_BELOW, FIT_MIN, tableContainer);

  const query = useQuery({
    queryKey: STORAGE_KEY,
    queryFn: () => maintenanceApi.storage(),
    // While a cleanup runs the page follows it closely; otherwise the sizes are measured again every minute.
    refetchInterval: (q) => (q.state.data?.areas.some((a) => a.running) === true ? 3_000 : pollingInterval(60_000)()),
  });
  const report = query.data;
  const verdictFilter = verdictOrder.find((v) => v === filter) ?? null;
  const grouping = groupBy === "subject" ? "subject" : "verdict";

  const totals = useMemo(() => (report === undefined ? null : totalsOf(report)), [report]);

  const rows = useMemo<Row[] | undefined>(() => {
    if (report === undefined) {
      return undefined;
    }

    const areas: Row[] = report.areas
      .filter((area) => (verdictFilter === null || area.verdict === verdictFilter) && matchesSearch(area, search))
      .map((area) => ({ kind: "area", area }));
    const needle = search.trim().toLowerCase();
    const tables: Row[] = verdictFilter !== null
      ? []
      : report.undescribed
        .filter((t) => needle === "" || `${t.schema}.${t.name}`.toLowerCase().includes(needle))
        .map((table) => ({ kind: "table", table }));
    return orderRows([...areas, ...tables], grouping);
  }, [report, verdictFilter, search, grouping]);

  // The largest entry of each group, which its group's bars are drawn against.
  const groupLargest = useMemo(() => {
    const largest = new Map<string, number>();
    for (const row of rows ?? []) {
      const group = groupOf(row, grouping);
      largest.set(group, Math.max(largest.get(group) ?? 0, rowBytes(row)));
    }

    return largest;
  }, [rows, grouping]);
  const storedBytes = report === undefined
    ? 0
    : report.areas.reduce((sum, a) => sum + a.bytes, 0) + report.undescribed.reduce((sum, t) => sum + t.bytes, 0);
  const openArea = report?.areas.find((a) => a.id === openId) ?? null;

  const columns = useMemo<Column<Row>[]>(() => {
    const list: Column<Row>[] = [
      {
        id: "area",
        header: "Area",
        fill: true,
        floor: 220,
        render: (row) => row.kind === "area"
          ? (
            <div className="flex min-w-0 flex-col">
              <span className="truncate text-[13px]">{row.area.title}</span>
              <span className="truncate text-[11px] text-muted-foreground">
                {grouping === "verdict" ? `${row.area.group} · ${row.area.owner}` : row.area.owner}
                <span className="font-mono"> · {holdingText(row.area)}</span>
              </span>
            </div>
          )
          : (
            <div className="flex min-w-0 flex-col">
              <span className="truncate font-mono text-xs">{row.table.schema}.{row.table.name}</span>
              <span className="truncate text-[11px] text-muted-foreground">No area describes this table</span>
            </div>
          ),
      },
    ];
    if (grouping === "subject") {
      list.push({
        id: "verdict",
        header: "Verdict",
        width: 136,
        render: (row) => row.kind === "area"
          ? (
            <RichTooltip title={verdictLabels[row.area.verdict]} body={verdictMeaning[row.area.verdict]}>
              <span><VerdictMark verdict={row.area.verdict} /></span>
            </RichTooltip>
          )
          : null,
      });
    }

    list.push(
      {
        id: "size",
        header: "Size",
        align: "right",
        width: 116,
        render: (row) => <SizeCell bytes={rowBytes(row)} groupLargest={groupLargest.get(groupOf(row, grouping)) ?? 0} stored={storedBytes} />,
      },
      {
        id: "rows",
        header: "Rows",
        align: "right",
        width: 100,
        render: (row) => {
          const value = row.kind === "area" ? row.area.rows : row.table.rows;
          return <span className={cn("font-mono text-xs tabular-nums", value === 0 && "text-muted-foreground")}>{value.toLocaleString()}</span>;
        },
      },
      {
        id: "cleanup",
        header: "How it leaves",
        width: 244,
        render: (row) => (row.kind === "area" ? <CleanupCell area={row.area} /> : <span className="text-xs text-muted-foreground">Not managed here</span>),
      },
      {
        id: "last",
        header: "Last cleanup",
        width: 124,
        render: (row) => (row.kind === "area" ? <LastCleanupCell area={row.area} /> : null),
      },
      {
        id: "act",
        header: "",
        width: 40,
        align: "center",
        render: (row) => row.kind === "area" && row.area.retention !== null
          ? (
            <IconAction
              label={`Clean up ${row.area.title.toLowerCase()}`}
              icon={<BrushCleaning />}
              onClick={(event) => { event.stopPropagation(); openCleanup(row.area.id); }}
              data-testid={`maintenance-row-cleanup-${row.area.id}`}
            />
          )
          : null,
      },
    );
    return list;
  }, [groupLargest, grouping, storedBytes, openCleanup]);

  const tableGrouping = useMemo<TableGrouping<Row>>(() => ({
    levels: [{ key: (row) => groupOf(row, grouping), renderHeader: (group) => <GroupHeader group={groupOf(group[0], grouping)} groupBy={grouping} rows={group} /> }],
  }), [grouping]);

  // What is safe to remove but goes by a rule of its own, not by Clean up: the summary says so, so its size is not read as
  // something a person has to remove.
  const selfCleaningBytes = report?.areas.filter((a) => a.verdict === "safe" && a.retention === null).reduce((sum, a) => sum + a.bytes, 0) ?? 0;
  const limited = report?.areas.filter((a) => a.retention?.days != null).length ?? 0;
  const error = query.isError
    ? isApiError(query.error) ? query.error : new ApiError(0, "Could not measure the database", String(query.error), null)
    : null;

  return (
    <Page data-testid="maintenance-page">
      <PageHeader
        title="Maintenance"
        subtitle="What the database holds, what is kept and why, and what may be removed."
        actions={(
          <div className="flex items-center gap-2">
            <Button
              size="sm"
              variant="outline"
              onClick={() => void queryClient.invalidateQueries({ queryKey: STORAGE_KEY })}
              disabled={query.isFetching}
              data-testid="maintenance-refresh"
            >
              <RefreshCw className={cn("size-3.5", query.isFetching && "animate-spin")} />
              Measure again
            </Button>
            <Button size="sm" onClick={() => openCleanup(null)} disabled={report === undefined} data-testid="maintenance-cleanup">
              <BrushCleaning className="size-3.5" />
              Clean up
            </Button>
          </div>
        )}
      />

      {error !== null ? (
        <CorrelationError error={error} data-testid="maintenance-error" />
      ) : report === undefined || totals === null ? (
        <div className="flex flex-col gap-3">
          <Skeleton className="h-10 rounded-lg" />
          <Skeleton className="h-[84px] rounded-lg" />
          <Skeleton className="h-96 rounded-lg" />
        </div>
      ) : (
        <div className="flex min-h-0 flex-col gap-3">
          <div className="flex flex-col gap-0.5" data-testid="maintenance-summary">
            <p className="text-[13.5px] leading-snug">
              <span className="font-mono">{sizeText(storedBytes)}</span> stored in <span className="font-mono">{report.database}</span>.{" "}
              {totals.safe.bytes + totals.optional.bytes === 0
                ? "Nothing in it may be removed; all of it is kept."
                : (
                  <>
                    <span className="font-mono">{sizeText(totals.safe.bytes + totals.optional.bytes)}</span> of it may be removed:{" "}
                    <span className="font-mono">{sizeText(totals.safe.bytes)}</span> at no loss
                    {selfCleaningBytes > 0 && <> (<span className="font-mono">{sizeText(selfCleaningBytes)}</span> of it cleans itself)</>} and{" "}
                    <span className="font-mono">{sizeText(totals.optional.bytes)}</span> at a cost you weigh. The rest is kept.
                  </>
                )}
            </p>
            <div className="flex flex-wrap items-center gap-x-1.5 text-xs text-muted-foreground" data-testid="maintenance-cleanup-line">
              <span>
                {report.automaticCleanup
                  ? `Automatic cleanup runs ${intervalText(report.cleanupIntervalSeconds)} and applies the limits set here; ${limited === 0 ? "no area has one yet, so it removes nothing." : `${limited} ${limited === 1 ? "area has" : "areas have"} one.`}`
                  : "Automatic cleanup is off (ControlPlane:Maintenance:Enabled); data goes only when an admin cleans up an area."}
                {!admin && " Only an admin can set a limit or clean up."}
              </span>
              <ExplainMark
                title="About these numbers"
                text={`Sizes come from SQL Server's own metadata and include each table's indexes; they were measured ${dateTimeText(report.measuredUtc)}. The database's data files take ${sizeText(report.dataFileBytes)} on disk.\n\nA cleanup frees space inside the database, which new rows then reuse. It does not shrink the files on disk; that stays a database administrator's decision.`}
                testId="maintenance-cleanup-explain"
              />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <KpiCard
              label="Stored"
              value={sizeText(storedBytes)}
              caption={`${report.areas.length} areas${report.undescribed.length === 0 ? "" : `, ${report.undescribed.length} other ${report.undescribed.length === 1 ? "table" : "tables"}`}`}
              onClick={() => setFilter("")}
              selected={verdictFilter === null}
              testId="maintenance-tile-all"
            />
            {verdictOrder.map((verdict) => (
              <KpiCard
                key={verdict}
                label={verdictLabels[verdict]}
                value={sizeText(totals[verdict].bytes)}
                caption={`${totals[verdict].areas} ${totals[verdict].areas === 1 ? "area" : "areas"}: ${verdictShort[verdict]}`}
                onClick={() => setFilter(verdictFilter === verdict ? "" : verdict)}
                selected={verdictFilter === verdict}
                testId={`maintenance-tile-${verdict}`}
              />
            ))}
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <SearchInput
              value={search}
              onChange={setSearch}
              placeholder="Search areas and tables"
              label="Search areas and tables"
              className="w-full max-w-xs"
              testId="maintenance-search"
            />
            <ToggleGroup
              type="single"
              value={grouping}
              onValueChange={(value) => { if (value === "verdict" || value === "subject") { setGroupBy(value); } }}
              variant="outline"
              size="sm"
              data-testid="maintenance-group-toggle"
            >
              <ToggleGroupItem value="verdict" className="px-2.5 text-xs">By verdict</ToggleGroupItem>
              <ToggleGroupItem value="subject" className="px-2.5 text-xs">By subject</ToggleGroupItem>
            </ToggleGroup>
          </div>

          <div ref={grid} className={FITTED_GRID}>
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={rowKey}
              grouping={tableGrouping}
              onRowClick={(row) => { if (row.kind === "area") { setOpenId(row.area.id); } }}
              rowClickable={(row) => row.kind === "area"}
              emptyMessage={search.trim() !== ""
                ? "No area or table matches the search."
                : verdictFilter === null ? "Nothing is stored yet." : `No area is ${verdictLabels[verdictFilter].toLowerCase()}.`}
              minWidth={780}
              data-testid="maintenance-areas"
            />
          </div>
        </div>
      )}

      <StorageAreaSheet
        area={openArea}
        automaticCleanup={report?.automaticCleanup ?? false}
        cleanupIntervalSeconds={report?.cleanupIntervalSeconds ?? 3600}
        admin={admin}
        onClose={() => setOpenId(null)}
        onCleanUp={(areaId) => openCleanup(areaId)}
      />

      <CleanupSheet
        open={cleanup.open}
        openedFor={cleanup.openedFor}
        report={report}
        admin={admin}
        onClose={() => setCleanup({ open: false, openedFor: null })}
      />
    </Page>
  );
}
