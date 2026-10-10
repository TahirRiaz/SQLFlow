import { useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { semanticLayerApi } from "../../api/endpoints";
import type { ExtractedSemanticReport } from "../../api/types";
import { formatBytes } from "../../lib/time";
import { announceStored, describeReport, errorText, refreshSemanticLayer, SEMANTIC_ROOT } from "./shared";

const MAX_REPORT_FILE_LENGTH = 260;

/** Why a report name cannot key the report's rows, or null. Mirrors the server's own rule, which has the last word. */
function reportFileProblem(value: string): string | null {
  if (value.trim() === "") {
    return "A report name is required.";
  }
  if (value !== value.trim()) {
    return "The name may not start or end with a space.";
  }
  if (value.length > MAX_REPORT_FILE_LENGTH) {
    return `The name is longer than ${MAX_REPORT_FILE_LENGTH} characters.`;
  }
  if (/[#\\\u0000-\u001f]/.test(value)) {
    return "The name may not contain '#' or a backslash.";
  }
  if (value.startsWith("/") || value.split("/").some((part) => part === "" || part === "." || part === "..")) {
    return "The name must be a relative path with no empty, '.' or '..' parts.";
  }
  return null;
}

const isSpecFile = (name: string) => /\.ya?ml$/i.test(name);

/** The report name a file stands for: a .pbix keeps its name, a specification drops its trailing .yaml. */
const reportNameOf = (name: string) => name.replace(/\.ya?ml$/i, "");

/**
 * Adds a Power BI report to the semantic layer for one Power BI subscriber. A .pbix is sent to the isolated extractor
 * and what it read is shown for review before anything is stored; a specification made with `sqlflow powerbi extract`
 * is stored directly (the server validates it). Either way the same store call saves it, and the repo's managed sync
 * applies it.
 */
export function ReportUploadDialog({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient();
  const fileInput = useRef<HTMLInputElement>(null);
  const [subscriber, setSubscriber] = useState<string>("");
  const [file, setFile] = useState<File | null>(null);
  const [reportFile, setReportFile] = useState("");
  const [extracted, setExtracted] = useState<ExtractedSemanticReport | null>(null);
  const abort = useRef<AbortController | null>(null);

  const capabilities = useQuery({
    queryKey: [SEMANTIC_ROOT, "reports", "capabilities"],
    queryFn: () => semanticLayerApi.reportCapabilities(),
  });
  const subscribers = useQuery({
    queryKey: [SEMANTIC_ROOT, "reports", "subscribers"],
    queryFn: () => semanticLayerApi.reportSubscribers(),
  });

  const chosen = useMemo(
    () => subscribers.data?.find((s) => `${s.repoId}::${s.subscriberKey}` === subscriber) ?? null,
    [subscribers.data, subscriber],
  );

  const extractionEnabled = capabilities.data?.extractionEnabled === true;
  const accept = extractionEnabled ? ".pbix,.yaml,.yml" : ".yaml,.yml";
  const isSpec = file !== null && isSpecFile(file.name);
  const nameProblem = file === null ? null : reportFileProblem(reportFile);
  const sizeProblem = (() => {
    if (file === null || capabilities.data === undefined) {
      return null;
    }
    const limit = isSpec ? capabilities.data.maxSpecBytes : capabilities.data.maxReportBytes;
    return file.size > limit ? `This file is ${formatBytes(file.size)}, over the ${formatBytes(limit)} limit.` : null;
  })();
  const kindProblem = file !== null && !isSpec && !/\.pbix$/i.test(file.name)
    ? "Choose a .pbix report or a .pbix.yaml specification."
    : file !== null && !isSpec && !extractionEnabled
      ? "Report extraction is not enabled here. Make a specification with 'sqlflow powerbi extract' and upload that."
      : null;

  const extract = useMutation({
    mutationFn: () => {
      abort.current = new AbortController();
      return semanticLayerApi.extractReport(file!, reportFile, abort.current.signal);
    },
    onSuccess: (result) => setExtracted(result),
  });

  const store = useMutation({
    mutationFn: async () => {
      const spec = isSpec ? await file!.text() : extracted!.spec;
      return semanticLayerApi.storeReport({
        repoId: chosen!.repoId,
        subscriber: chosen!.name,
        reportFile: isSpec ? reportFile : extracted!.reportFile,
        spec,
      });
    },
    onSuccess: (result) => {
      announceStored(result);
      refreshSemanticLayer(queryClient);
      onClose();
    },
  });

  const busy = extract.isPending || store.isPending;
  const ready = chosen !== null && file !== null && nameProblem === null && sizeProblem === null && kindProblem === null;
  const needsExtraction = ready && !isSpec && (extracted === null || extracted.reportFile !== reportFile);

  const chooseFile = (next: File | null) => {
    setFile(next);
    setExtracted(null);
    extract.reset();
    store.reset();
    setReportFile(next === null ? "" : reportNameOf(next.name));
  };

  const close = () => {
    abort.current?.abort();
    onClose();
  };

  const error = extract.error ?? store.error;

  return (
    <Dialog open onOpenChange={(next) => !next && !store.isPending && close()}>
      <DialogContent className="sm:max-w-xl" data-testid="semantic-report-upload-dialog">
        <DialogHeader>
          <DialogTitle>Upload a Power BI report</DialogTitle>
          <DialogDescription>
            {extractionEnabled
              ? "Choose the .pbix itself, or a .pbix.yaml specification made with 'sqlflow powerbi extract'. A .pbix is "
                + "read by the isolated extractor, never by the control plane, and you review what it found before it is saved."
              : "Choose a .pbix.yaml specification made with 'sqlflow powerbi extract <report.pbix>'. This control plane "
                + "does not read .pbix files itself."}
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          {error !== null && (
            <p className="text-[13px] text-destructive" data-testid="semantic-report-upload-error">{errorText(error)}</p>
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="semantic-report-subscriber">Subscriber</Label>
            <Select value={subscriber} onValueChange={setSubscriber} disabled={busy}>
              <SelectTrigger id="semantic-report-subscriber" className="h-8" data-testid="semantic-report-subscriber">
                <SelectValue placeholder={subscribers.isPending ? "Loading subscribers" : "Choose a Power BI subscriber"} />
              </SelectTrigger>
              <SelectContent>
                {(subscribers.data ?? []).map((s) => (
                  <SelectItem key={`${s.repoId}::${s.subscriberKey}`} value={`${s.repoId}::${s.subscriberKey}`}>
                    {`${s.name} (${s.repoName})`}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {subscribers.data?.length === 0 && (
              <p className="text-xs text-muted-foreground">
                No repository declares a Power BI subscriber yet. Add one to a subscribers.yaml (type: PowerBI, with the
                server its report reads through) and sync the repo.
              </p>
            )}
            {subscribers.isError && <p className="text-xs text-destructive">{errorText(subscribers.error)}</p>}
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="semantic-report-file">File</Label>
            <input
              ref={fileInput}
              id="semantic-report-file"
              type="file"
              accept={accept}
              className="hidden"
              onChange={(event) => chooseFile(event.target.files?.[0] ?? null)}
              data-testid="semantic-report-file"
            />
            <div className="flex items-center gap-2">
              <Button variant="outline" size="sm" onClick={() => fileInput.current?.click()} disabled={busy}>
                Choose file
              </Button>
              <span className="truncate text-[13px] text-muted-foreground">
                {file === null ? "No file chosen" : `${file.name} (${formatBytes(file.size)})`}
              </span>
            </div>
            {(kindProblem ?? sizeProblem) !== null && (
              <p className="text-xs text-destructive">{kindProblem ?? sizeProblem}</p>
            )}
          </div>

          {file !== null && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="semantic-report-name">Report name</Label>
              <Input
                id="semantic-report-name"
                value={reportFile}
                onChange={(event) => setReportFile(event.target.value)}
                aria-invalid={nameProblem !== null || undefined}
                className="h-8 font-mono text-[12px]"
                disabled={busy}
                data-testid="semantic-report-name"
              />
              {nameProblem === null ? (
                <p className="text-xs text-muted-foreground">
                  How the report is known on its pages and in the catalog. Uploading a report with the same name again
                  replaces it.
                </p>
              ) : <p className="text-xs text-destructive">{nameProblem}</p>}
            </div>
          )}

          {extracted !== null && extracted.reportFile === reportFile && (
            <div className="flex flex-col gap-1 rounded-md border p-3 text-[13px]" data-testid="semantic-report-extracted">
              <span className="font-medium">{`Read ${describeReport(extracted.summary)}.`}</span>
              <span className="text-muted-foreground">
                {`${extracted.summary.resolvedTables} of ${extracted.summary.tables} model tables load from a warehouse table, `
                  + `and ${extracted.summary.relationships} relationship${extracted.summary.relationships === 1 ? "" : "s"} were found.`}
              </span>
              {extracted.summary.warnings.length > 0 && (
                <ul className="mt-1 list-disc pl-5 text-[12px] text-warning">
                  {extracted.summary.warnings.map((warning) => <li key={warning}>{warning}</li>)}
                </ul>
              )}
            </div>
          )}
        </div>

        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={close} disabled={store.isPending}>Cancel</Button>
          {needsExtraction ? (
            <Button size="sm" onClick={() => extract.mutate()} disabled={busy} data-testid="semantic-report-extract">
              {extract.isPending && <Loader2 className="animate-spin" />}
              {extract.isPending ? "Reading report" : "Read report"}
            </Button>
          ) : (
            <Button size="sm" onClick={() => store.mutate()} disabled={!ready || busy} data-testid="semantic-report-save">
              {store.isPending && <Loader2 className="animate-spin" />}
              Save
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
