import { Loader2 } from "lucide-react";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  message: string;
  confirmLabel: string;
  danger?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}

/**
 * The one confirmation dialog for destructive/irreversible actions (cancel run, delete schedule,
 * deactivate). Per DESIGN.md 7.4 this is one of the two legitimate modal surfaces; while busy, the
 * dialog cannot be dismissed and the confirm button shows the pending spinner (DESIGN.md 8.1).
 */
export function ConfirmDialog({ open, title, message, confirmLabel, danger, busy, onConfirm, onClose }: ConfirmDialogProps) {
  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next && !busy) {
          onClose();
        }
      }}
    >
      <AlertDialogContent data-testid="confirm-dialog" className="max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription>{message}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy} data-testid="confirm-dialog-cancel">
            Cancel
          </Button>
          <Button
            variant={danger ? "destructive" : "default"}
            size="sm"
            onClick={onConfirm}
            disabled={busy}
            data-testid="confirm-dialog-confirm"
          >
            {busy && <Loader2 className="animate-spin" />}
            {confirmLabel}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
