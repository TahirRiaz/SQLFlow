import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { toast } from "sonner";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { isApiError } from "../../api/client";
import { pipelineApi, repoApi, repoSourceApi, runApi } from "../../api/endpoints";
import type { Repo, RepoSource } from "../../api/types";

/**
 * Confirms and performs an irreversible repo deletion. A repo (its pipelines, runs, lineage, schedules, and the
 * managed git source registered under the same name) is wiped, so the confirm is deliberately heavy: it shows the
 * concrete impact (pipeline and run counts, fetched live) and requires typing the repo's name to arm the button.
 * When the row is a source-only registration (registered but not yet synced, so no repo exists), it deletes that
 * source instead. Callers pass whichever facets the row has; at least one of `repo`/`source` is present.
 */
export function RepoDeleteDialog({
  name, repo, source, onClose,
}: {
  name: string;
  repo?: Repo;
  source?: RepoSource;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [typed, setTyped] = useState("");

  // The concrete impact, fetched live so the operator sees exactly what a delete removes. Only a synced repo has
  // pipelines and runs; a source-only row (pending first sync) has neither, so the counts are skipped for it.
  const pipelineCount = useQuery({
    queryKey: ["pipelines", "count-for-delete", repo?.id],
    queryFn: () => pipelineApi.list({ repoId: repo!.id, page: 1, pageSize: 1 }),
    enabled: repo !== undefined,
  });
  const runCount = useQuery({
    queryKey: ["runs", "count-for-delete", repo?.id],
    queryFn: () => runApi.list({ repoId: repo!.id, page: 1, pageSize: 1 }),
    enabled: repo !== undefined,
  });

  const remove = useMutation({
    mutationFn: () =>
      repo !== undefined
        ? repoApi.delete(repo.id).then(() => undefined)
        : repoSourceApi.remove(source!.id),
    onSuccess: () => {
      toast.success(`Deleted '${name}'.`);
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      void queryClient.invalidateQueries({ queryKey: ["pipelines"] });
      onClose();
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const confirmed = typed.trim() === name;
  const pipelines = pipelineCount.data?.total;
  const runs = runCount.data?.total;

  return (
    <AlertDialog
      open
      onOpenChange={(next) => {
        if (!next && !remove.isPending) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="repo-delete-dialog" className="max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>Delete &lsquo;{name}&rsquo;?</AlertDialogTitle>
          <AlertDialogDescription>
            {repo !== undefined
              ? "This permanently removes the repo and everything attributed to it. This cannot be undone."
              : "This removes the tracked git source. It has not synced yet, so nothing else is affected."}
          </AlertDialogDescription>
        </AlertDialogHeader>

        {repo !== undefined && (
          <ul className="list-disc space-y-1 pl-5 text-[13px] text-muted-foreground" data-testid="repo-delete-impact">
            <li>
              {pipelines === undefined ? "…" : pipelines.toLocaleString()} pipeline{pipelines === 1 ? "" : "s"}
            </li>
            <li>
              {runs === undefined ? "…" : runs.toLocaleString()} run{runs === 1 ? "" : "s"} and their history
            </li>
            <li>lineage (object relationships, edges, waves) and schedules</li>
            {source !== undefined && <li>the managed git source and its sync schedule</li>}
          </ul>
        )}

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="repo-delete-confirm">
            Type <span className="font-mono font-medium text-foreground">{name}</span> to confirm
          </Label>
          <Input
            id="repo-delete-confirm"
            className="h-8 font-mono text-[12px]"
            autoComplete="off"
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            disabled={remove.isPending}
            data-testid="repo-delete-confirm-input"
          />
        </div>

        <AlertDialogFooter>
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={remove.isPending}
            data-testid="repo-delete-cancel"
          >
            Cancel
          </Button>
          <Button
            variant="destructive"
            size="sm"
            onClick={() => remove.mutate()}
            disabled={!confirmed || remove.isPending}
            data-testid="repo-delete-confirm-button"
          >
            {remove.isPending && <Loader2 className="animate-spin" />}
            Delete
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
