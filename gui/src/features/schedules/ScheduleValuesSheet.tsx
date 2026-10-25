import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { SlidersHorizontal, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { scheduleApi } from "../../api/endpoints";
import type { RunValueOrigin, Schedule, ScheduleMemberParameters } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { memberValuesLine, originLabel, refusedMembers, scheduleValueAssignments, valueText } from "./scheduleValues";

/** The tone a value's origin is shown in: a refusal is an error, a document's own value is quiet, the schedule's is plain. */
function originTone(origin: RunValueOrigin): string {
  return origin === "Missing" || origin === "NotDeclared"
    ? "text-destructive"
    : origin === "Default" || origin === "Unset" ? "text-muted-foreground" : "text-foreground";
}

/** A member's values as one line, with a mark when its run would be refused: what Run now shows on each member's row. */
export function ScheduleMemberValuesLine({ member }: { member: ScheduleMemberParameters }) {
  const line = memberValuesLine(member);
  const refused = member.problems.length > 0;
  return (
    <div className="flex min-w-0 items-center gap-1 text-xs text-muted-foreground" data-testid="schedule-member-values">
      {refused && (
        <span className="flex shrink-0 text-destructive" title={member.problems.join("\n")} data-testid="schedule-member-refused">
          <TriangleAlert className="size-3.5" />
        </span>
      )}
      <span className="min-w-0 truncate font-mono" title={[line, ...member.problems].join("\n")}>{line}</span>
    </div>
  );
}

/**
 * What every flow a schedule runs is given when it fires, so the values a schedule runs with are visible before it fires
 * rather than only on its runs afterwards. Read as a fire applies the schedule: its operation and values reach the flows
 * whose kind takes them, every flow's own document gives the rest (its defaults, its settings), and what would refuse a
 * flow's run is named on it.
 */
export function ScheduleValuesSheet({ schedule, onClose }: { schedule: Schedule; onClose: () => void }) {
  const preview = useQuery({
    queryKey: ["schedules", "parameters", schedule.id],
    queryFn: () => scheduleApi.parameters(schedule.id),
  });
  const data = preview.data;
  const refused = data === undefined ? 0 : refusedMembers(data.members);
  const assignments = scheduleValueAssignments(schedule.values);

  return (
    <Sheet open onOpenChange={(next) => !next && onClose()}>
      <SheetContent className="w-full gap-0 sm:max-w-3xl" data-testid="schedule-values-sheet">
        <SheetHeader className="border-b">
          <SheetTitle className="flex items-center gap-2">
            <SlidersHorizontal className="size-4 text-muted-foreground" />
            <span className="font-mono text-[13px]">{schedule.name}</span>
          </SheetTitle>
          <SheetDescription>
            What each flow is given when this schedule fires: its operation and values for the flows whose kind takes them,
            and each flow&apos;s own defaults for the rest.
          </SheetDescription>
        </SheetHeader>

        <div className="flex flex-1 flex-col gap-3 overflow-y-auto p-4">
          <div className="flex flex-wrap items-center gap-1.5" data-testid="schedule-values-own">
            <span className="text-[12px] text-muted-foreground">The schedule gives:</span>
            {schedule.operation !== null && (
              <Badge variant="outline" className="font-mono text-[11px]">{`operation ${schedule.operation}`}</Badge>
            )}
            {assignments.map((assignment) => (
              <Badge key={assignment} variant="outline" className="font-mono text-[11px]">{assignment}</Badge>
            ))}
            {schedule.operation === null && assignments.length === 0 && (
              <span className="text-[12px]">no operation and no values; every flow runs with its own defaults.</span>
            )}
          </div>

          {preview.isLoading && <Skeleton className="h-64 w-full" />}
          {preview.isError && (
            isApiError(preview.error)
              ? <CorrelationError error={preview.error} />
              : <p className="text-[13px] text-destructive">{String(preview.error)}</p>
          )}

          {data !== undefined && refused > 0 && (
            <Alert className="text-destructive" data-testid="schedule-values-refused">
              <TriangleAlert />
              <AlertDescription className="text-destructive/90">
                {`${refused} of ${data.members.length} ${data.members.length === 1 ? "flow" : "flows"} would be refused when this schedule fires; each says why below.`}
              </AlertDescription>
            </Alert>
          )}

          {data !== undefined && data.members.length === 0 && (
            <p className="text-[13px] text-muted-foreground">No runnable flow joins this schedule, so a fire runs nothing.</p>
          )}

          {data?.members.map((member) => (
            <section
              key={member.flowName}
              className={cn("rounded-md border", member.problems.length > 0 && "border-destructive/50")}
              data-testid="schedule-values-member"
            >
              <header className="flex flex-wrap items-center gap-2 border-b bg-muted/40 px-3 py-2">
                <span className="text-[11px] text-muted-foreground">{`wave ${member.wave + 1}`}</span>
                {member.pipelineId !== null
                  ? (
                    <RouterLink to={`/pipelines/${member.pipelineId}`} onClick={onClose} className="font-mono text-[12px] font-medium text-primary hover:underline">
                      {member.flowName}
                    </RouterLink>
                  )
                  : <span className="font-mono text-[12px] font-medium">{member.flowName}</span>}
                <Badge variant="secondary" className="text-[11px]">{member.flowKind}</Badge>
                {member.operation !== null && (
                  <Badge variant="outline" className="font-mono text-[11px]">{member.operation}</Badge>
                )}
              </header>

              <div className="flex flex-col gap-2 px-3 py-2">
                {member.note !== null && <p className="text-[12px] text-muted-foreground" data-testid="schedule-values-note">{member.note}</p>}
                {member.problems.length > 0 && (
                  <ul className="flex flex-col gap-0.5" data-testid="schedule-values-problems">
                    {member.problems.map((problem) => (
                      <li key={problem} className="text-[12px] font-medium text-destructive">{problem}</li>
                    ))}
                  </ul>
                )}

                {member.values.length === 0 && member.settings.length === 0
                  ? <p className="text-[12px] text-muted-foreground">Its document declares no parameters: it runs as defined.</p>
                  : (
                    <Table className="text-[12px]">
                      <TableHeader>
                        <TableRow>
                          <TableHead className="h-7">Parameter</TableHead>
                          <TableHead className="h-7">Value</TableHead>
                          <TableHead className="h-7">From</TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {member.values.map((value) => {
                          const declared = member.parameters.find((parameter) => parameter.name === value.name);
                          return (
                            <TableRow key={`value-${value.name}`} data-testid="schedule-values-row" data-origin={value.origin}>
                              <TableCell className="py-1 font-mono" title={declared?.description ?? undefined}>{value.name}</TableCell>
                              <TableCell className="py-1 font-mono break-all">{valueText(value.value)}</TableCell>
                              <TableCell className={cn("py-1", originTone(value.origin))} title={value.problem ?? undefined}>
                                {originLabel(value.origin)}
                              </TableCell>
                            </TableRow>
                          );
                        })}
                        {member.settings.map((setting) => (
                          <TableRow key={`setting-${setting.key}`} data-testid="schedule-values-setting">
                            <TableCell className="py-1" title={setting.help}>{setting.label}</TableCell>
                            <TableCell className="py-1 font-mono">{setting.documentValue ?? "-"}</TableCell>
                            <TableCell className="py-1 text-muted-foreground">
                              {setting.documentValue !== null && setting.documentValue !== undefined
                                ? `the flow's${setting.documentKey ? ` ${setting.documentKey}` : ""}`
                                : `not set${setting.whenUnset ? `: ${setting.whenUnset}` : ""}`}
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  )}
              </div>
            </section>
          ))}
        </div>
      </SheetContent>
    </Sheet>
  );
}
