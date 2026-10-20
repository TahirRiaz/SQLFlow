import { SummaryStrip, type SummaryCell } from "../../components/SummaryStrip";
import { useDispatchSnapshot, useWorkerPools } from "./nodeQueries";
import { scalingModeLabel, scalingModeOf } from "./scalingMode";

/** The Nodes page's tabs, which the summary's cells lead to. */
export type NodesTab = "workers" | "scaling" | "queue" | "activity";

const sum = (values: readonly number[]) => values.reduce((total, value) => total + value, 0);

/**
 * The four facts the fleet is read by, on one line above the tabs: how many workers are online, what is running against
 * the slots they offer, what is queued and how much of it could start now, and how workers are started. Each cell opens
 * the tab that explains its number, so the page answers "is anything wrong" before anyone opens a table.
 */
export function NodesSummary({ onOpen }: { onOpen: (tab: NodesTab) => void }) {
  const pools = useWorkerPools().data ?? [];
  const dispatch = useDispatchSnapshot().data;

  const online = sum(pools.map((pool) => pool.onlineNodes));
  const busy = sum(pools.map((pool) => pool.busyNodes));
  const queued = sum(pools.map((pool) => pool.queuedRuns));
  const eligible = sum(pools.map((pool) => pool.eligibleQueuedRuns));
  // Runs and slots are the dispatcher's own counts, which only the replica that owns dispatch holds.
  const owner = dispatch?.active === true ? dispatch : null;
  const running = owner?.leasedRuns.length ?? null;
  const slots = owner === null ? null : sum(owner.nodes.filter((node) => node.online).map((node) => node.runSlots));
  const stranded = queued > 0 && online === 0;

  const mode = scalingModeOf(pools);
  const failing = pools.find((pool) => pool.managed !== null && pool.managed.lastError !== null)?.managed ?? null;
  const managedDefault = pools.find((pool) => pool.pool.length === 0)?.managed ?? null;

  const cells: SummaryCell[] = [
    {
      label: "Workers online",
      value: online,
      caption: busy > 0 ? `${busy} busy` : "none busy",
      tone: stranded ? "warning" : undefined,
      onClick: () => onOpen("workers"),
      testId: "nodes-summary-online",
    },
    {
      label: "Running",
      value: running ?? "-",
      caption: slots === null ? "counted by the dispatch owner" : `of ${slots} run slots online`,
      onClick: () => onOpen("queue"),
      testId: "nodes-summary-running",
    },
    {
      label: "Queued",
      value: queued,
      caption: queued === 0 ? "nothing waiting" : eligible > 0 ? `${eligible} can start now` : "all waiting on a gate",
      tone: stranded ? "warning" : undefined,
      onClick: () => onOpen("queue"),
      testId: "nodes-summary-queued",
    },
    {
      label: "Workers start",
      value: scalingModeLabel(mode),
      caption: mode.kind === "managed"
        ? failing !== null
          ? `starts failing (${failing.consecutiveFailures} in a row)`
          : managedDefault !== null
            ? `${managedDefault.minWorkers} to ${managedDefault.maxWorkers} workers, ${mode.launcher} launcher`
            : `${mode.launcher} launcher`
        : mode.kind === "built-in"
          ? "nothing here starts workers"
          : `target ${sum(pools.map((pool) => pool.replicaTarget))}`,
      tone: failing !== null ? "warning" : undefined,
      onClick: () => onOpen("scaling"),
      testId: "nodes-summary-scaling",
    },
  ];

  return <SummaryStrip cells={cells} data-testid="nodes-summary" />;
}
