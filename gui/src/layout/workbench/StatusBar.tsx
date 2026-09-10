import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { CircleCheck, Clock3, Loader2, TriangleAlert, User } from "lucide-react";
import { runApi } from "../../api/endpoints";
import { useAuth } from "../../auth/AuthContext";
import { pollingInterval, useRateLimitPause } from "../../hooks/usePolling";

/** One page-1 probe per state: the PagedResult total is the count, the items are discarded. */
function useRunCount(status: "running" | "queued"): number | undefined {
  const { data } = useQuery({
    queryKey: ["status-bar", "runs", status],
    queryFn: () => runApi.list({ status, pageSize: 1 }),
    refetchInterval: pollingInterval(15_000),
    refetchOnWindowFocus: false,
  });
  return data?.total;
}

/**
 * The workbench status bar (DESIGN.md section 6): live workload on the left (running and queued run
 * counts, click-through to the filtered Runs page, plus the rate-limit pause), identity on the right.
 */
export function StatusBar() {
  const { session } = useAuth();
  const navigate = useNavigate();
  const rateLimitedUntil = useRateLimitPause();
  const running = useRunCount("running");
  const queued = useRunCount("queued");

  return (
    <footer className="flex h-[22px] shrink-0 select-none items-stretch gap-0.5 bg-status-bar px-1 text-[11px] text-status-bar-foreground">
      <button
        onClick={() => navigate("/runs?status=running")}
        className="flex items-center gap-1 px-2 hover:bg-white/15"
        aria-label="Show running runs"
      >
        {running !== undefined && running > 0
          ? (
            <>
              <Loader2 className="size-3.5 animate-spin" />
              {running} running
            </>
          )
          : (
            <>
              <CircleCheck className="size-3.5" />
              No active runs
            </>
          )}
      </button>
      {queued !== undefined && queued > 0 && (
        <button
          onClick={() => navigate("/runs?status=queued")}
          className="flex items-center gap-1 px-2 hover:bg-white/15"
          aria-label="Show queued runs"
        >
          <Clock3 className="size-3.5" />
          {queued} queued
        </button>
      )}
      {rateLimitedUntil !== null && (
        <span
          data-testid="rate-limit-banner"
          className="flex items-center gap-1 bg-warning px-2 text-warning-foreground"
        >
          <TriangleAlert className="size-3.5" />
          Rate limited; live updates resume shortly
        </span>
      )}
      <span className="flex-1" />
      <span className="flex items-center gap-1 px-2">
        <User className="size-3.5" />
        {session?.subject}
        {session?.role ? ` (${session.role})` : ""}
      </span>
    </footer>
  );
}
