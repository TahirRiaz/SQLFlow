import { useCallback, useEffect, useRef, useState } from "react";
import { executeComputeTask } from "../../api/endpoints";
import type { ComputeTaskRequest } from "../../api/types";
import { isApiError } from "../../api/client";

interface ComputeState<T> {
  running: boolean;
  /** The terminal failure/cancel message, or null while running / after success. */
  error: string | null;
  data: T | null;
}

/**
 * One live compute concern (a databases list, an objects page, an introspection): runs a compute task end to
 * end (enqueue + long-poll) and holds its result. Starting a new run aborts the previous one, and aborting
 * also best-effort cancels the server-side task (see executeComputeTask), so a superseded browse or an
 * unmounted page never keeps a worker busy. The generic parameter is the operation's result shape.
 */
export function useCompute<T>() {
  const [state, setState] = useState<ComputeState<T>>({ running: false, error: null, data: null });
  const abortRef = useRef<AbortController | null>(null);

  // Abort the in-flight task when the component unmounts (navigation away mid-browse).
  useEffect(() => () => abortRef.current?.abort(), []);

  const run = useCallback(async (request: ComputeTaskRequest): Promise<T | null> => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setState((current) => ({ running: true, error: null, data: current.data }));

    try {
      const task = await executeComputeTask(request, controller.signal);
      if (controller.signal.aborted) {
        return null; // superseded by a newer run; its state owns the UI now
      }

      if (task.status === "succeeded") {
        const data = task.result as T;
        setState({ running: false, error: null, data });
        return data;
      }

      setState({
        running: false,
        error: task.error ?? (task.status === "cancelled" ? "The task was cancelled." : "The task did not complete."),
        data: null,
      });
      return null;
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return null;
      }

      setState({
        running: false,
        error: isApiError(error) ? error.message : error instanceof Error ? error.message : String(error),
        data: null,
      });
      return null;
    }
  }, []);

  const reset = useCallback(() => {
    abortRef.current?.abort();
    setState({ running: false, error: null, data: null });
  }, []);

  return { ...state, run, reset };
}
