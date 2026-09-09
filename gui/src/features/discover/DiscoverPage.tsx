import { useMemo, useState, type ReactNode } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { ChevronDown, Download, Info, Loader2, SlidersHorizontal, Telescope, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { CodeView } from "../../components/CodeView";
import { DataTable, type Column } from "../../components/DataTable";
import { CorrelationError } from "../../components/CorrelationError";
import { isApiError } from "../../api/client";
import { sourceApi } from "../../api/endpoints";
import type { DiscoveredColumn, DiscoveredPath, SourceDiscoverResult } from "../../api/types";
import { usePanel } from "../../layout/workbench/PanelContext";
import { ActivityTracePanel } from "../activity/ActivityTracePanel";

/** The activity-trace kind a source discovery writes under (mirrors ActivityKinds.SourceDiscover server-side). */
const DISCOVER_KIND = "source-discover";

/** The last path segment of a location, for a compact panel title (falls back to the whole location). */
function locationLabel(location: string): string {
  const cleaned = location.split(/[?#]/)[0].replace(/[/\\]+$/, "");
  const segment = cleaned.split(/[/\\]/).pop();
  return segment && segment.length > 0 ? segment : location;
}

// Radix Select items cannot carry an empty value, so "auto" stands in for "let the server detect".
const AUTO_FORMAT = "auto";

const FORMATS = [
  { value: AUTO_FORMAT, label: "Auto (detect from content)" },
  { value: "json", label: "JSON" },
  { value: "ndjson", label: "NDJSON" },
  { value: "jsonl", label: "JSONL" },
  { value: "xml", label: "XML" },
  { value: "csv", label: "CSV" },
  { value: "tsv", label: "TSV (tab)" },
  { value: "xlsx", label: "Excel (xls/xlsx)" },
  { value: "parquet", label: "Parquet" },
];

// Landing column types, leanest first. varchar is single-byte (SQLFlow's default); nvarchar doubles storage and is
// only worth it when the data is known to exceed Latin-1. Typing narrows in a later transformation stage.
const COLUMN_TYPES = ["varchar(255)", "varchar(4000)", "varchar(max)", "nvarchar(255)", "nvarchar(4000)", "nvarchar(max)"];

/** Text color per detection confidence; the label always carries the meaning too (never color alone). */
const CONFIDENCE_CLASS: Record<string, string> = {
  explicit: "text-muted-foreground",
  extension: "text-info",
  high: "text-success",
  medium: "text-warning",
  low: "text-warning",
};

/** Parses a text number field into a positive integer, or undefined when blank/invalid (falls back to the default). */
function parseCount(raw: string): number | undefined {
  const trimmed = raw.trim();
  if (trimmed === "") {
    return undefined;
  }
  const value = Number(trimmed);
  return Number.isFinite(value) && value >= 0 ? Math.floor(value) : undefined;
}

/** A download filename derived from the source location's last segment (folder or file), always *.flow.yaml. */
function yamlFileName(location: string): string {
  const cleaned = location.split(/[?#]/)[0].replace(/[/\\]+$/, "");
  const segment = cleaned.split(/[/\\]/).pop() ?? "source";
  const base = segment.replace(/\.[^.]+$/, "").replace(/[^a-zA-Z0-9]+/g, "_").replace(/^_+|_+$/g, "");
  return `${base || "source"}.flow.yaml`;
}

function presenceLabel(row: DiscoveredPath, recordsScanned: number): string {
  return row.present ? "all" : `${row.recordCount}/${recordsScanned}`;
}

/** One field wrapper: label above the control, optional caption under it (DESIGN.md 7.5). */
function Field({ label, caption, children }: { label: string; caption?: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label>{label}</Label>
      {children}
      {caption !== undefined && <p className="text-xs text-muted-foreground">{caption}</p>}
    </div>
  );
}

export default function DiscoverPage() {
  const [location, setLocation] = useState("");
  const [format, setFormat] = useState(AUTO_FORMAT);
  const [pattern, setPattern] = useState("");
  const [recursive, setRecursive] = useState(true);
  const [rootPath, setRootPath] = useState("");
  const [maxFiles, setMaxFiles] = useState("");
  const [maxRecords, setMaxRecords] = useState("");
  const [maxDepth, setMaxDepth] = useState("");
  const [defaultType, setDefaultType] = useState("varchar(255)");
  const panel = usePanel();
  const [traceNonce, setTraceNonce] = useState(0);

  const discover = useMutation({
    mutationFn: (subject: string): Promise<SourceDiscoverResult> =>
      sourceApi.discover({
        location: location.trim(),
        format: format === AUTO_FORMAT ? null : format,
        pattern: pattern.trim() || null,
        recursive,
        rootPath: rootPath.trim() || null,
        maxFiles: parseCount(maxFiles) ?? null,
        maxRecords: parseCount(maxRecords) ?? null,
        maxDepth: parseCount(maxDepth) ?? null,
        defaultColumnType: defaultType.trim() || null,
        traceSubject: subject,
      }),
    // Discovery outlives the click (a folder scan can sample many files), so it ends with a terminal
    // toast either way (DESIGN.md 8.2); the liveness surface below the form covers the in-between.
    onSuccess: (result) => {
      const shape = result.mode === "flatten"
        ? `${result.recordsScanned} record(s), ${result.paths.length} path(s)`
        : `${result.columns.length} column(s)`;
      toast.success(`Discovery complete: ${result.filesScanned} file(s), ${shape}.`);
    },
    onError: (error) =>
      toast.error(isApiError(error) ? error.detail ?? error.title : error instanceof Error ? error.message : String(error)),
  });

  const result = discover.data;
  const error = discover.error;

  const pathColumns: Column<DiscoveredPath>[] = useMemo(
    () => [
      { id: "path", header: "Path", render: (row) => <span className="font-mono text-[12px]">{row.path}</span> },
      { id: "column", header: "Column", render: (row) => <span className="font-mono text-[12px]">{row.column}</span> },
      { id: "kind", header: "Kind", render: (row) => <Badge variant="outline">{row.kind}</Badge> },
      {
        id: "presence",
        header: "Presence",
        align: "right",
        render: (row) => (
          <span className="font-mono tabular-nums">{result ? presenceLabel(row, result.recordsScanned) : ""}</span>
        ),
      },
    ],
    [result],
  );

  const columnColumns: Column<DiscoveredColumn>[] = useMemo(
    () => [
      { id: "name", header: "Column", render: (row) => <span className="font-mono text-[12px]">{row.name}</span> },
      { id: "type", header: "Type", render: (row) => <Badge variant="outline" className="font-mono text-[11px]">{row.sqlType}</Badge> },
      { id: "nullable", header: "Nullable", align: "right", render: (row) => (row.nullable ? "yes" : "no") },
    ],
    [],
  );

  const canDiscover = location.trim().length > 0 && !discover.isPending;
  const advancedCount = [pattern.trim(), rootPath.trim(), maxFiles.trim(), maxRecords.trim(), maxDepth.trim()]
    .filter(Boolean).length
    + (format !== AUTO_FORMAT ? 1 : 0)
    + (recursive ? 1 : 0)
    + (defaultType !== "varchar(255)" ? 1 : 0);

  const downloadYaml = () => {
    if (!result) {
      return;
    }
    const blob = new Blob([result.generatedYaml], { type: "text/yaml;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = yamlFileName(location);
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  };

  const submit = () => {
    if (!canDiscover) {
      return;
    }

    // The location is the trace subject, so repeat scans of the same source keep a bounded scrollback and a fresh
    // nonce restarts the stream from the top. Open the bottom trace panel first, then run the scan: both address the
    // same (kind, subject), so the panel tails each phase (format, delimiter, schema) and shows where a scan fails.
    // Cap to the server's SubjectKey column bound (256) so the panel keys on the exact value the request traces under.
    const subject = location.trim().slice(0, 256);
    const nextNonce = traceNonce + 1;
    setTraceNonce(nextNonce);
    panel.open({
      id: `discover-trace:${subject}`,
      title: `Discover · ${locationLabel(subject)}`,
      node: <ActivityTracePanel kind={DISCOVER_KIND} subject={subject} nonce={nextNonce} />,
    });
    discover.mutate(subject);
  };

  return (
    <Page data-testid="discover-page">
      <PageHeader
        title="Discover"
        subtitle="Point at any file or folder (JSON, XML, CSV, Excel, Parquet); SQLFlow detects the format, samples it, and generates the ingestion YAML."
      />

      <Card className="gap-0 rounded-lg p-4">
        <div className="flex flex-col gap-3">
          <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
            <Input
              value={location}
              onChange={(e) => setLocation(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  submit();
                }
              }}
              placeholder="abfss://container/path/ (folder or file); the format is detected automatically"
              aria-label="Source location"
              autoFocus
              className="h-8 flex-1 font-mono text-[12px]"
              data-testid="discover-location"
            />
            <Button size="sm" disabled={!canDiscover} onClick={submit} data-testid="discover-submit">
              {discover.isPending ? <Loader2 className="animate-spin" /> : <Telescope />}
              {discover.isPending ? "Discovering..." : "Discover"}
            </Button>
          </div>

          <Collapsible>
            <CollapsibleTrigger
              className="group flex items-center gap-1.5 text-[13px] text-muted-foreground hover:text-foreground"
              data-testid="discover-advanced-toggle"
            >
              <SlidersHorizontal className="size-4 shrink-0" />
              Advanced{advancedCount > 0 ? ` (${advancedCount} set)` : ""}
              <ChevronDown className="size-4 shrink-0 transition-transform duration-120 group-data-[state=open]:rotate-180" />
            </CollapsibleTrigger>
            <CollapsibleContent>
              <div className="flex flex-col gap-4 pt-3">
                <div className="grid gap-4 sm:grid-cols-3">
                  <Field label="Format" caption="Auto detects from the file content.">
                    <Select value={format} onValueChange={setFormat}>
                      <SelectTrigger size="sm" className="h-8 w-full" data-testid="discover-format">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {FORMATS.map((f) => (
                          <SelectItem key={f.value} value={f.value}>{f.label}</SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </Field>
                  <Field label="Folder pattern" caption="Used only for a folder location.">
                    <Input
                      value={pattern}
                      onChange={(e) => setPattern(e.target.value)}
                      placeholder="auto per format"
                      className="h-8 font-mono text-[12px]"
                      data-testid="discover-pattern"
                    />
                  </Field>
                  <Label className="flex items-center gap-2 pt-6 text-[13px] font-normal">
                    <Switch checked={recursive} onCheckedChange={setRecursive} data-testid="discover-recursive" />
                    Recurse sub-folders
                  </Label>
                </div>

                <div className="grid gap-4 sm:grid-cols-3 lg:grid-cols-6">
                  <Field label="Default column type" caption="Landing type; narrows later.">
                    <Select value={defaultType} onValueChange={setDefaultType}>
                      <SelectTrigger size="sm" className="h-8 w-full" data-testid="discover-default-type">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {COLUMN_TYPES.map((t) => (
                          <SelectItem key={t} value={t}>{t}</SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </Field>
                  <div className="sm:col-span-2 lg:col-span-2">
                    <Field label="Record grain override" caption="rootPath / rowXPath (JSON/XML, auto if blank).">
                      <Input
                        value={rootPath}
                        onChange={(e) => setRootPath(e.target.value)}
                        className="h-8 font-mono text-[12px]"
                        data-testid="discover-rootpath"
                      />
                    </Field>
                  </div>
                  <Field label="Max files">
                    <Input
                      value={maxFiles}
                      onChange={(e) => setMaxFiles(e.target.value)}
                      placeholder="100"
                      inputMode="numeric"
                      className="h-8"
                      data-testid="discover-maxfiles"
                    />
                  </Field>
                  <Field label="Max records">
                    <Input
                      value={maxRecords}
                      onChange={(e) => setMaxRecords(e.target.value)}
                      placeholder="0 = all"
                      inputMode="numeric"
                      className="h-8"
                      data-testid="discover-maxrecords"
                    />
                  </Field>
                  <Field label="Max depth">
                    <Input
                      value={maxDepth}
                      onChange={(e) => setMaxDepth(e.target.value)}
                      placeholder="10"
                      inputMode="numeric"
                      className="h-8"
                      data-testid="discover-maxdepth"
                    />
                  </Field>
                </div>
              </div>
            </CollapsibleContent>
          </Collapsible>

          {discover.isPending && (
            <div className="flex items-center gap-2 rounded-md border border-border bg-muted/50 px-3 py-2 text-[13px] text-muted-foreground">
              <Loader2 className="size-4 shrink-0 animate-spin text-info" />
              Scanning <span className="max-w-md truncate font-mono text-[12px]">{location.trim()}</span>:
              detecting the format and sampling records. Large folders can take a while.
            </div>
          )}

          {error !== null && !discover.isPending && (
            isApiError(error)
              ? <CorrelationError error={error} />
              : <p className="text-[13px] text-destructive">{String(error)}</p>
          )}
        </div>
      </Card>

      {result && (
        <>
          <div className="flex flex-wrap items-center gap-1.5">
            <Badge data-testid="discover-format-chip">{result.sourceType.toUpperCase()}</Badge>
            <Tooltip>
              <TooltipTrigger asChild>
                <Badge
                  variant="outline"
                  className={CONFIDENCE_CLASS[result.detectionConfidence] ?? "text-muted-foreground"}
                  data-testid="discover-confidence-chip"
                >
                  format: {result.detectionConfidence}
                </Badge>
              </TooltipTrigger>
              <TooltipContent className="max-w-md">{result.detectionEvidence.join("; ")}</TooltipContent>
            </Tooltip>
            <Badge variant="outline">{result.filesScanned} file(s)</Badge>
            {result.mode === "flatten" ? (
              <>
                <Badge variant="outline">{result.recordsScanned} record(s)</Badge>
                <Badge variant="outline">{result.paths.length} path(s)</Badge>
                {result.autoDetectedGrain && (
                  <Badge variant="outline" className="text-info">grain: {result.autoDetectedGrain}</Badge>
                )}
                {result.schemaDrift && <Badge variant="outline" className="text-warning">schema drift</Badge>}
              </>
            ) : (
              <Badge variant="outline">{result.columns.length} column(s)</Badge>
            )}
          </div>

          {result.mode === "flatten" && result.schemaDrift && (
            <Alert>
              <TriangleAlert className="text-warning" />
              <AlertTitle>Schema drift in the sample</AlertTitle>
              <AlertDescription>
                Some paths are missing from part of the sample. A path missing from a file becomes NULL for that
                file&apos;s rows; reconcile renamed fields with{" "}
                <code className="font-mono text-[12px]">pathAliases</code>.
              </AlertDescription>
            </Alert>
          )}

          {result.mode === "flatten" ? (
            result.paths.length === 0 ? (
              <Alert>
                <Info />
                <AlertDescription>
                  No records found. Check the location, the folder pattern, and the record grain (rootPath / rowXPath).
                </AlertDescription>
              </Alert>
            ) : (
              <div className="flex flex-col gap-2">
                <h2 className="text-base font-medium">Path structure</h2>
                <DataTable
                  columns={pathColumns}
                  rows={result.paths}
                  rowKey={(row) => row.path}
                  rowSx={(row) => (row.present
                    ? undefined
                    : { backgroundColor: "color-mix(in srgb, var(--warning) 8%, transparent)" })}
                  emptyMessage="No paths discovered."
                  data-testid="discover-paths"
                />
              </div>
            )
          ) : (
            <div className="flex flex-col gap-2">
              <h2 className="text-base font-medium">Columns</h2>
              <p className="text-[13px] text-muted-foreground">
                Schema sampled from a representative file. A tabular source lands string-first; typing happens in a
                later transformation stage.
              </p>
              <DataTable
                columns={columnColumns}
                rows={result.columns}
                rowKey={(row) => row.name}
                emptyMessage="No columns discovered."
                data-testid="discover-columns"
              />
            </div>
          )}

          <div className="flex flex-col gap-2">
            <div className="flex items-center justify-between gap-2">
              <h2 className="text-base font-medium">Ingestion YAML</h2>
              <Button variant="outline" size="sm" onClick={downloadYaml} data-testid="discover-download">
                <Download />
                Download
              </Button>
            </div>
            <p className="text-[13px] text-muted-foreground">
              A runnable ingestion flow. Set <code className="font-mono text-[12px]">target.connection</code>, then
              run it to ingest the source.
            </p>
            <CodeView value={result.generatedYaml} language="yaml" lsp={false} height={420} data-testid="discover-yaml" />
          </div>
        </>
      )}
    </Page>
  );
}
