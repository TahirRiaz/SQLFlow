import { useState } from "react";
import { ChevronDown, ChevronUp, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { UniqueKeyCandidate, UniqueKeyReport } from "../../api/types";
import { CopyButton } from "../../components/CopyButton";
import { DataTable, type Column } from "../../components/DataTable";
import { Mono } from "../../components/Mono";

/** One unique-key candidate line: the verdict and how trustworthy it is. */
function candidateLabel(candidate: UniqueKeyCandidate): string {
  if (candidate.isUnique) {
    if (candidate.declared) {
      return "UNIQUE (declared by the database)";
    }

    return candidate.verified ? "UNIQUE (verified against the whole table)" : "unique on the sample (unverified)";
  }

  const approx = candidate.estimated ? "~" : "";
  return `not unique (${approx}${candidate.duplicates.toLocaleString()} duplicate row(s)${candidate.nulls > 0 ? `, ${approx}${candidate.nulls.toLocaleString()} null row(s)` : ""}${candidate.estimated ? ", sample estimate" : ""})`;
}

/** The flow-file fragment for one detected key, ready to paste into a flow's YAML. */
function keyColumnsYaml(columns: string[]): string {
  return `keyColumns:\n${columns.map((c) => `  - ${c}`).join("\n")}\n`;
}

/** Selectivity formatted for display: 1 collapses to "1", everything else keeps four decimals. */
function formatSelectivity(value: number): string {
  return value === 1 ? "1" : value.toFixed(4);
}

const statisticsColumns: Column<UniqueKeyReport["columns"][number]>[] = [
  { id: "column", header: "Column", render: (row) => <Mono>{row.column}</Mono> },
  {
    id: "distinct",
    header: "Distinct",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.distinct.toLocaleString()}</span>,
  },
  {
    id: "nulls",
    header: "Nulls",
    align: "right",
    render: (row) => <span className="font-mono tabular-nums">{row.nulls.toLocaleString()}</span>,
  },
  {
    id: "selectivity",
    header: "Selectivity",
    align: "right",
    render: (row) => <Mono className="tabular-nums">{formatSelectivity(row.selectivity)}</Mono>,
  },
];

interface UniqueKeyReportViewProps {
  report: UniqueKeyReport;
  /** Compact spacing for the inspector sheet; the dedicated page uses the roomier default. */
  dense?: boolean;
  "data-testid"?: string;
}

/**
 * The one rendering of a unique-key detection report, shared by the inspector sheet and the dedicated
 * detection page: the scan scope, the ranked candidates (with selectivity and per-candidate keyColumns copy),
 * the per-column statistics (sorted most-identifying first, collapsed by default), the columns excluded before
 * the search with their reasons, and the report note. Purely presentational; running the detection is the
 * caller's concern.
 */
export function UniqueKeyReportView({ report, dense = false, "data-testid": testId }: UniqueKeyReportViewProps) {
  const [showStatistics, setShowStatistics] = useState(false);
  const metadataAnswer = report.scannedRows === 0 && report.candidates.some((c) => c.declared);

  const scope = metadataAnswer
    ? `${report.totalRows.toLocaleString()} row(s), answered from database metadata without reading the data`
    : report.sampled
      ? `${report.totalRows.toLocaleString()} row(s), profiled on a random sample of ${report.scannedRows.toLocaleString()}`
      : `${report.totalRows.toLocaleString()} row(s), full scan`;

  const statistics = [...report.columns].sort((a, b) => b.selectivity - a.selectivity);

  return (
    <div className={cn("flex flex-col", dense ? "gap-2" : "gap-3")} data-testid={testId ?? "unique-key-report"}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-[13px] text-muted-foreground" data-testid="report-scope">{scope}</span>
        <CopyButton
          label="Copy report JSON"
          text={JSON.stringify(report, null, 2)}
          testId="copy-report-json"
        />
      </div>

      {report.candidates.length === 0 && (
        <Alert className="border-warning/50 text-warning" data-testid="report-no-candidates">
          <TriangleAlert />
          <AlertDescription className="text-warning/90">No candidate key was found.</AlertDescription>
        </Alert>
      )}

      {report.candidates.map((candidate, rank) => (
        <div
          key={candidate.columns.join("|")}
          className={cn(
            "flex flex-col gap-1 rounded-lg border px-3 py-2",
            candidate.isUnique ? "border-success/40 bg-success/10" : "border-info/40 bg-info/10",
          )}
          data-testid="report-candidate"
        >
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[13px] font-semibold">{rank + 1}.</span>
            <Mono className="font-semibold">[{candidate.columns.join(", ")}]</Mono>
            {candidate.declared && (
              <Badge variant="outline" className="border-primary/50 text-primary">declared</Badge>
            )}
            {candidate.isUnique && !candidate.declared && candidate.verified && (
              <Badge variant="outline" className="border-success/50 text-success">verified</Badge>
            )}
            {candidate.estimated && <Badge variant="outline">sample estimate</Badge>}
            <Badge variant="outline" className="font-mono tabular-nums">
              selectivity {candidate.estimated ? "~" : ""}{formatSelectivity(candidate.selectivity)}
            </Badge>
          </div>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="text-[13px] text-foreground">{candidateLabel(candidate)}</span>
            {candidate.isUnique && (
              <CopyButton
                label="Copy keyColumns YAML"
                text={keyColumnsYaml(candidate.columns)}
                testId="copy-key-columns"
              />
            )}
          </div>
        </div>
      ))}

      {statistics.length > 0 && (
        <>
          <Button
            variant="ghost"
            size="sm"
            className="self-start"
            onClick={() => setShowStatistics((current) => !current)}
            data-testid="toggle-column-statistics"
          >
            {showStatistics ? <ChevronUp /> : <ChevronDown />}
            Column statistics ({statistics.length})
          </Button>
          {showStatistics && (
            <DataTable
              columns={statisticsColumns}
              rows={statistics}
              rowKey={(row) => row.column}
              emptyMessage="No columns were measured."
              data-testid="column-statistics-table"
            />
          )}
        </>
      )}

      {report.excludedColumns.length > 0 && (
        <span className="text-xs text-muted-foreground" data-testid="report-excluded">
          Excluded from the search: {report.excludedColumns.map((e) => `${e.column} (${e.reason})`).join(", ")}
        </span>
      )}

      {report.note !== null && (
        <span className="text-xs text-muted-foreground" data-testid="report-note">{report.note}</span>
      )}
    </div>
  );
}
