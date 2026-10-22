import { useMutation } from "@tanstack/react-query";
import { repoSourceApi } from "../../api/endpoints";
import type { CheckRepoAccessRequest } from "../../api/types";

/**
 * Reads a remote's branch tip with the credential a source naming the given reference would use, through the same path
 * a sync takes: the check behind every "Test access" button. Nothing is cloned and nothing reaches the catalog.
 */
export function useRepoAccessCheck() {
  return useMutation({ mutationFn: (request: CheckRepoAccessRequest) => repoSourceApi.checkAccess(request) });
}
