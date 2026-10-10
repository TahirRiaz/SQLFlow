import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { semanticLayerApi } from "../../api/endpoints";
import type { SemanticMeasureAdmin } from "../../api/types";
import { LayerTablePicker, type PickedTable } from "./LayerTablePicker";
import { errorText, refreshSemanticLayer, SEMANTIC_ROOT, textOrNull } from "./shared";

const MEASURE_NAME = /^[A-Za-z_][A-Za-z0-9_]{0,127}$/;

/**
 * Creates or edits one measure: a named SQL expression anchored to the table whose columns it reads. The anchor's
 * allowed columns are listed as insertable chips, since those are the only columns the server accepts in the
 * expression; everything else about validity (one scalar expression, no subquery, allow-listed columns) is decided
 * by the server and its refusal is shown here verbatim.
 */
export function MeasureDialog({
  measure, anchor, onClose,
}: {
  measure: SemanticMeasureAdmin | null;
  /** The table the measure is created from; fixes the anchor when set. */
  anchor: PickedTable | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(measure?.name ?? "");
  const [table, setTable] = useState<PickedTable | null>(
    measure !== null
      ? { key: measure.objectKey, name: measure.objectName, database: null, schema: null }
      : anchor,
  );
  const [expression, setExpression] = useState(measure?.expression ?? "");
  const [description, setDescription] = useState(measure?.description ?? "");

  const anchorColumns = useQuery({
    queryKey: [SEMANTIC_ROOT, "object", table?.key],
    queryFn: () => semanticLayerApi.object(table!.key),
    enabled: table !== null,
  });
  const allowed = (anchorColumns.data?.columns ?? []).filter((column) => column.isAllowed);

  const save = useMutation({
    mutationFn: () => {
      const request = {
        name: name.trim(),
        objectKey: table!.key,
        expression: expression.trim(),
        description: textOrNull(description),
      };
      return measure === null
        ? semanticLayerApi.createMeasure(request)
        : semanticLayerApi.updateMeasure(measure.id, request);
    },
    onSuccess: (saved) => {
      toast.success(`Measure ${saved.name} saved.`);
      refreshSemanticLayer(queryClient);
      onClose();
    },
  });

  const nameValid = MEASURE_NAME.test(name.trim());
  const canSave = nameValid && table !== null && expression.trim() !== "" && !save.isPending;

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && !save.isPending) {
          onClose();
        }
      }}
    >
      <DialogContent className="sm:max-w-xl" data-testid="semantic-measure-dialog">
        <DialogHeader>
          <DialogTitle>{measure === null ? "New measure" : `Edit ${measure.name}`}</DialogTitle>
          <DialogDescription>
            A named SQL expression the assistant reuses verbatim, for example SUM(Amount) - SUM(RefundAmount).
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          {save.isError && (
            <p className="text-[13px] text-destructive" data-testid="semantic-measure-error">{errorText(save.error)}</p>
          )}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="semantic-measure-name">Name</Label>
            <Input
              id="semantic-measure-name"
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder="net_revenue"
              aria-invalid={(name !== "" && !nameValid) || undefined}
              className="h-8 font-mono text-[12px]"
              data-testid="semantic-measure-name"
            />
            <p className="text-xs text-muted-foreground">Letters, digits, and underscores; unique across the layer.</p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label>Table</Label>
            <LayerTablePicker value={table} onChange={setTable} locked={anchor !== null} testId="semantic-measure-table" />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="semantic-measure-expression">Expression</Label>
            <Textarea
              id="semantic-measure-expression"
              value={expression}
              onChange={(event) => setExpression(event.target.value)}
              placeholder="SUM(Amount)"
              className="min-h-24 font-mono text-[12px]"
              data-testid="semantic-measure-expression"
            />
            {allowed.length > 0 && (
              <div className="flex flex-wrap gap-1" data-testid="semantic-measure-columns">
                {allowed.map((column) => (
                  <Button
                    key={column.columnName}
                    variant="outline"
                    size="xs"
                    className="font-mono text-[11px]"
                    onClick={() => setExpression((current) => `${current}${current === "" || current.endsWith(" ") || current.endsWith("(") ? "" : " "}${column.columnName}`)}
                  >
                    {column.columnName}
                  </Button>
                ))}
              </div>
            )}
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="semantic-measure-description">Description</Label>
            <Input
              id="semantic-measure-description"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              placeholder="What the number means and when to use it"
              className="h-8"
              data-testid="semantic-measure-description"
            />
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={save.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => save.mutate()} disabled={!canSave} data-testid="semantic-measure-save">
            {save.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
