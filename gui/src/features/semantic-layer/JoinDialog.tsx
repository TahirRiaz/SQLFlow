import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { semanticLayerApi } from "../../api/endpoints";
import { LayerTablePicker, type PickedTable } from "./LayerTablePicker";
import { errorText, qualifiedName, refreshSemanticLayer, SEMANTIC_ROOT, textOrNull } from "./shared";

/** A curated join being created (id null) or edited. */
export interface JoinDraft {
  id: number | null;
  from: PickedTable;
  to: PickedTable | null;
  pairs: { from: string; to: string }[];
  joinType: string;
  description: string;
}

/** The allowed column names of one table, for the pair selects. */
function useAllowedColumns(key: string | null) {
  const query = useQuery({
    queryKey: [SEMANTIC_ROOT, "object", key],
    queryFn: () => semanticLayerApi.object(key!),
    enabled: key !== null,
  });
  return (query.data?.columns ?? []).filter((column) => column.isAllowed).map((column) => column.columnName);
}

function ColumnSelect({
  value, options, onChange, label, testId,
}: {
  value: string;
  options: string[];
  onChange: (value: string) => void;
  label: string;
  testId: string;
}) {
  return (
    <Select value={value === "" ? undefined : value} onValueChange={onChange}>
      <SelectTrigger size="sm" className="h-8 w-full font-mono text-[12px]" aria-label={label} data-testid={testId}>
        <SelectValue placeholder="Column" />
      </SelectTrigger>
      <SelectContent>
        {options.map((option) => (
          <SelectItem key={option} value={option} className="font-mono text-[12px]">{option}</SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/**
 * Declares or edits one join between two semantic layer tables, as column pairs matched by position. Only allowed
 * columns are offered on either side, matching what the server accepts. The declared direction is kept as written:
 * editing from the other table's page still edits the same from/to relationship.
 */
export function JoinDialog({ draft, onClose }: { draft: JoinDraft; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [to, setTo] = useState<PickedTable | null>(draft.to);
  const [pairs, setPairs] = useState(draft.pairs.length > 0 ? draft.pairs : [{ from: "", to: "" }]);
  const [joinType, setJoinType] = useState(draft.joinType);
  const [description, setDescription] = useState(draft.description);

  const fromColumns = useAllowedColumns(draft.from.key);
  const toColumns = useAllowedColumns(to?.key ?? null);

  const save = useMutation({
    mutationFn: () => {
      const request = {
        fromObjectKey: draft.from.key,
        fromColumns: pairs.map((pair) => pair.from),
        toObjectKey: to!.key,
        toColumns: pairs.map((pair) => pair.to),
        joinType,
        description: textOrNull(description),
      };
      return draft.id === null
        ? semanticLayerApi.createRelationship(request)
        : semanticLayerApi.updateRelationship(draft.id, request);
    },
    onSuccess: (saved) => {
      toast.success(`Relationship ${saved.fromObjectName} to ${saved.toObjectName} saved.`);
      refreshSemanticLayer(queryClient);
      onClose();
    },
  });

  const setPair = (index: number, side: "from" | "to", value: string) =>
    setPairs((current) => current.map((pair, i) => (i === index ? { ...pair, [side]: value } : pair)));

  const canSave = to !== null && pairs.length > 0 && pairs.every((pair) => pair.from !== "" && pair.to !== "")
    && !save.isPending;

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && !save.isPending) {
          onClose();
        }
      }}
    >
      <DialogContent className="sm:max-w-2xl" data-testid="semantic-join-dialog">
        <DialogHeader>
          <DialogTitle>{draft.id === null ? "New relationship" : "Edit relationship"}</DialogTitle>
          <DialogDescription>
            A join you vouch for. The assistant prefers it over joins inferred from the code.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          {save.isError && (
            <p className="text-[13px] text-destructive" data-testid="semantic-join-error">{errorText(save.error)}</p>
          )}
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <Label>From</Label>
              <p className="flex min-h-8 items-center font-mono text-[12px]">
                {qualifiedName(draft.from.database, draft.from.schema, draft.from.name)}
              </p>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label>To</Label>
              <LayerTablePicker
                value={to}
                onChange={(next) => {
                  setTo(next);
                  setPairs((current) => current.map((pair) => ({ ...pair, to: "" })));
                }}
                locked={draft.id !== null}
                testId="semantic-join-to"
              />
            </div>
          </div>

          <div className="flex flex-col gap-2">
            <Label>Join on</Label>
            {pairs.map((pair, index) => (
              <div key={index} className="grid grid-cols-[1fr_auto_1fr_auto] items-center gap-2">
                <ColumnSelect
                  value={pair.from}
                  options={fromColumns}
                  onChange={(value) => setPair(index, "from", value)}
                  label={`From column ${index + 1}`}
                  testId={`semantic-join-from-${index}`}
                />
                <span className="text-muted-foreground">=</span>
                <ColumnSelect
                  value={pair.to}
                  options={toColumns}
                  onChange={(value) => setPair(index, "to", value)}
                  label={`To column ${index + 1}`}
                  testId={`semantic-join-to-${index}`}
                />
                <Button
                  variant="ghost"
                  size="icon"
                  className="size-8"
                  disabled={pairs.length === 1}
                  onClick={() => setPairs((current) => current.filter((_, i) => i !== index))}
                  aria-label={`Remove column pair ${index + 1}`}
                >
                  <X />
                </Button>
              </div>
            ))}
            <Button
              variant="outline"
              size="xs"
              className="self-start"
              onClick={() => setPairs((current) => [...current, { from: "", to: "" }])}
              data-testid="semantic-join-add-pair"
            >
              <Plus />
              Add column pair
            </Button>
          </div>

          <div className="grid gap-4 sm:grid-cols-[10rem_1fr]">
            <div className="flex flex-col gap-1.5">
              <Label>Join type</Label>
              <Select value={joinType} onValueChange={setJoinType}>
                <SelectTrigger size="sm" className="h-8 w-full" data-testid="semantic-join-type">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Inner">Inner</SelectItem>
                  <SelectItem value="Left">Left</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="semantic-join-description">Description</Label>
              <Input
                id="semantic-join-description"
                value={description}
                onChange={(event) => setDescription(event.target.value)}
                placeholder="When to use this join"
                className="h-8"
                data-testid="semantic-join-description"
              />
            </div>
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={save.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => save.mutate()} disabled={!canSave} data-testid="semantic-join-save">
            {save.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
