import { useCallback, useRef } from "react";
import { format, formatDistanceToNow } from "date-fns";
import { FileCode2 } from "lucide-react";
import { Card } from "@/components/ui/card";
import { isApiError } from "../../api/client";
import type { AiContextSummary } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { RichTooltip } from "../../components/RichTooltip";
import { useClipped } from "../../components/useClipped";
import { tableContainer, useScrollFit } from "../../hooks/useScrollFit";
import { useOwnedPanel } from "../../layout/workbench/useOwnedPanel";
import { parseUtc } from "../../lib/time";
import { AiContextDetailPanel } from "./AiContextDetail";
import { AiContextStatusChip, FileRef } from "./parts";
import { errorText, FIT_BELOW, FIT_MIN, FITTED_GRID, shortVersion, useAiContexts } from "./shared";

/** The bottom panel content ids this surface owns. */
const PANEL = "ai-context:";

/**
 * A context's name, capped rather than filling its column: the name is what a row is known by, so it keeps its room while
 * the approval and the file give way. What the context covers is on hover, with the whole name when it is clipped.
 */
function ContextName({ row }: { row: AiContextSummary }) {
  const [ref, clipped] = useClipped(row.name);
  const name = (
    <span ref={ref} className="block max-w-[140px] truncate font-mono text-[12px] font-medium @5xl/table:max-w-[220px]" data-testid="ai-context-name">
      {row.name}
    </span>
  );
  if (row.description == null && !clipped) {
    return name;
  }

  const body = [clipped ? row.name : null, row.description].filter((part): part is string => part != null).join("\n\n");
  return <RichTooltip body={body} title={clipped ? "Context" : row.name}>{name}</RichTooltip>;
}

/**
 * Who approved a context and how long ago, the whole of it on hover: the approved version, the name, and the time to the
 * second. Capped so the file keeps the room the grid has left; in a narrow grid the name keeps what there is and the
 * time is left to the hover.
 */
function ApprovedCell({ row }: { row: AiContextSummary }) {
  if (row.approvedUtc == null) {
    return <span className="text-muted-foreground">never</span>;
  }

  const when = parseUtc(row.approvedUtc);
  const relative = formatDistanceToNow(when, { addSuffix: true });
  const who = row.approvedBy ?? "unknown";
  return (
    <RichTooltip
      body={`Version ${shortVersion(row.approvedVersion) ?? "?"} approved by ${who}, ${relative} (${format(when, "MMM d yyyy, HH:mm:ss")}).`}
      title="Approved"
    >
      <span className="flex min-w-0 max-w-[120px] items-baseline gap-1.5 text-[12px] @3xl/table:max-w-[240px]" data-testid="ai-context-approved">
        <span className="min-w-0 truncate">{who}</span>
        <span className="shrink-0 whitespace-nowrap text-muted-foreground @max-3xl/table:hidden">{relative}</span>
      </span>
    </RichTooltip>
  );
}

const columns: Column<AiContextSummary>[] = [
  {
    id: "name",
    header: "Context",
    render: (row) => <ContextName row={row} />,
  },
  { id: "status", header: "Status", render: (row) => <AiContextStatusChip status={row.status} collapse /> },
  {
    id: "version",
    header: "Proposed version",
    render: (row) => row.currentVersion == null
      ? <span className="text-muted-foreground">none</span>
      : <span className="font-mono text-[12px]">{shortVersion(row.currentVersion)}</span>,
  },
  {
    id: "approved",
    header: "Approved",
    render: (row) => <ApprovedCell row={row} />,
  },
  {
    id: "tables",
    header: "Tables",
    align: "right",
    render: (row) => (
      <RichTooltip
        body={`${row.servedTableCount} served now, of ${row.tableCount} the file proposes.`}
        title="Tables"
      >
        <span className="font-mono text-[12px] tabular-nums" data-testid="ai-context-tables-count">
          <span className={row.servedTableCount === 0 ? "text-muted-foreground" : undefined}>{row.servedTableCount}</span>
          <span className="text-muted-foreground">{` / ${row.tableCount}`}</span>
        </span>
      </RichTooltip>
    ),
  },
  {
    id: "file",
    header: "File",
    fill: true,
    floor: 96,
    render: (row) => <FileRef repoName={row.repoName} path={row.path} />,
  },
];

/**
 * The AI contexts files propose: one row per context with where it stands, the version its file proposes, who approved
 * what, and how much of it is served. The grid fits the page and scrolls in place; a row opens the context in the bottom
 * panel, where an admin reviews the proposed version and approves or revokes it.
 */
export function AiContextsPanel() {
  const contexts = useAiContexts();
  const { ownedId, show } = useOwnedPanel(PANEL);
  const grid = useRef<HTMLDivElement>(null);
  useScrollFit(grid, FIT_BELOW, FIT_MIN, tableContainer);

  const open = useCallback(
    (row: AiContextSummary) => show(row.name, `AI context · ${row.name}`, <AiContextDetailPanel key={row.name} name={row.name} />),
    [show],
  );

  if (contexts.isError) {
    return isApiError(contexts.error)
      ? <CorrelationError error={contexts.error} />
      : <p className="text-[13px] text-destructive">{errorText(contexts.error)}</p>;
  }

  return (
    <div ref={grid} className={FITTED_GRID} data-testid="ai-context-grid">
      {contexts.data?.contexts.length === 0 ? (
        <Card className="rounded-lg p-0">
          <EmptyState
            icon={<FileCode2 />}
            title="No AI context yet"
            description={(
              <>
                Add an <span className="font-mono">ai-context.yaml</span> (or a <span className="font-mono">*.ai-context.yaml</span>)
                {" "}with a root <span className="font-mono">aiContext: &lt;name&gt;</span> to a synced repository. List a table under
                {" "}<span className="font-mono">tables:</span> to open all its columns, and list columns under
                {" "}<span className="font-mono">deny:</span> to keep them closed. Once the repository has synced, the context
                appears here, and the assistant uses it after an admin approves it.
              </>
            )}
            data-testid="ai-context-empty"
          />
        </Card>
      ) : (
        <DataTable<AiContextSummary>
          columns={columns}
          rows={contexts.data?.contexts}
          rowKey={(row) => row.name}
          onRowClick={open}
          rowSx={(row) => (row.name === ownedId ? { backgroundColor: "var(--accent)" } : undefined)}
          emptyMessage="No AI context yet."
          data-testid="ai-context-table"
        />
      )}
    </div>
  );
}
