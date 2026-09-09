import { useId, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { isApiError } from "../../api/client";
import { nodeApi } from "../../api/endpoints";
import type { WorkerPool } from "../../api/types";

const poolLabel = (pool: string) => (pool.length === 0 ? "the default pool" : `pool '${pool}'`);

/** Starts workers on demand: pick how many and for how long. Defaults to one worker for 30 minutes (the common
 *  "spawn a worker now" case); a higher count is a temporary scale-up. After the window the pool returns to
 *  autoscaling on its own, so nothing stays warm by accident. */
export function SpawnWorkersDialog({ pool, open, onClose }: { pool: WorkerPool; open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [count, setCount] = useState("1");
  const [minutes, setMinutes] = useState("30");
  const countId = useId();
  const minutesId = useId();

  const spawn = useMutation({
    mutationFn: (input: { count: number; minutes: number }) =>
      nodeApi.scalePool({ pool: pool.pool, manualReplicas: input.count, manualForMinutes: input.minutes }),
    onSuccess: (_result, input) => {
      toast.success(
        `Starting ${input.count} worker${input.count === 1 ? "" : "s"} in ${poolLabel(pool.pool)} for ${input.minutes} min.`,
      );
      void queryClient.invalidateQueries({ queryKey: ["nodes", "pools"] });
      void queryClient.invalidateQueries({ queryKey: ["nodes", "list"] });
      onClose();
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
  });

  const parsedCount = Number.parseInt(count, 10);
  const parsedMinutes = Number.parseInt(minutes, 10);
  const valid = Number.isFinite(parsedCount) && parsedCount >= 1 && Number.isFinite(parsedMinutes) && parsedMinutes >= 1;

  return (
    <Sheet
      open={open}
      onOpenChange={(next) => {
        if (!next && !spawn.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="sm:max-w-xl" data-testid="spawn-dialog">
        <SheetHeader>
          <SheetTitle>Spawn workers</SheetTitle>
          <SheetDescription className="text-[13px]">
            Start workers in {poolLabel(pool.pool)} now, even if nothing is queued. They run for the window you set,
            then the pool goes back to autoscaling on its own.
          </SheetDescription>
        </SheetHeader>
        <div className="grid grid-cols-2 gap-4 px-4">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor={countId} className="text-[13px]">Workers</Label>
            <Input
              id={countId}
              className="h-8"
              inputMode="numeric"
              value={count}
              onChange={(event) => setCount(event.target.value.replace(/[^0-9]/g, ""))}
              data-testid="spawn-count"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor={minutesId} className="text-[13px]">For (minutes)</Label>
            <Input
              id={minutesId}
              className="h-8"
              inputMode="numeric"
              value={minutes}
              onChange={(event) => setMinutes(event.target.value.replace(/[^0-9]/g, ""))}
              data-testid="spawn-minutes"
            />
          </div>
        </div>
        <SheetFooter className="flex-row justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={spawn.isPending}>
            Cancel
          </Button>
          <Button
            size="sm"
            disabled={!valid || spawn.isPending}
            onClick={() => spawn.mutate({ count: parsedCount, minutes: parsedMinutes })}
            data-testid="spawn-confirm"
          >
            {spawn.isPending && <Loader2 className="animate-spin" />}
            {valid && parsedCount === 1 ? "Spawn 1 worker" : valid ? `Spawn ${parsedCount} workers` : "Spawn"}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
