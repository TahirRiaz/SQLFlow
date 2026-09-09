import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { Bug, Cable, ChevronDown, CircleAlert, CircleCheck, Code, Info, Loader2, RefreshCw, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { get, post, isApiError } from "../../api/client";
import { pipelineApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { ActiveBadge } from "../../components/StatusBadge";

interface Integration {
  id: string;
  name: string;
  batch: string | null;
  relativePath: string;
  active: boolean;
}

interface IntegrationDebugPage {
  iteration: number;
  page: number;
  method: string;
  url: string;
  requestHeaders: Record<string, string>;
  status: number;
  responseHeaders: Record<string, string>;
  contentType: string | null;
  bytes: number;
  recordCount: number;
  durationMs: number;
  bodyPreview: string;
  wouldLandTo: string | null;
}

interface IntegrationDebugResponse {
  success: boolean;
  error: string | null;
  iterations: number;
  pagesFetched: number;
  filesWritten: number;
  skipped: number;
  bytesWritten: number;
  landedBase: string | null;
  watermarkBefore: string | null;
  watermarkAfter: string | null;
  pages: IntegrationDebugPage[];
}

interface IntegrationParam {
  name: string;
  default: string | null;
  required: boolean;
}

interface IntegrationDescribeResponse {
  name: string;
  batch: string | null;
  transport: string;
  hasDateWindow: boolean;
  params: IntegrationParam[];
}

const integrationApi = {
  list: () => get<Integration[]>("/api/v1/integrations"),
  describe: (yaml: string) => post<IntegrationDescribeResponse>("/api/v1/integrations/describe", { yaml }),
  debug: (yaml: string, maxPages: number, params: Record<string, string>) =>
    post<IntegrationDebugResponse>("/api/v1/integrations/debug", {
      yaml,
      maxPages,
      params: Object.keys(params).length > 0 ? params : undefined,
    }),
};

const STARTER_YAML = `flowType: api
name: My_Integration
source:
  transport: http
  baseUrl: https://jsonplaceholder.typicode.com
  request:
    method: GET
    path: /posts
  reliability:
    urlAllowlist: ["*.typicode.com"]
landing:
  target: ./_landing/my_integration
  pathTemplate: posts_{yyyyMMdd}
`;

function looksJson(page: IntegrationDebugPage): boolean {
  const media = page.contentType?.toLowerCase() ?? "";
  return media.includes("json") || page.bodyPreview.trimStart().startsWith("{") || page.bodyPreview.trimStart().startsWith("[");
}

/** One error-to-text mapping for every toast on this page (the API's detail wins over a generic title). */
function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

/** A success/failure pill (DESIGN.md 7.3): icon + label so color never carries the state alone. */
function OutcomePill({ ok, label }: { ok: boolean; label: string }) {
  const Icon = ok ? CircleCheck : CircleAlert;
  return (
    <span
      className={`inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4 ${ok ? "bg-success/12 text-success" : "bg-destructive/12 text-destructive"}`}
    >
      <Icon className="size-3.5 shrink-0" />
      {label}
    </span>
  );
}

/** Loads an integration's stored (secret-redacted) YAML from the catalog by its pipeline id. */
function useIntegrationYaml(id: string | null) {
  return useQuery({
    queryKey: ["integrations", "yaml", id],
    queryFn: () => pipelineApi.getById(id as string).then((p) => p.yaml),
    enabled: id !== null,
  });
}

/** Read-only view of the integration's YAML document exactly as the catalog stores it (secrets redacted). */
function CodeSheet({ integration, onClose, onDebug }: { integration: Integration; onClose: () => void; onDebug: (yaml: string) => void }) {
  const yaml = useIntegrationYaml(integration.id);

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next) {
          onClose();
        }
      }}
    >
      {/* Focus stays outside Monaco so Escape reaches the sheet, not the editor. */}
      <SheetContent className="w-full sm:max-w-3xl" data-testid="integration-code" onOpenAutoFocus={(event) => event.preventDefault()}>
        <SheetHeader>
          <SheetTitle className="flex min-w-0 items-center gap-2 text-base font-medium">
            <Code className="size-4 shrink-0" />
            <Mono className="truncate">{integration.relativePath}</Mono>
          </SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
          {yaml.isLoading && <Skeleton className="h-[480px] w-full" />}
          {yaml.isError && (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertDescription>Could not load the definition from the catalog.</AlertDescription>
            </Alert>
          )}
          {yaml.data !== undefined && (
            <>
              <CodeView value={yaml.data} language="yaml" height={480} data-testid="integration-code-view" />
              <div className="flex justify-end">
                <Button size="sm" onClick={() => onDebug(yaml.data)}>
                  <Bug />
                  Open in debugger
                </Button>
              </div>
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}

/**
 * The dynamically generated parameter form: the backend parses the YAML through the real loader (debounced as the
 * user edits) and reports the declared `params:`; one input renders per parameter, prefilled with its default and
 * required-marked when it declares none. The values feed the run as overrides, so executing a parameterized
 * integration never means editing its YAML.
 */
function useDeclaredParams(yaml: string) {
  const [described, setDescribed] = useState<IntegrationDescribeResponse | null>(null);
  useEffect(() => {
    let cancelled = false;
    const handle = setTimeout(() => {
      integrationApi
        .describe(yaml)
        .then((d) => {
          if (!cancelled) {
            setDescribed(d);
          }
        })
        .catch(() => {
          // A document mid-edit often does not parse; keep the last good description until it does again.
        });
    }, 500);
    return () => {
      cancelled = true;
      clearTimeout(handle);
    };
  }, [yaml]);
  return described;
}

/** One fetched page of a test run: an expandable section with the request line up top and the details inside. */
function DebugPageSection({ page, defaultOpen }: { page: IntegrationDebugPage; defaultOpen: boolean }) {
  return (
    <Collapsible defaultOpen={defaultOpen} className="rounded-md border border-border">
      <CollapsibleTrigger className="group flex w-full items-center gap-2 px-3 py-2 text-left">
        <ChevronDown className="size-4 shrink-0 text-muted-foreground transition-transform duration-120 group-data-[state=closed]:-rotate-90" />
        <OutcomePill ok={page.status >= 200 && page.status < 300} label={String(page.status)} />
        <Mono className="shrink-0">{page.method}</Mono>
        <Mono className="min-w-0 flex-1 truncate">{page.url}</Mono>
        <span className="shrink-0 text-xs text-muted-foreground">
          {page.durationMs} ms · {page.bytes} B · {page.recordCount < 0 ? "?" : page.recordCount} rec
        </span>
      </CollapsibleTrigger>
      <CollapsibleContent>
        <div className="flex flex-col gap-3 border-t border-border px-3 py-3">
          {page.wouldLandTo && (
            <p className="text-[13px] text-muted-foreground">
              Would land to <Mono>{page.wouldLandTo}</Mono>
            </p>
          )}
          <HeaderTable title="Request headers" headers={page.requestHeaders} />
          <HeaderTable title="Response headers" headers={page.responseHeaders} />
          <div className="flex flex-col gap-1.5">
            <h3 className="text-[13px] font-medium">
              Response body {page.contentType ? `(${page.contentType})` : ""}
            </h3>
            <CodeView value={page.bodyPreview} language={looksJson(page) ? "json" : "plaintext"} lsp={false} height={280} />
          </div>
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}

/** The DeltaForge-style debugger: edit the integration YAML in Monaco (flow-YAML hovers and diagnostics on), fill
 * the auto-generated parameter form, run a safe Test invoke (fetch without landing), and inspect the request,
 * response, timing, pagination steps, and where each page would land. */
function DebuggerSheet({ initialYaml, onClose }: { initialYaml: string; onClose: () => void }) {
  const [yaml, setYaml] = useState(initialYaml);
  const [maxPages, setMaxPages] = useState(3);
  const [paramValues, setParamValues] = useState<Record<string, string>>({});
  const described = useDeclaredParams(yaml);

  const debug = useMutation({
    mutationFn: () => {
      // Send only the values the user actually set; unset fields fall back to the declared defaults server-side.
      const supplied = Object.fromEntries(Object.entries(paramValues).filter(([, v]) => v.length > 0));
      return integrationApi.debug(yaml, maxPages, supplied);
    },
    // The result panel below renders the outcome in place, so only the transport failure needs a toast.
    onError: (error) => toast.error(errorText(error)),
  });

  const missingRequired = (described?.params ?? []).filter((p) => p.required && (paramValues[p.name] ?? "") === "");

  const inlineError = debug.isError ? debug.error : null;
  const result = debug.data;

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !debug.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-[900px]" data-testid="integration-debugger">
        <SheetHeader>
          <SheetTitle className="flex items-center gap-2 text-base font-medium">
            <Bug className="size-4 shrink-0" />
            Integration debugger
          </SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4 pb-4">
          <Alert>
            <Info />
            <AlertDescription>
              Test is a safe preview: it fetches (honoring auth, pagination, and iteration) but lands nothing. It
              shows what a production run would write.
            </AlertDescription>
          </Alert>

          <CodeView
            value={yaml}
            language="yaml"
            height={360}
            readOnly={false}
            onChange={setYaml}
            data-testid="integration-debugger-yaml"
          />

          {described !== null && described.params.length > 0 && (
            <div className="flex flex-col gap-2">
              <h3 className="text-[13px] font-medium">Parameters</h3>
              <div className="flex flex-wrap gap-3">
                {described.params.map((param) => (
                  <div key={param.name} className="flex min-w-56 flex-col gap-1.5">
                    <Label htmlFor={`integration-param-${param.name}`}>
                      {param.name}
                      {param.required && <span className="text-destructive">*</span>}
                    </Label>
                    <Input
                      id={`integration-param-${param.name}`}
                      value={paramValues[param.name] ?? ""}
                      placeholder={param.default ?? undefined}
                      onChange={(e) => setParamValues((current) => ({ ...current, [param.name]: e.target.value }))}
                      className="h-8 font-mono text-[12px]"
                      data-testid={`integration-param-${param.name}`}
                    />
                    <p className="text-xs text-muted-foreground">
                      {param.required ? "required (no default)" : `default: ${param.default}`}
                    </p>
                  </div>
                ))}
              </div>
            </div>
          )}

          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="integration-debugger-max-pages">Max pages</Label>
              <Input
                id="integration-debugger-max-pages"
                type="number"
                min={1}
                max={20}
                value={maxPages}
                onChange={(e) => setMaxPages(Math.max(1, Math.min(20, Number(e.target.value) || 1)))}
                className="h-8 w-28"
              />
            </div>
            <Button
              size="sm"
              disabled={debug.isPending || missingRequired.length > 0}
              onClick={() => debug.mutate()}
              data-testid="integration-debugger-run"
            >
              {debug.isPending ? <Loader2 className="animate-spin" /> : <Bug />}
              Run test
            </Button>
            {missingRequired.length > 0 && (
              <p className="pb-1.5 text-xs text-muted-foreground">
                Supply {missingRequired.map((p) => p.name).join(", ")} to run.
              </p>
            )}
          </div>

          {inlineError !== null && (
            isApiError(inlineError)
              ? <CorrelationError error={inlineError} />
              : (
                <Alert variant="destructive">
                  <CircleAlert />
                  <AlertDescription>{String(inlineError)}</AlertDescription>
                </Alert>
              )
          )}

          {result && (
            <div className="flex flex-col gap-3 border-t border-border pt-3">
              <div className="flex flex-wrap items-center gap-1.5">
                <OutcomePill ok={result.success} label={result.success ? "success" : "failed"} />
                <Badge variant="outline">{result.iterations} iteration(s)</Badge>
                <Badge variant="outline">{result.pagesFetched} page(s) fetched</Badge>
                <Badge variant="outline">{result.filesWritten} would land</Badge>
                <Badge variant="outline">{result.skipped} skipped</Badge>
                <Badge variant="outline">{result.bytesWritten} bytes</Badge>
              </div>
              {result.landedBase && (
                <p className="text-[13px] text-muted-foreground">
                  Would land under <Mono>{result.landedBase}</Mono>
                  {result.watermarkAfter && (
                    <>
                      {" · watermark "}
                      <Mono>{result.watermarkBefore ?? "(none)"}</Mono> to <Mono>{result.watermarkAfter}</Mono>
                    </>
                  )}
                </p>
              )}
              {result.error && (
                <Alert>
                  <TriangleAlert className="text-warning" />
                  <AlertDescription>{result.error}</AlertDescription>
                </Alert>
              )}

              {result.pages.length === 0 ? (
                <Alert>
                  <Info />
                  <AlertDescription>
                    No pages were returned (an empty response, or every page was skipped as empty).
                  </AlertDescription>
                </Alert>
              ) : (
                result.pages.map((page, index) => (
                  <DebugPageSection
                    key={`${page.iteration}-${page.page}-${index}`}
                    page={page}
                    defaultOpen={index === 0}
                  />
                ))
              )}
            </div>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}

function HeaderTable({ title, headers }: { title: string; headers: Record<string, string> }) {
  const entries = Object.entries(headers);
  if (entries.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-col gap-1">
      <h3 className="text-[13px] font-medium">{title}</h3>
      <div className="flex flex-col gap-0.5">
        {entries.map(([name, value]) => (
          <div key={name} className="text-xs text-muted-foreground">
            <Mono className="text-foreground">{name}</Mono>: {value}
          </div>
        ))}
      </div>
    </div>
  );
}

/** Debug an existing integration: load its stored YAML first, then open the debugger prefilled with the real
 * document (falling back to the starter template only if the catalog copy is unavailable). */
function DebugExistingButton({ integration, onOpen }: { integration: Integration; onOpen: (yaml: string) => void }) {
  const [loading, setLoading] = useState(false);

  const openWithDefinition = async () => {
    setLoading(true);
    try {
      const detail = await pipelineApi.getById(integration.id);
      onOpen(detail.yaml || STARTER_YAML);
    } catch {
      onOpen(STARTER_YAML);
    } finally {
      setLoading(false);
    }
  };

  return (
    <Button
      variant="ghost"
      size="xs"
      disabled={loading}
      onClick={(e) => {
        e.stopPropagation();
        void openWithDefinition();
      }}
    >
      {loading ? <Loader2 className="animate-spin" /> : <Bug />}
      Debug
    </Button>
  );
}

export default function IntegrationsPage() {
  const [debuggerYaml, setDebuggerYaml] = useState<string | null>(null);
  const [codeFor, setCodeFor] = useState<Integration | null>(null);

  const flows = useQuery({ queryKey: ["integrations", "list"], queryFn: integrationApi.list });

  const columns = useMemo<Column<Integration>[]>(
    () => [
      { id: "name", header: "Integration", render: (f) => <Mono className="font-medium">{f.name}</Mono> },
      { id: "batch", header: "Batch", render: (f) => f.batch ?? "-" },
      { id: "path", header: "File", render: (f) => <Mono>{f.relativePath}</Mono> },
      { id: "active", header: "Status", render: (f) => <ActiveBadge active={f.active} /> },
      {
        id: "actions",
        header: "",
        align: "right",
        render: (f) => (
          <div className="flex justify-end gap-0.5">
            <Button
              variant="ghost"
              size="xs"
              onClick={(e) => {
                e.stopPropagation();
                setCodeFor(f);
              }}
            >
              <Code />
              View code
            </Button>
            <DebugExistingButton integration={f} onOpen={(yaml) => setDebuggerYaml(yaml)} />
          </div>
        ),
      },
    ],
    [],
  );

  const rows = flows.data ?? [];

  return (
    <Page data-testid="integrations-page">
      <PageHeader
        title="Integrations"
        subtitle="Acquisition flows (flowType: acq) in your estate: HTTP APIs, SFTP drops, S3 buckets, and storage tables, each landing raw payloads to the lake. View the YAML, test and debug here, and run or schedule them like any pipeline."
        actions={(
          <>
            <Button
              variant="outline"
              size="sm"
              onClick={() => void flows.refetch()}
              disabled={flows.isRefetching}
              aria-label="Refresh integrations"
            >
              <RefreshCw className={flows.isRefetching ? "animate-spin" : undefined} />
              Refresh
            </Button>
            <Button size="sm" onClick={() => setDebuggerYaml(STARTER_YAML)}>
              <Bug />
              New / Debug
            </Button>
          </>
        )}
      />

      {flows.isError ? (
        isApiError(flows.error)
          ? <CorrelationError error={flows.error} />
          : (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertDescription>Could not load integrations.</AlertDescription>
            </Alert>
          )
      ) : rows.length === 0 && !flows.isLoading ? (
        <EmptyState
          icon={<Cable />}
          title="No integrations yet"
          description="Add a flowType: acq document to your repo (see samples/acquire), or click New / Debug to try one against a public API."
          action={(
            <Button size="sm" onClick={() => setDebuggerYaml(STARTER_YAML)}>
              <Bug />
              New / Debug
            </Button>
          )}
          data-testid="integrations-empty"
        />
      ) : (
        <DataTable
          columns={columns}
          rows={flows.isLoading ? undefined : rows}
          rowKey={(f) => f.id}
          emptyMessage="No integrations."
        />
      )}

      {codeFor !== null && (
        <CodeSheet
          integration={codeFor}
          onClose={() => setCodeFor(null)}
          onDebug={(yaml) => {
            setCodeFor(null);
            setDebuggerYaml(yaml);
          }}
        />
      )}
      {debuggerYaml !== null && <DebuggerSheet initialYaml={debuggerYaml} onClose={() => setDebuggerYaml(null)} />}
    </Page>
  );
}
