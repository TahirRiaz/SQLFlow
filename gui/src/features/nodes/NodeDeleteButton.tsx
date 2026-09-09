import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import type { Node } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";

/** Removes a dead node from the fleet registry. Offered only for offline nodes (a live node would just re-register
 *  on its next heartbeat) and only to operators. The 24h prune clears these automatically; this is the on-demand
 *  removal. */
export function NodeDeleteButton({ node }: { node: Node }) {
  const { hasScope } = useAuth();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const remove = useMutation({
    mutationFn: () => nodeApi.delete(node.name),
    onSuccess: () => {
      toast.success(`Removed '${node.name}' from the fleet.`);
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
      setConfirmOpen(false);
    },
  });

  // A live node re-registers on its next heartbeat, so deleting it is pointless; offer removal only for dead entries.
  if (node.online || !hasScope("operate")) {
    return null;
  }

  return (
    <>
      <Button
        variant="outline"
        size="xs"
        disabled={remove.isPending}
        onClick={() => setConfirmOpen(true)}
        data-testid="node-delete-button"
      >
        <Trash2 />
        Delete
      </Button>
      <ConfirmDialog
        open={confirmOpen}
        title={`Remove node '${node.name}'?`}
        message="This drops the dead node's entry from the fleet registry. It carries no state, so removal is safe; if the node is somehow still alive it re-registers on its next heartbeat."
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
    </>
  );
}
