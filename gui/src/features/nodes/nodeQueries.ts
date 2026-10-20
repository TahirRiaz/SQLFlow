import { useQuery } from "@tanstack/react-query";
import { dispatchApi, nodeApi } from "../../api/endpoints";

/** How often the Nodes page reads the fleet again. */
const pollMs = 5000;

/** Each pool's desired state and its resolved target. The page's summary and the pools panel read it under one key. */
export function useWorkerPools() {
  return useQuery({ queryKey: ["nodes", "pools"], queryFn: () => nodeApi.listPools(), refetchInterval: pollMs });
}

/** The managed fleet's state; `enabled: false` when managed workers are off. */
export function useManagedWorkers() {
  return useQuery({ queryKey: ["nodes", "managed"], queryFn: () => nodeApi.managed(), refetchInterval: pollMs });
}

/** The dispatcher's own view: the queue with every gate, the leases, and the nodes it has heard from. */
export function useDispatchSnapshot() {
  return useQuery({ queryKey: ["dispatch", "snapshot"], queryFn: () => dispatchApi.snapshot(), refetchInterval: pollMs });
}
