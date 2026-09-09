import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ExternalLink, FileCode2 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { scheduleApi } from "../../api/endpoints";
import type { Schedule } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";

/**
 * The YAML behind one schedule, so the cadence is readable where it is operated instead of only in git. A schedule
 * declared by an inline `schedule:` block shows its declaring flow's document (the catalog's secret-redacted copy);
 * one declared in a `schedules.yaml` shows that library file. An API-created schedule has no file behind it and says
 * so rather than showing a reconstruction that git does not contain.
 */
export function ScheduleDefinitionSheet({ schedule, onClose }: { schedule: Schedule; onClose: () => void }) {
  const definition = useQuery({
    queryKey: ["schedules", "definition", schedule.id],
    queryFn: () => scheduleApi.definition(schedule.id),
  });

  const data = definition.data;

  return (
    <Sheet open onOpenChange={(next) => !next && onClose()}>
      <SheetContent className="w-full gap-0 sm:max-w-3xl" data-testid="schedule-definition-sheet">
        <SheetHeader className="border-b">
          <SheetTitle className="flex items-center gap-2">
            <FileCode2 className="size-4 text-muted-foreground" />
            <span className="font-mono text-[13px]">{schedule.name}</span>
            <Badge variant="outline">{schedule.source}</Badge>
          </SheetTitle>
          <SheetDescription>
            {data?.flowName !== null && data?.flowName !== undefined
              ? `Declared by an inline schedule: block on ${data.flowName}.`
              : data?.path
                ? "Declared in a shared schedule library file."
                : "Where this schedule's cadence is declared."}
          </SheetDescription>
        </SheetHeader>

        <div className="flex flex-1 flex-col gap-3 overflow-y-auto p-4">
          {definition.isLoading && <Skeleton className="h-[520px] w-full" />}

          {definition.isError && (
            isApiError(definition.error)
              ? <CorrelationError error={definition.error} />
              : <p className="text-[13px] text-destructive">{String(definition.error)}</p>
          )}

          {data && (
            <>
              <div className="flex items-center justify-between gap-2">
                <Mono>{data.path ?? "no file"}</Mono>
                {data.pipelineId !== null && (
                  <Button variant="ghost" size="xs" asChild data-testid="schedule-definition-flow-link">
                    <RouterLink to={`/pipelines/${data.pipelineId}`} onClick={onClose}>
                      Open flow
                      <ExternalLink />
                    </RouterLink>
                  </Button>
                )}
              </div>

              {data.yaml !== null ? (
                <CodeView value={data.yaml} language="yaml" height={560} lsp data-testid="schedule-definition-yaml" />
              ) : (
                <EmptyState
                  title="No YAML behind this schedule"
                  description={data.source === "api"
                    ? "This schedule was created through the control plane, not declared in git. Add a schedule: block to a flow (or a schedules.yaml entry) to version it."
                    : data.path === null
                      ? "This schedule is declared in git, but the catalog has not recorded which file since the last sync. Sync the repo to fill it in."
                      : "The flow that declares this schedule has left the estate, so its document is no longer in the catalog."}
                />
              )}
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
