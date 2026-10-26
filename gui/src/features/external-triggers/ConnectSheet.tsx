import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Info, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { repoApi, scheduleApi } from "../../api/endpoints";
import type { ExternalTrigger, ExternalTriggerSetup } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CopyButton } from "../../components/CopyButton";
import { runtimeConfig } from "../../config/runtime";
import { keyScript, workflowPath, workflowYaml } from "./workflow";

/** The control plane's address as a workflow outside the browser reaches it: the API base the GUI calls, made absolute. */
export function controlPlaneAddress(): string {
  const base = runtimeConfig().apiBaseUrl;
  return new URL(base === "" ? "/" : base, window.location.origin).toString().replace(/\/+$/, "");
}

/**
 * How to connect a trigger, in a right side sheet: the key once (for a key trigger, just created or rotated), what is
 * worth knowing about the trigger, and the workflow to commit, written for the schedules a merge should fire, which the
 * person picks here and sees the YAML follow. A key trigger also gets the two calls any other system makes.
 */
export function ConnectSheet({
  trigger, setup, secret, warnings, onClose,
}: {
  trigger: ExternalTrigger;
  setup: ExternalTriggerSetup;
  secret: string | null;
  warnings: string[];
  onClose: () => void;
}) {
  const reposQuery = useQuery({ queryKey: ["repos", "list", 200], queryFn: () => repoApi.list({ page: 1, pageSize: 200 }) });
  const repoId = reposQuery.data?.items.find((r) => r.name === trigger.repoName)?.id ?? null;
  const schedulesQuery = useQuery({
    queryKey: ["schedules", "for-trigger", repoId],
    queryFn: () => scheduleApi.list({ repoId: repoId!, page: 1, pageSize: 200 }),
    enabled: repoId !== null && trigger.allSchedules,
  });
  const selectable = useMemo(
    () => trigger.allSchedules ? (schedulesQuery.data?.items ?? []).map((s) => s.name).sort((a, b) => a.localeCompare(b)) : trigger.schedules,
    [trigger, schedulesQuery.data],
  );
  const [onMerge, setOnMerge] = useState<string[] | null>(null);
  const merge = onMerge ?? (trigger.allSchedules ? [] : trigger.schedules);
  const [keepTraces, setKeepTraces] = useState(true);
  const address = controlPlaneAddress();
  const yaml = workflowYaml(trigger, setup, address, { onMerge: merge, selectable, keepTraces });
  const toggle = (schedule: string) =>
    setOnMerge(merge.includes(schedule) ? merge.filter((s) => s !== schedule) : [...merge, schedule]);

  const picker = (
    <div className="flex flex-col gap-2 rounded-md border border-border p-2.5">
      <Label className="text-[13px]">{trigger.kind === "key" ? "Schedules to fire" : "On every merge, fire"}</Label>
      {selectable.length === 0 && (
        <p className="text-xs text-muted-foreground">
          {trigger.allSchedules ? "The repository has no schedules yet." : "The trigger may fire no schedule; a merge only syncs."}
        </p>
      )}
      <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
        {selectable.map((schedule) => (
          <label key={schedule} className="flex items-center gap-2 text-[13px]">
            <Checkbox checked={merge.includes(schedule)} onCheckedChange={() => toggle(schedule)} data-testid={`connect-merge-${schedule}`} />
            <span className="truncate font-mono text-[12px]">{schedule}</span>
          </label>
        ))}
      </div>
      <p className="text-xs text-muted-foreground">
        {trigger.kind === "key"
          ? "Each request names the schedules it fires; the workflow and the script below fire these."
          : "Run by hand, the workflow offers each of them, this set, or none, as a choice."}
      </p>
    </div>
  );

  const workflow = (
    <div className="flex min-h-0 flex-1 flex-col gap-3">
      <label className="inline-flex items-center gap-2 text-[13px]">
        <Switch checked={keepTraces} onCheckedChange={setKeepTraces} data-testid="connect-keep-traces" />
        Keep the sync&apos;s and every run&apos;s trace as an artifact of the job
      </label>
      <div className="flex items-center justify-between gap-2">
        <p className="text-[13px]">
          Commit as <code className="font-mono text-[12px]">{workflowPath(trigger)}</code>
          {trigger.gitHub?.repository ? <> in <code className="font-mono text-[12px]">{trigger.gitHub.repository}</code></> : null}
        </p>
        <CopyButton label="Copy workflow" text={yaml} testId="connect-copy-workflow" />
      </div>
      {setup.gitHubActionReference.startsWith("./") && (
        <Alert>
          <Info />
          <AlertDescription>
            <p>
              The workflow uses a copy of SQLFlow&apos;s action kept in the repository: copy SQLFlow&apos;s{" "}
              <code className="font-mono text-[12px]">integrations/github</code> folder to{" "}
              <code className="font-mono text-[12px]">{setup.gitHubActionReference.slice(2)}</code>. To use one copy for every
              repository instead, set <code className="font-mono text-[12px]">ControlPlane:ExternalTriggers:GitHub:ActionReference</code>.
            </p>
          </AlertDescription>
        </Alert>
      )}
      <div data-testid="connect-workflow">
        <CodeView value={yaml} language="yaml" lsp={false} height={460} />
      </div>
    </div>
  );

  return (
    <Sheet open onOpenChange={(next) => { if (!next) onClose(); }}>
      <SheetContent className="w-full sm:max-w-2xl" data-testid="connect-sheet">
        <SheetHeader>
          <SheetTitle>Connect {trigger.name}</SheetTitle>
        </SheetHeader>
        <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 pb-2">
          {secret !== null && (
            <div className="flex flex-col gap-2">
              <Alert>
                <TriangleAlert className="text-warning" />
                <AlertTitle>Shown only once</AlertTitle>
                <AlertDescription>
                  This is the only time the key is shown. Put it in the calling system&apos;s secret store now (for GitHub, a
                  repository secret named <code className="font-mono text-[12px]">SQLFLOW_TRIGGER_KEY</code>); it cannot be shown again.
                </AlertDescription>
              </Alert>
              <div className="flex items-start justify-between gap-2 rounded-md border border-border bg-muted/50 p-2">
                <code className="min-w-0 break-all font-mono text-[12px] leading-5" data-testid="connect-key">{secret}</code>
                <CopyButton label="Copy" text={secret} testId="connect-copy-key" />
              </div>
            </div>
          )}

          {warnings.length > 0 && (
            <Alert data-testid="connect-warnings">
              <TriangleAlert className="text-warning" />
              <AlertTitle>Worth a look</AlertTitle>
              <AlertDescription>
                <ul className="list-disc space-y-1 pl-4">
                  {warnings.map((warning) => <li key={warning}>{warning}</li>)}
                </ul>
              </AlertDescription>
            </Alert>
          )}

          {picker}

          {trigger.kind === "key"
            ? (
              <Tabs defaultValue="github" className="flex min-h-0 flex-1 flex-col gap-3">
                <TabsList variant="line" className="w-full justify-start">
                  <TabsTrigger value="github" className="flex-none" data-testid="connect-tab-github">GitHub workflow</TabsTrigger>
                  <TabsTrigger value="other" className="flex-none" data-testid="connect-tab-other">Another system</TabsTrigger>
                </TabsList>
                <TabsContent value="github" className="flex min-h-0 flex-1 flex-col">{workflow}</TabsContent>
                <TabsContent value="other" className="flex flex-col gap-2">
                  <div className="flex items-center justify-between gap-2">
                    <p className="text-[13px]">Exchange the key, then make the request:</p>
                    <CopyButton label="Copy script" text={keyScript(address, merge)} testId="connect-copy-script" />
                  </div>
                  <CodeView value={keyScript(address, merge)} language="plaintext" height={220} />
                </TabsContent>
              </Tabs>
            )
            : workflow}
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button size="sm" onClick={onClose} data-testid="connect-done">Done</Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
