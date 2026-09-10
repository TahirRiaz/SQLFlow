import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { RotateCcw } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import type { Node } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";

/** Restarts one node: a confirmation, then a request the worker honors on its next heartbeat (drain, then exit) so
 *  the orchestrator recreates it. A pending request shows as a badge instead of the button; the control is only
 *  offered to operators. */
export function NodeRestartButton({ node }: { node: Node }) {
  const { hasScope } = useAuth();
  const queryClient = useQueryClient();
  const [confirmOpen, setConfirmOpen] = useState(false);

  const restart = useMutation({
    mutationFn: () => nodeApi.restart(node.name),
    onSuccess: () => {
      toast.success(`Restart requested for '${node.name}'.`);
      setConfirmOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
      setConfirmOpen(false);
    },
  });

  if (node.restartRequestedUtc !== null) {
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <Badge variant="outline" className="border-warning/50 text-warning" data-testid="node-restart-pending">
            Restart pending
          </Badge>
        </TooltipTrigger>
        <TooltipContent className="max-w-xs">
          The node will drain its in-flight work and restart on its next heartbeat.
        </TooltipContent>
      </Tooltip>
    );
  }

  if (!hasScope("operate")) {
    return null;
  }

  return (
    <>
      <Button
        variant="outline"
        size="xs"
        className="text-warning"
        disabled={!node.online || restart.isPending}
        onClick={() => setConfirmOpen(true)}
        data-testid="node-restart-button"
      >
        <RotateCcw />
        Restart
      </Button>
      <ConfirmDialog
        open={confirmOpen}
        title={`Restart node '${node.name}'?`}
        message="The node stops claiming, drains its in-flight runs, and exits; the orchestrator then recreates it. Runs in progress finish first, so this is safe to do at any time."
        confirmLabel="Restart"
        danger
        busy={restart.isPending}
        onConfirm={() => restart.mutate()}
        onClose={() => setConfirmOpen(false)}
      />
    </>
  );
}
