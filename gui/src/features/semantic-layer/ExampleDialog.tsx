import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
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
import type { SemanticExampleAdmin } from "../../api/types";
import { errorText, refreshSemanticLayer, textOrNull } from "./shared";

/** The longest question the store accepts, matching the column and the endpoint's own limit. */
const MAX_QUESTION_LENGTH = 1000;

/**
 * Edits one saved answer (one of the layer's example queries): the question people ask, the query that answers it,
 * and the datasource it runs against. The query is edited as plain text rather than in the SQL editor, because the
 * editor pretty-prints what it shows and a saved answer is stored exactly as written. Whether the query is acceptable
 * (one read-only SELECT over allow-listed columns, not a duplicate of another saved answer) is decided by the server,
 * and its refusal is shown here verbatim.
 */
export function ExampleDialog({ example, onClose }: { example: SemanticExampleAdmin; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [question, setQuestion] = useState(example.question);
  const [sql, setSql] = useState(example.sql);
  const [sourceRef, setSourceRef] = useState(example.sourceRef ?? "");

  const save = useMutation({
    mutationFn: () => semanticLayerApi.updateExample(example.id, {
      question: question.trim(),
      sql: sql.trim(),
      sourceRef: textOrNull(sourceRef),
    }),
    onSuccess: () => {
      toast.success("Saved answer updated.");
      refreshSemanticLayer(queryClient);
      onClose();
    },
  });

  const trimmedQuestion = question.trim();
  const tooLong = trimmedQuestion.length > MAX_QUESTION_LENGTH;
  const unchanged = trimmedQuestion === example.question
    && sql.trim() === example.sql
    && textOrNull(sourceRef) === example.sourceRef;
  const canSave = trimmedQuestion !== "" && !tooLong && sql.trim() !== "" && !unchanged && !save.isPending;

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && !save.isPending) {
          onClose();
        }
      }}
    >
      <DialogContent className="sm:max-w-2xl" data-testid="semantic-example-dialog">
        <DialogHeader>
          <DialogTitle>Edit saved answer</DialogTitle>
          <DialogDescription>
            The assistant reuses this query for questions that mean the same thing, and runs it without asking when a
            new question matches closely. Saving puts your name on it as the person who checked it.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          {example.problem !== null && (
            <p className="rounded-md border border-warning/60 p-2 text-[13px] text-warning" data-testid="semantic-example-problem">
              The assistant is not offered this answer right now: {example.problem}
            </p>
          )}
          {save.isError && (
            <p className="text-[13px] text-destructive" data-testid="semantic-example-error">{errorText(save.error)}</p>
          )}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="semantic-example-question">Question</Label>
            <Input
              id="semantic-example-question"
              value={question}
              onChange={(event) => setQuestion(event.target.value)}
              aria-invalid={tooLong || undefined}
              className="h-8"
              data-testid="semantic-example-question"
            />
            {tooLong && (
              <p className="text-xs text-destructive">
                This question is {trimmedQuestion.length} characters, over the {MAX_QUESTION_LENGTH}-character limit.
              </p>
            )}
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="semantic-example-sql">Query</Label>
            <Textarea
              id="semantic-example-sql"
              value={sql}
              onChange={(event) => setSql(event.target.value)}
              spellCheck={false}
              className="min-h-40 font-mono text-[12px]"
              data-testid="semantic-example-sql"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="semantic-example-source">Datasource</Label>
            <Input
              id="semantic-example-source"
              value={sourceRef}
              onChange={(event) => setSourceRef(event.target.value)}
              placeholder="${env:...} or @alias"
              className="h-8 font-mono text-[12px]"
              data-testid="semantic-example-source"
            />
            <p className="text-xs text-muted-foreground">
              Leave blank to work it out from the tables the query reads.
            </p>
          </div>
        </div>

        <DialogFooter>
          <Button variant="ghost" size="sm" onClick={onClose} disabled={save.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => save.mutate()} disabled={!canSave} data-testid="semantic-example-save">
            {save.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
