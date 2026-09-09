import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Code, Loader2, Search } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { repoSourceApi } from "../../api/endpoints";
import type { DiscoveredFlow, RepoSource } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";

// Mirrors the server's guard: a whole ${scheme:locator} reference, never a raw token.
const REFERENCE_RE = /^\$\{[a-zA-Z]+:[^}]+\}$/;

/**
 * Registers a git repo as a tracked source, or edits an existing one (pass `source`), with an optional preview-first
 * scan: discover the repo's flows, preview each, and choose which to import. The credential is a ${...} reference
 * (created in the vault), never a raw token. Registering persists the source and its selection; the SYNC (managed on
 * the interval, or "Sync now") is what actually imports the selected flows into the catalog, which the scheduler then
 * executes. Because the server upserts by name, editing reuses the same register call with the source's name; the
 * name is therefore locked in edit mode (changing it would create a second source instead of updating this one).
 *
 * Passing `presetName` (without a `source`) opens the dialog to ATTACH a git source to an existing manual repo: the
 * name is fixed to that repo's name and locked, so the registered source merges onto the repo (they are joined by
 * name) and the repo becomes managed on its next sync. This is the same register call, so there is one code path.
 */
export function RegisterSourceDialog({
  source, presetName, onClose,
}: {
  source?: RepoSource;
  presetName?: string;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const isEdit = source !== undefined;
  // The name is fixed both when editing an existing source and when attaching to a manual repo: in either case a
  // different name would create a second, unrelated source instead of updating/merging onto this one.
  const nameLocked = isEdit || presetName !== undefined;
  const [name, setName] = useState(source?.name ?? presetName ?? "");
  const [remoteUrl, setRemoteUrl] = useState(source?.remoteUrl ?? "");
  const [branch, setBranch] = useState(source?.branch ?? "main");
  const [intervalText, setIntervalText] = useState(source ? String(source.syncIntervalSeconds) : "300");
  const [enabled, setEnabled] = useState(source?.enabled ?? true);
  const [credentialUsername, setCredentialUsername] = useState(source?.credentialUsername ?? "");
  const [credentialReference, setCredentialReference] = useState(source?.credentialReference ?? "");
  const [discovered, setDiscovered] = useState<DiscoveredFlow[] | null>(null);
  const [included, setIncluded] = useState<ReadonlySet<string>>(new Set());
  const [previewPath, setPreviewPath] = useState<string | null>(null);

  const intervalValid = /^\d+$/.test(intervalText.trim()) && Number.parseInt(intervalText.trim(), 10) > 0;
  const referenceValid = credentialReference.trim() === "" || REFERENCE_RE.test(credentialReference.trim());

  const credentialPayload = () => ({
    credentialUsername: credentialUsername.trim() === "" ? null : credentialUsername.trim(),
    credentialReference: credentialReference.trim() === "" ? null : credentialReference.trim(),
  });

  const discover = useMutation({
    mutationFn: () =>
      repoSourceApi.discover({
        remoteUrl: remoteUrl.trim(),
        branch: branch.trim() === "" ? null : branch.trim(),
        ...credentialPayload(),
      }),
    onSuccess: (flows) => {
      setDiscovered(flows);
      setIncluded(new Set(flows.map((f) => f.relativePath))); // default: import everything discovered
      setPreviewPath(null);
      if (flows.length === 0) {
        toast.info("No *.flow.yaml files were found in this repo/branch.");
      }
    },
    onError: (error) =>
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const register = useMutation({
    mutationFn: repoSourceApi.register,
    onSuccess: () => {
      toast.success(
        isEdit
          ? "Repo source updated. Use \"Sync now\" to pull with the new settings."
          : "Repo source registered. The next sync imports the selected flows into the catalog.",
      );
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      onClose();
    },
    onError: (error) =>
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  // Not (re-)discovered in this dialog: keep the source's existing exclusion when editing (sending null would wipe it
  // and re-import every flow), or send null for a new source so the sync imports everything. Otherwise send the
  // unchecked files as the exclusion, which the sync honors (an excluded flow never becomes a catalog pipeline).
  const excludedFlowPaths = useMemo(
    () =>
      discovered === null
        ? source?.excludedFlowPaths ?? null
        : discovered.filter((f) => !included.has(f.relativePath)).map((f) => f.relativePath),
    [discovered, included, source],
  );

  const canDiscover = remoteUrl.trim() !== "" && referenceValid && !discover.isPending;
  const canSubmit = name.trim() !== "" && remoteUrl.trim() !== "" && intervalValid && referenceValid && !register.isPending;
  const includedCount = discovered === null ? 0 : discovered.filter((f) => included.has(f.relativePath)).length;

  const toggle = (path: string) =>
    setIncluded((current) => {
      const next = new Set(current);
      if (next.has(path)) {
        next.delete(path);
      } else {
        next.add(path);
      }

      return next;
    });

  const submit = () => {
    register.mutate({
      name: name.trim(),
      remoteUrl: remoteUrl.trim(),
      branch: branch.trim() === "" ? null : branch.trim(),
      syncIntervalSeconds: Number.parseInt(intervalText.trim(), 10),
      enabled,
      ...credentialPayload(),
      excludedFlowPaths,
    });
  };

  return (
    <Sheet
      open
      onOpenChange={(open) => {
        if (!open && !register.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full gap-0 sm:max-w-xl" data-testid="register-source-dialog">
        <SheetHeader>
          <SheetTitle>
            {isEdit ? "Edit source" : presetName !== undefined ? `Attach a git source to '${presetName}'` : "Register source"}
          </SheetTitle>
          <SheetDescription>
            {isEdit
              ? "Update this tracked source's settings and credentials, then sync to apply them."
              : presetName !== undefined
                ? "Give this manual repo a git remote so its flows sync automatically. On the next sync it becomes a managed repo."
                : "Track a git repo so its flows sync into the catalog on an interval."}
          </SheetDescription>
        </SheetHeader>

        <div className="flex-1 overflow-y-auto px-4 pb-4">
          <div className="flex flex-col gap-4">
            {register.isError && isApiError(register.error) && <CorrelationError error={register.error} />}
            {register.isError && !isApiError(register.error) && (
              <p className="text-[13px] text-destructive">{String(register.error)}</p>
            )}

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="source-name">Name</Label>
              <Input
                id="source-name"
                className="h-8"
                required
                disabled={nameLocked}
                value={name}
                onChange={(e) => setName(e.target.value)}
                data-testid="source-name"
              />
              {nameLocked && (
                <p className="text-xs text-muted-foreground">
                  {isEdit
                    ? "The name identifies the source and cannot be changed here."
                    : "The name is fixed to the repo so the source attaches to it."}
                </p>
              )}
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="source-remote-url">Remote URL</Label>
              <Input
                id="source-remote-url"
                className="h-8 font-mono text-[12px]"
                required
                placeholder="https://git.example.com/org/repo.git"
                value={remoteUrl}
                onChange={(e) => setRemoteUrl(e.target.value)}
                data-testid="source-remote-url"
              />
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="source-branch">Branch</Label>
              <Input
                id="source-branch"
                className="h-8 font-mono text-[12px]"
                value={branch}
                onChange={(e) => setBranch(e.target.value)}
                data-testid="source-branch"
              />
              <p className="text-xs text-muted-foreground">Empty falls back to the server default (main).</p>
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="source-interval">Sync interval (seconds)</Label>
              <Input
                id="source-interval"
                className="h-8"
                type="number"
                min={1}
                value={intervalText}
                onChange={(e) => setIntervalText(e.target.value)}
                aria-invalid={!intervalValid}
                data-testid="source-interval"
              />
              <p className={intervalValid ? "text-xs text-muted-foreground" : "text-xs text-destructive"}>
                A positive whole number of seconds between syncs.
              </p>
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="source-credential-username">Git username (optional)</Label>
              <Input
                id="source-credential-username"
                className="h-8"
                value={credentialUsername}
                onChange={(e) => setCredentialUsername(e.target.value)}
                data-testid="source-credential-username"
              />
              <p className="text-xs text-muted-foreground">
                Only needed for hosts that authenticate the username too (e.g. Bitbucket app passwords).
              </p>
            </div>

            <div className="flex flex-col gap-1.5">
              <Label htmlFor="source-credential-reference">Credential reference (optional)</Label>
              <Input
                id="source-credential-reference"
                className="h-8 font-mono text-[12px]"
                placeholder="${keyvault:my-vault/github-pat}"
                value={credentialReference}
                onChange={(e) => setCredentialReference(e.target.value)}
                aria-invalid={!referenceValid}
                data-testid="source-credential-reference"
              />
              {referenceValid ? (
                <p className="text-xs text-muted-foreground">
                  {"A ${keyvault:vault/secret} or ${env:NAME} reference to the token. Create the secret in "
                    + "your vault; SQLFlow only references it. Leave blank for a public remote."}
                </p>
              ) : (
                <p className="text-xs text-destructive">
                  {"Must be a reference like ${keyvault:my-vault/github-pat}, not a raw token."}
                </p>
              )}
            </div>

            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={enabled} onCheckedChange={setEnabled} data-testid="source-enabled" />
              Enabled
            </Label>

            <Separator />

            <div className="flex flex-wrap items-center gap-3">
              <Button
                variant="outline"
                size="sm"
                disabled={!canDiscover}
                onClick={() => discover.mutate()}
                data-testid="discover-flows"
              >
                {discover.isPending ? <Loader2 className="animate-spin" /> : <Search />}
                {discovered ? "Re-discover flows" : "Discover flows"}
              </Button>
              <p className="min-w-[200px] flex-1 text-[13px] text-muted-foreground">
                {discovered === null
                  ? "Optional: preview the repo's flows and choose which to import. Skip to import every flow."
                  : `${includedCount} of ${discovered.length} flow(s) will be imported on sync.`}
              </p>
            </div>
            {discover.isError && isApiError(discover.error) && <CorrelationError error={discover.error} />}

            {discovered !== null && discovered.length > 0 && (
              <div
                className="max-h-[340px] overflow-auto rounded-md border border-input"
                data-testid="discovered-flows"
              >
                <div className="sticky top-0 z-10 flex items-center gap-1 border-b border-border bg-card p-1">
                  <Button
                    variant="ghost"
                    size="xs"
                    onClick={() => setIncluded(new Set(discovered.map((f) => f.relativePath)))}
                  >
                    Select all
                  </Button>
                  <Button variant="ghost" size="xs" onClick={() => setIncluded(new Set())}>
                    Select none
                  </Button>
                </div>
                {discovered.map((f) => (
                  <div
                    key={f.relativePath}
                    className="border-b border-border px-2 last:border-b-0"
                    data-testid="discovered-flow-row"
                  >
                    <div className="flex items-center gap-2 py-1.5">
                      <Checkbox
                        checked={included.has(f.relativePath)}
                        onCheckedChange={() => toggle(f.relativePath)}
                        aria-label={`include ${f.relativePath}`}
                      />
                      <div className="min-w-0 flex-1">
                        <div className="truncate text-[13px] font-medium">{f.flowName ?? f.relativePath}</div>
                        <div className="truncate font-mono text-xs text-muted-foreground">{f.relativePath}</div>
                      </div>
                      {f.parseOk
                        ? f.kind !== null && <Badge variant="outline">{f.kind}</Badge>
                        : (
                          <Tooltip>
                            <TooltipTrigger asChild>
                              <span>
                                <Badge variant="destructive" data-testid="discovered-flow-error">
                                  parse error
                                </Badge>
                              </span>
                            </TooltipTrigger>
                            <TooltipContent className="max-w-lg break-words">
                              {f.parseError ?? "parse error"}
                            </TooltipContent>
                          </Tooltip>
                        )}
                      {f.content !== null && (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <Button
                              variant="ghost"
                              size="icon-xs"
                              aria-label="Preview"
                              aria-pressed={previewPath === f.relativePath}
                              onClick={() => setPreviewPath((p) => (p === f.relativePath ? null : f.relativePath))}
                              data-testid="discovered-flow-preview"
                            >
                              <Code />
                            </Button>
                          </TooltipTrigger>
                          <TooltipContent>Preview</TooltipContent>
                        </Tooltip>
                      )}
                    </div>
                    {previewPath === f.relativePath && f.content !== null && (
                      <div className="pb-2">
                        <CodeView value={f.content} language="yaml" height={240} />
                      </div>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>

        <SheetFooter className="flex-row justify-end gap-2 border-t border-border">
          <Button
            variant="outline"
            size="sm"
            onClick={onClose}
            disabled={register.isPending}
            data-testid="register-source-cancel"
          >
            Cancel
          </Button>
          <Button size="sm" onClick={submit} disabled={!canSubmit} data-testid="register-source-submit">
            {register.isPending && <Loader2 className="animate-spin" />}
            {isEdit ? "Save" : presetName !== undefined ? "Attach" : "Register"}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
