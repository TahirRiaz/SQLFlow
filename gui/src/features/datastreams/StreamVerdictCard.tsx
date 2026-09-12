import { useQuery } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { isApiError } from "../../api/client";
import { dataStreamApi } from "../../api/endpoints";
import type { DataStream } from "../../api/types";
import { evidence, finding } from "./streamPresentation";
import { StreamSparkline } from "./StreamSparkline";
import { StreamStatusBadge } from "./StreamStatusBadge";

interface StreamVerdictCardProps {
  pipelineId: string;
  /** The flow the verdict belongs to, shown only when the surrounding panel is not already about that flow:
   * an object's data stream is really the stream of whichever flow writes it, and hiding whose verdict it is
   * would make a table look like it judges itself. */
  caption?: string;
  /** The verdict when the caller has already fetched it (a board sweep it made for other reasons). Given one,
   * the card renders it at once and asks for nothing; the board's row and this card carry the same fields, so
   * there is nothing to fetch a second time. */
  known?: DataStream;
  windowDays: number;
  includeBackfills: boolean;
  /** Open the full stream (chart, detectors, learned pattern) for this flow. */
  onOpen: (pipelineId: string, flowName: string) => void;
}

/**
 * One stream's verdict in the width of a side panel: is data still arriving here, and how sure are we. It is
 * the board row's content minus the grid, reusing the same badge, wording and sparkline so a verdict read
 * beside a lineage node is recognisably the same verdict as the one on the data-streams board.
 *
 * It reads the SAME window and backfill preferences the board stores, so the two surfaces can never show one
 * table two different answers because they quietly measured different spans of history.
 */
export function StreamVerdictCard({
  pipelineId, caption, known, windowDays, includeBackfills, onOpen,
}: StreamVerdictCardProps) {
  const query = useQuery({
    queryKey: ["datastreams", "detail", pipelineId, windowDays, includeBackfills],
    queryFn: () => dataStreamApi.get(pipelineId, windowDays, includeBackfills),
    enabled: known === undefined,
  });

  const stream = known ?? query.data;

  return (
    <div className="rounded-md border border-border bg-muted/30 p-2" data-testid="stream-verdict-card">
      {caption !== undefined && (
        <div className="mb-1 truncate font-mono text-[11px] text-muted-foreground" title={caption}>
          written by {caption}
        </div>
      )}

      {known === undefined && query.isPending && <Skeleton className="h-12 rounded" />}

      {/* A flow with no runs in the window is not a fault and not an error: there is simply nothing to judge
          it on yet, and saying so is more use than an error code the reader has to interpret. */}
      {known === undefined && query.isError && (
        <p className="text-[11px] leading-4 text-muted-foreground">
          {isApiError(query.error) && query.error.status === 404
            ? `No runs in the last ${windowDays} days, so there is nothing to check yet.`
            : "The stream verdict could not be loaded."}
        </p>
      )}

      {stream !== undefined && (
        <>
          <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5">
            <StreamStatusBadge status={stream.status} severity={stream.severity} />
            <span className="text-[11px] text-muted-foreground">{finding(stream)}</span>
          </div>
          {/* How much to trust it, in the same words the detail sheet uses: a verdict a reader cannot
              weigh is a verdict they either over-trust or ignore. */}
          <p className="mt-0.5 text-[11px] leading-4 text-muted-foreground/80">{evidence(stream)}</p>
          <div className="mt-1.5">
            <StreamSparkline sparkline={stream.sparkline} width={260} height={24} />
          </div>
          <Button
            variant="ghost"
            size="xs"
            className="mt-1 px-1"
            onClick={() => onOpen(stream.pipelineId, stream.flowName)}
            data-testid="stream-verdict-open"
          >
            Open data stream
          </Button>
        </>
      )}
    </div>
  );
}
