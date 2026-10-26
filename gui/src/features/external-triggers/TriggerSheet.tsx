import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { GitBranch, KeyRound, Loader2, Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { externalTriggerApi, repoApi, repoSourceApi, scheduleApi } from "../../api/endpoints";
import type { ExternalTrigger, ExternalTriggerKind, ExternalTriggerWritten } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { errorText } from "./triggerVisuals";

const NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;
const REPOSITORY_PATTERN = /^[A-Za-z0-9][A-Za-z0-9-]{0,38}\/[A-Za-z0-9._-]{1,100}$/;

const KEY_EXPIRY: { label: string; days: number | null }[] = [
  { label: "No expiry", days: null },
  { label: "90 days", days: 90 },
  { label: "1 year", days: 365 },
  { label: "2 years", days: 730 },
];

/** One of the two ways a trigger proves who it is, as a card to pick. */
function KindOption({
  kind, selected, disabled, onSelect,
}: { kind: ExternalTriggerKind; selected: boolean; disabled: boolean; onSelect: () => void }) {
  const github = kind === "github";
  const Icon = github ? GitBranch : KeyRound;
  return (
    <button
      type="button"
      onClick={onSelect}
      disabled={disabled}
      aria-pressed={selected}
      data-testid={`trigger-kind-${kind}`}
      className={cn(
        "flex flex-1 flex-col gap-1 rounded-md border p-3 text-left transition-colors duration-150",
        selected ? "border-primary bg-primary/5 ring-1 ring-primary/40" : "border-border hover:bg-accent/50",
        disabled && !selected && "opacity-50",
      )}
    >
      <span className="inline-flex items-center gap-2 text-[13px] font-medium">
        <Icon className="size-4 text-muted-foreground" />
        {github ? "GitHub workflow" : "Key"}
        {github && <span className="text-[11px] font-normal text-muted-foreground">recommended</span>}
      </span>
      <span className="text-xs text-muted-foreground">
        {github
          ? "The job proves who it is with the token GitHub mints for it. Nothing is stored on either side."
          : "For a system that cannot present GitHub's token. The key is shown once; only its hash is kept."}
      </span>
    </button>
  );
}

/** A field's error under it, in the form's one style (DESIGN.md 7.5). */
function FieldError({ message }: { message: string | null }) {
  return message === null ? null : <p className="text-xs text-destructive">{message}</p>;
}

/**
 * Creates or changes a trigger in a right side sheet (DESIGN.md 7.4): how it proves who it is, the repository it syncs
 * and the schedules it may fire, and for a GitHub trigger the repository, branch, workflow and environment a job must
 * run in. A trigger keeps its kind for life, so editing shows it but does not offer the other.
 */
export function TriggerSheet({
  existing, onSaved, onClose,
}: {
  existing: ExternalTrigger | null;
  onSaved: (written: ExternalTriggerWritten, created: boolean) => void;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<ExternalTriggerKind>(existing?.kind ?? "github");
  const [name, setName] = useState(existing?.name ?? "");
  const [description, setDescription] = useState(existing?.description ?? "");
  const [repoName, setRepoName] = useState(existing?.repoName ?? "");
  const [allSchedules, setAllSchedules] = useState(existing?.allSchedules ?? false);
  const [schedules, setSchedules] = useState<string[]>(existing?.schedules ?? []);
  const [extraSchedule, setExtraSchedule] = useState("");
  const [repository, setRepository] = useState(existing?.gitHub?.repository ?? "");
  const [repositoryId, setRepositoryId] = useState(existing?.gitHub?.repositoryId ?? "");
  const [gitRef, setGitRef] = useState(existing?.gitHub?.ref ?? "");
  const [workflow, setWorkflow] = useState(existing?.gitHub?.workflow ?? "");
  const [environment, setEnvironment] = useState(existing?.gitHub?.environment ?? "");
  const [keyDays, setKeyDays] = useState<number | null>(null);
  const [touched, setTouched] = useState(false);

  const sourcesQuery = useQuery({
    queryKey: ["repo-sources", "list", 200],
    queryFn: () => repoSourceApi.list({ page: 1, pageSize: 200 }),
  });
  const reposQuery = useQuery({ queryKey: ["repos", "list", 200], queryFn: () => repoApi.list({ page: 1, pageSize: 200 }) });
  const source = sourcesQuery.data?.items.find((s) => s.name === repoName) ?? null;
  const repoId = reposQuery.data?.items.find((r) => r.name === repoName)?.id ?? null;
  const schedulesQuery = useQuery({
    queryKey: ["schedules", "for-trigger", repoId],
    queryFn: () => scheduleApi.list({ repoId: repoId!, page: 1, pageSize: 200 }),
    enabled: repoId !== null,
  });
  const known = useMemo(() => (schedulesQuery.data?.items ?? []).map((s) => s.name).sort((a, b) => a.localeCompare(b)), [schedulesQuery.data]);
  const unknownPicked = schedules.filter((s) => !known.some((k) => k.toLowerCase() === s.toLowerCase()));

  const effectiveRef = gitRef.trim() !== "" ? gitRef.trim() : source ? `refs/heads/${source.branch}` : "";
  const errors = {
    name: NAME_PATTERN.test(name.trim()) ? null : "1 to 100 letters, digits, dots, dashes and underscores, starting with a letter or digit.",
    repo: repoName === "" ? "Pick the repository the trigger syncs." : null,
    repository: kind === "github" && !REPOSITORY_PATTERN.test(repository.trim().replace(/^https:\/\/github\.com\//, "").replace(/\.git$/, ""))
      ? "The GitHub repository whose workflows it trusts, written owner/name."
      : null,
    repositoryId: kind === "github" && repositoryId.trim() !== "" && !/^[0-9]{1,20}$/.test(repositoryId.trim())
      ? "The repository's number (the token's repository_id)."
      : null,
    ref: kind === "github" && effectiveRef === "" ? "The branch or ref a workflow must run on." : null,
  };
  const valid = Object.values(errors).every((e) => e === null);

  const save = useMutation({
    mutationFn: () => {
      const request = {
        name: name.trim(),
        kind,
        repoName,
        description: description.trim() === "" ? null : description.trim(),
        allSchedules,
        schedules: allSchedules ? [] : schedules,
        gitHub: kind === "github"
          ? {
            repository: repository.trim(),
            repositoryId: repositoryId.trim() === "" ? null : repositoryId.trim(),
            ref: effectiveRef,
            workflow: workflow.trim() === "" ? null : workflow.trim(),
            environment: environment.trim() === "" ? null : environment.trim(),
          }
          : null,
        keyExpiresInDays: kind === "key" ? keyDays : null,
      };
      return existing === null ? externalTriggerApi.create(request) : externalTriggerApi.update(existing.id, request);
    },
    onSuccess: (written) => {
      toast.success(existing === null ? `Trigger "${written.trigger.name}" created.` : `Trigger "${written.trigger.name}" saved.`);
      if (existing !== null && written.warnings.length > 0) {
        toast.warning(written.warnings.join(" "));
      }
      void queryClient.invalidateQueries({ queryKey: ["external-triggers"] });
      onSaved(written, existing === null);
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const toggleSchedule = (schedule: string) =>
    setSchedules((current) => current.includes(schedule) ? current.filter((s) => s !== schedule) : [...current, schedule]);
  const addExtra = () => {
    const value = extraSchedule.trim();
    if (value !== "" && !schedules.some((s) => s.toLowerCase() === value.toLowerCase())) {
      setSchedules((current) => [...current, value]);
    }

    setExtraSchedule("");
  };
  const show = (message: string | null) => (touched ? message : null);

  return (
    <Sheet open onOpenChange={(next) => { if (!next && !save.isPending) onClose(); }}>
      <SheetContent className="w-full sm:max-w-xl" data-testid="trigger-sheet">
        <SheetHeader>
          <SheetTitle>{existing === null ? "New external trigger" : `Edit ${existing.name}`}</SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-4 pb-2">
          {save.isError && isApiError(save.error) && <CorrelationError error={save.error} />}

          <div className="flex gap-2">
            <KindOption kind="github" selected={kind === "github"} disabled={existing !== null} onSelect={() => setKind("github")} />
            <KindOption kind="key" selected={kind === "key"} disabled={existing !== null} onSelect={() => setKind("key")} />
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="trigger-name">Name</Label>
              <Input id="trigger-name" value={name} maxLength={100} placeholder="estate-deliver" className="h-8 font-mono text-[13px]"
                onChange={(e) => setName(e.target.value)} data-testid="trigger-name" />
              <FieldError message={show(errors.name)} />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label>Repository it syncs</Label>
              <Select value={repoName} onValueChange={(value) => { setRepoName(value); setSchedules([]); }} disabled={sourcesQuery.isLoading}>
                <SelectTrigger size="sm" className="h-8 w-full" data-testid="trigger-repo">
                  <SelectValue placeholder={sourcesQuery.isLoading ? "Loading..." : "Pick a repository"} />
                </SelectTrigger>
                <SelectContent>
                  {(sourcesQuery.data?.items ?? []).map((s) => (
                    <SelectItem key={s.id} value={s.name}>{s.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <FieldError message={show(errors.repo)} />
            </div>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="trigger-description">What it is for <span className="font-normal text-muted-foreground">(optional)</span></Label>
            <Textarea id="trigger-description" rows={2} maxLength={1000} value={description}
              placeholder="Syncs the estate after every merge to main and runs the nightly delivery."
              onChange={(e) => setDescription(e.target.value)} data-testid="trigger-description" />
          </div>

          <section className="flex flex-col gap-2">
            <div className="flex items-center justify-between gap-3">
              <Label>Schedules it may fire</Label>
              <label className="inline-flex items-center gap-2 text-[13px]">
                <Switch checked={allSchedules} onCheckedChange={setAllSchedules} data-testid="trigger-all-schedules" />
                Every schedule, present and future
              </label>
            </div>
            {!allSchedules && (
              <div className="flex flex-col gap-2 rounded-md border border-border p-2.5">
                {repoName === "" && <p className="text-xs text-muted-foreground">Pick the repository first.</p>}
                {repoName !== "" && schedulesQuery.isLoading && <p className="text-xs text-muted-foreground">Loading its schedules...</p>}
                {repoName !== "" && !schedulesQuery.isLoading && known.length === 0 && (
                  <p className="text-xs text-muted-foreground">{repoName} has no schedules yet; add the names a merge will bring.</p>
                )}
                <div className="grid max-h-48 grid-cols-1 gap-1.5 overflow-y-auto sm:grid-cols-2">
                  {known.map((schedule) => (
                    <label key={schedule} className="flex items-center gap-2 text-[13px]">
                      <Checkbox checked={schedules.some((s) => s.toLowerCase() === schedule.toLowerCase())}
                        onCheckedChange={() => toggleSchedule(schedule)} data-testid={`trigger-schedule-${schedule}`} />
                      <span className="truncate font-mono text-[12px]">{schedule}</span>
                    </label>
                  ))}
                </div>
                {unknownPicked.length > 0 && (
                  <div className="flex flex-wrap gap-1.5">
                    {unknownPicked.map((schedule) => (
                      <span key={schedule} className="inline-flex items-center gap-1 rounded-md border border-dashed border-border px-1.5 py-0.5 font-mono text-[11px]">
                        {schedule}
                        <button type="button" aria-label={`Remove ${schedule}`} onClick={() => toggleSchedule(schedule)}>
                          <X className="size-3" />
                        </button>
                      </span>
                    ))}
                  </div>
                )}
                <div className="flex gap-2">
                  <Input value={extraSchedule} placeholder="A schedule a merge will add" className="h-8 font-mono text-[12px]"
                    onChange={(e) => setExtraSchedule(e.target.value)}
                    onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addExtra(); } }}
                    data-testid="trigger-extra-schedule" />
                  <Button type="button" variant="outline" size="sm" onClick={addExtra} disabled={extraSchedule.trim() === ""}>
                    <Plus />
                    Add
                  </Button>
                </div>
                <p className="text-xs text-muted-foreground">
                  {schedules.length === 0
                    ? "None picked: the trigger only syncs the repository."
                    : `It may fire ${schedules.length === 1 ? "this schedule" : `these ${schedules.length} schedules`}; each request picks among them.`}
                </p>
              </div>
            )}
          </section>

          {kind === "github" && (
            <section className="flex flex-col gap-3">
              <Label>The workflow it trusts</Label>
              <div className="grid gap-4 sm:grid-cols-2">
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="trigger-gh-repository" className="text-xs font-normal text-muted-foreground">Repository</Label>
                  <Input id="trigger-gh-repository" value={repository} placeholder="owner/estate" className="h-8 font-mono text-[13px]"
                    onChange={(e) => setRepository(e.target.value)} data-testid="trigger-gh-repository" />
                  <FieldError message={show(errors.repository)} />
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="trigger-gh-ref" className="text-xs font-normal text-muted-foreground">Branch or ref</Label>
                  <Input id="trigger-gh-ref" value={gitRef} placeholder={source ? `refs/heads/${source.branch}` : "refs/heads/main"}
                    className="h-8 font-mono text-[13px]" onChange={(e) => setGitRef(e.target.value)} data-testid="trigger-gh-ref" />
                  <FieldError message={show(errors.ref)} />
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="trigger-gh-workflow" className="text-xs font-normal text-muted-foreground">Workflow file</Label>
                  <Input id="trigger-gh-workflow" value={workflow} placeholder=".github/workflows/sqlflow.yml" className="h-8 font-mono text-[13px]"
                    onChange={(e) => setWorkflow(e.target.value)} data-testid="trigger-gh-workflow" />
                  <p className="text-xs text-muted-foreground">Name it, so a workflow added later is not trusted with it.</p>
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="trigger-gh-environment" className="text-xs font-normal text-muted-foreground">Environment <span>(optional)</span></Label>
                  <Input id="trigger-gh-environment" value={environment} placeholder="production" className="h-8 font-mono text-[13px]"
                    onChange={(e) => setEnvironment(e.target.value)} data-testid="trigger-gh-environment" />
                  <p className="text-xs text-muted-foreground">The environment's reviewers and branch rules then apply before a job gets in.</p>
                </div>
                <div className="flex flex-col gap-1.5 sm:col-span-2">
                  <Label htmlFor="trigger-gh-repository-id" className="text-xs font-normal text-muted-foreground">Repository id <span>(optional)</span></Label>
                  <Input id="trigger-gh-repository-id" value={repositoryId} placeholder="123456789" className="h-8 w-48 font-mono text-[13px]"
                    onChange={(e) => setRepositoryId(e.target.value)} data-testid="trigger-gh-repository-id" />
                  <FieldError message={show(errors.repositoryId)} />
                  <p className="text-xs text-muted-foreground">
                    Pins the repository, so one deleted and recreated under the same name is not trusted. After the first
                    call, Last used shows the id GitHub stated.
                  </p>
                </div>
              </div>
            </section>
          )}

          {kind === "key" && existing === null && (
            <div className="flex flex-col gap-1.5">
              <Label>Key expiry</Label>
              <Select value={keyDays === null ? "none" : String(keyDays)} onValueChange={(v) => setKeyDays(v === "none" ? null : Number(v))}>
                <SelectTrigger size="sm" className="h-8 w-48" data-testid="trigger-key-expiry"><SelectValue /></SelectTrigger>
                <SelectContent>
                  {KEY_EXPIRY.map((option) => (
                    <SelectItem key={option.label} value={option.days === null ? "none" : String(option.days)}>{option.label}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          )}
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={save.isPending} data-testid="trigger-cancel">Cancel</Button>
          <Button
            size="sm"
            onClick={() => {
              setTouched(true);
              if (valid) {
                save.mutate();
              }
            }}
            disabled={save.isPending}
            data-testid="trigger-submit"
          >
            {save.isPending && <Loader2 className="animate-spin" />}
            {existing === null ? "Create" : "Save"}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
