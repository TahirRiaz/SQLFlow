import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";

/** Clears every offline node out of the fleet registry in one go. The list accumulates a dead row per worker pod
 *  (each revision, each autoscale-up registers under a fresh name), so deleting them one at a time is tedious; this
 *  is the bulk form of {@link NodeDeleteButton}, offered only to operators. */
export function PurgeOfflineNodesButton() {
  const { hasScope } = useAuth();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const purge = useMutation({
    mutationFn: () => nodeApi.purgeOffline(),
    onSuccess: (result) => {
      toast.success(
        result.removed === 0
          ? "No offline nodes to purge."
          : `Removed ${result.removed} offline node${result.removed === 1 ? "" : "s"} from the fleet.`,
      );
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
      setConfirmOpen(false);
    },
  });

  if (!hasScope("operate")) {
    return null;
  }

  return (
    <>
      <Button
        variant="outline"
        size="sm"
        disabled={purge.isPending}
        onClick={() => setConfirmOpen(true)}
        data-testid="nodes-purge-offline-button"
      >
        <Trash2 />
        Purge offline
      </Button>
      <ConfirmDialog
        open={confirmOpen}
        title="Purge all offline nodes?"
        message="This drops every node that has not heartbeated within the last minute from the fleet registry. Those entries carry no state, so removal is safe; a node that is somehow still alive re-registers on its next heartbeat."
        confirmLabel="Purge"
        danger
        busy={purge.isPending}
        onConfirm={() => purge.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
    </>
  );
}
