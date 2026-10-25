import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { pipelineApi } from "../../api/endpoints";
import type { RunValue } from "../../api/types";
import { formProblems, problemsByParameter } from "./runInputs";

/** How long a form rests before the control plane is asked what a run with its inputs would take. */
export const EXPLAIN_DELAY_MS = 300;

/**
 * What a run of the pipeline may be given (GET /pipelines/{id}/parameters): the built-in run controls that apply, and for a
 * registered kind's flow what its document declares. One query per flow, which the run form, a Preview tab and a value
 * check share.
 */
export function usePipelineParameters(pipelineId: string | null, enabled = true) {
  return useQuery({
    queryKey: ["pipeline-parameters", pipelineId],
    queryFn: () => pipelineApi.parameters(pipelineId!),
    enabled: enabled && pipelineId !== null && pipelineId !== "",
  });
}

/**
 * What a run of the pipeline with a form's operation, values and payload would take for each parameter, and what would
 * refuse it, as the control plane checks a trigger (POST /pipelines/{id}/parameters/explain): asked once the form rests,
 * so the fields say what is wrong as they are filled in, and never judged by the form itself. `refused` is true once the
 * answer for what the form holds names a problem; `explaining` while that answer is on its way.
 */
export function useRunExplanation(
  pipelineId: string | null,
  operation: string | null,
  values: Readonly<Record<string, RunValue>>,
  payload: Readonly<Record<string, unknown>>,
  enabled: boolean,
) {
  const body = useMemo(() => JSON.stringify({ operation, values, payload }), [operation, values, payload]);
  const [resting, setResting] = useState(body);
  useEffect(() => {
    const timer = setTimeout(() => setResting(body), EXPLAIN_DELAY_MS);
    return () => clearTimeout(timer);
  }, [body]);

  const asks = enabled && pipelineId !== null;
  const explained = useQuery({
    queryKey: ["pipeline-explain", pipelineId, resting],
    queryFn: () => pipelineApi.explain(pipelineId!, JSON.parse(resting) as Parameters<typeof pipelineApi.explain>[1]),
    enabled: asks,
    staleTime: 30_000,
  });
  const explaining = asks && (resting !== body || explained.isFetching);
  const parameterProblems = useMemo(() => problemsByParameter(explained.data?.values ?? []), [explained.data]);
  const inputProblems = useMemo(
    () => (explained.data === undefined ? [] : formProblems(explained.data.problems, explained.data.values)),
    [explained.data],
  );
  const refused = asks && !explaining && (explained.data?.problems.length ?? 0) > 0;
  return { explained, explaining, parameterProblems, inputProblems, refused };
}
