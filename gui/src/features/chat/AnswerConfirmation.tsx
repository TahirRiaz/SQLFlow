// The confirmation moment of PowerAI's learning loop (POWERAI.md Section 6): the row under a
// finished answer where a person says the query was right, fixes it, or says it was wrong. Until
// this existed, a decision was recorded only when the assistant happened to remember to call
// confirm_question after being told, which made the loop real but opportunistic; here it is a click.
//
// Nothing new is stored: it posts to the confirm endpoint that already backs the confirm_question
// MCP tool, so a click and a tool call land the same row. The query being judged is the SQL the
// answer handed back in a fenced code block, which is exactly what the person read before deciding.

import { Suspense, lazy, useMemo, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useAui, useAuiState } from "@assistant-ui/react";
import { Check, CircleCheck, Pencil, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Textarea } from "@/components/ui/textarea";
import { cn } from "@/lib/utils";
import { ComboBoxField } from "../../components/ComboBoxField";
import { isApiError } from "../../api/client";
import { datasourceApi, questionExampleApi } from "../../api/endpoints";
import type { ConfirmQuestionRequest, ConfirmedQuestion, Datasource, QuestionConfirmationOutcome } from "../../api/types";

// Monaco is a large chunk the chat route does not otherwise pull in, and correcting a query is the
// rare path; it loads when someone actually opens the editor, not when a thread renders.
const CodeView = lazy(() => import("../../components/CodeView").then((m) => ({ default: m.CodeView })));

/** The longest question the store accepts, matching the column and the endpoint's own limit, so an
 * over-long question is caught before a round trip rather than coming back a 400. */
const MAX_QUESTION_LENGTH = 1000;

/** A fenced block the answer marked as SQL. Only `sql`-tagged fences count: an untagged fence is as
 * likely to be YAML, a path, or a table of results, and storing one of those as a confirmed example
 * would poison the store with something no future question should ever be answered with. */
const SQL_FENCE = /```sql\r?\n([\s\S]*?)```/gi;

function extractSqlBlocks(text: string): string[] {
  const blocks: string[] = [];
  for (const match of text.matchAll(SQL_FENCE)) {
    const sql = match[1].trim();
    if (sql.length > 0) {
      blocks.push(sql);
    }
  }
  return blocks;
}

/** The first non-blank line of a query, which is what tells one block apart from another in a picker. */
function firstLine(sql: string): string {
  return sql.split("\n").find((candidate) => candidate.trim().length > 0)?.trim() ?? sql;
}

/** The same line, cut short enough that several fit on one row; the full text rides on the title. */
function shortLabel(sql: string): string {
  const line = firstLine(sql);
  return line.length > 32 ? `${line.slice(0, 32)}...` : line;
}

/** Flattens a thread message's parts down to its text, which is what the SQL fences live in. */
function messageText(content: readonly { type: string }[]): string {
  return content
    .filter((part): part is { type: "text"; text: string } => part.type === "text")
    .map((part) => part.text)
    .join("\n");
}

/** Which panel the row has open; "idle" is the three buttons, and a decision closes the row for good. */
type Stage = "idle" | "accepting" | "correcting";

/**
 * Rendered under every assistant answer in the chat thread (the `AnswerFooter` slot). It shows
 * nothing at all unless the answer is finished AND carries at least one SQL block AND the
 * deployment has the example store turned on, so a thread of ordinary prose answers looks exactly
 * as it did before.
 */
export function AnswerConfirmation({ enabled }: { enabled: boolean }) {
  const aui = useAui();
  const status = useAuiState((s) => s.message.status?.type);
  const messageId = useAuiState((s) => s.message.id);
  // Deliberately NOT a `s.thread.messages` subscription: that array gets a new identity on every
  // streamed delta, so every answer in the thread would re-render (and re-scan its text) dozens of
  // times per second while one is being written. This component subscribes only to its OWN message
  // and reads the thread imperatively, once, when that message is finished.
  const content = useAuiState((s) => s.message.content);

  const [stage, setStage] = useState<Stage>("idle");
  const [selectedBlock, setSelectedBlock] = useState(0);
  const [editedSql, setEditedSql] = useState<string | null>(null);
  const [editedQuestion, setEditedQuestion] = useState<string | null>(null);
  const [sourceRef, setSourceRef] = useState<string | null>(null);
  const [decision, setDecision] = useState<ConfirmedQuestion | null>(null);

  // The candidate queries are the SQL this answer handed back, in the fenced blocks the assistant is
  // instructed to write them in, which is exactly what the person read before deciding.
  const sqlBlocks = useMemo(() => extractSqlBlocks(messageText(content)), [content]);

  // The question is the user turn this answer replies to. Resolved from the thread once the answer is
  // finished and known to carry a query, so a thread of prose answers never walks its own history.
  // A re-opened conversation resolves it exactly like a live one: the transcript is the same record.
  // It is only the STARTING point: what gets stored is whatever the person has in the field when they
  // press Store, since they are vouching for that text as the question this query answers.
  const askedQuestion = useMemo(() => {
    if (status === "running" || sqlBlocks.length === 0) {
      return "";
    }
    const messages = aui.thread.getState().messages;
    const index = messages.findIndex((message) => message.id === messageId);
    for (let i = index - 1; i >= 0; i--) {
      if (messages[i].role === "user") {
        return messageText(messages[i].content).trim();
      }
    }
    return "";
  }, [aui, status, sqlBlocks.length, messageId]);

  // Only offered for a datasource the catalog actually declares: the endpoint refuses anything else,
  // and an example pointed at a connection the reviewed estate never named must not be storable.
  const datasources = useQuery({
    queryKey: ["datasources"],
    queryFn: () => datasourceApi.list(),
    enabled: enabled && stage !== "idle",
  });

  const confirm = useMutation({
    mutationFn: (request: ConfirmQuestionRequest) => questionExampleApi.confirm(request),
    onSuccess: (result) => {
      setDecision(result);
      setStage("idle");
    },
    onError: (error) => toast.error("Could not record your decision", {
      description: isApiError(error) ? error.message : String(error),
    }),
  });

  const sql = editedSql ?? sqlBlocks[selectedBlock] ?? "";
  const question = editedQuestion ?? askedQuestion;
  const trimmedQuestion = question.trim();
  const tooLong = trimmedQuestion.length > MAX_QUESTION_LENGTH;
  const ready = status !== "running" && askedQuestion.length > 0 && sqlBlocks.length > 0;

  if (!enabled || !ready) {
    return null;
  }

  // Over the store's question limit while the row is closed, there is nothing to press: the endpoint
  // checks the length before it even reaches the rejection shortcut. The panel below can still shorten
  // it, so this points at that rather than turning the answer away outright.
  if (tooLong && stage === "idle") {
    return (
      <div className="mt-3 rounded-md border border-border bg-muted/30" data-testid="answer-confirmation-too-long">
        <div className="flex flex-wrap items-center gap-2 px-3 py-2">
          <span className="text-xs text-muted-foreground">
            This question is {trimmedQuestion.length} characters, over the {MAX_QUESTION_LENGTH}-character
            limit the example store accepts. Shorten it to confirm this answer.
          </span>
          <Button
            variant="ghost"
            size="xs"
            className="ms-auto"
            onClick={() => setStage("accepting")}
            data-testid="answer-shorten"
          >
            <Pencil /> Shorten
          </Button>
        </div>
      </div>
    );
  }

  if (decision !== null) {
    return (
      <div
        className="mt-3 flex items-start gap-2 rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground"
        data-testid="answer-confirmation-outcome"
      >
        <CircleCheck className="mt-px size-3.5 shrink-0 text-muted-foreground" />
        <span>{decision.message}</span>
      </div>
    );
  }

  const send = (outcome: QuestionConfirmationOutcome, statement: string) => confirm.mutate({
    question: trimmedQuestion,
    sql: statement,
    outcome,
    // Deliberately absent: objectKeys (no lineage pass produced them here, and inventing identities
    // would put unverified keys in the catalog) and confidence (the endpoint documents it as the
    // retrieval score a proposal was built from, never anyone's impression that a query looks right).
    sourceRef,
  });

  return (
    <div className="mt-3 rounded-md border border-border bg-muted/30" data-testid="answer-confirmation">
      <div className="flex flex-wrap items-center gap-2 px-3 py-2">
        <span className="text-xs text-muted-foreground">
          {stage === "idle"
            ? "Did this query answer your question?"
            : stage === "accepting"
              ? "Store this query as the confirmed answer to this question:"
              : "Fix the query, then store what actually worked:"}
        </span>
        <div className="ms-auto flex items-center gap-1">
          {stage === "idle"
            ? (
              <>
                <Button
                  variant="ghost"
                  size="xs"
                  onClick={() => setStage("accepting")}
                  data-testid="answer-accept"
                >
                  <Check /> Yes
                </Button>
                <Button
                  variant="ghost"
                  size="xs"
                  onClick={() => {
                    setEditedSql(sqlBlocks[selectedBlock] ?? "");
                    setStage("correcting");
                  }}
                  data-testid="answer-correct"
                >
                  <Pencil /> Not quite
                </Button>
                <Button
                  variant="ghost"
                  size="xs"
                  disabled={confirm.isPending}
                  onClick={() => send("rejected", sqlBlocks[selectedBlock] ?? "")}
                  data-testid="answer-reject"
                >
                  <X /> No
                </Button>
              </>
            )
            : (
              <>
                <Button
                  variant="ghost"
                  size="xs"
                  onClick={() => {
                    setEditedSql(null);
                    setEditedQuestion(null);
                    setStage("idle");
                  }}
                >
                  Cancel
                </Button>
                <Button
                  size="xs"
                  disabled={confirm.isPending || sql.trim().length === 0 || trimmedQuestion.length === 0 || tooLong}
                  onClick={() => send(stage === "accepting" ? "accepted" : "corrected", sql)}
                  data-testid="answer-confirm"
                >
                  {confirm.isPending ? "Storing..." : "Store"}
                </Button>
              </>
            )}
        </div>
      </div>

      {stage !== "idle" && (
        <div className="flex flex-col gap-2 border-t border-border px-3 py-2">
          {/* The question is stored alongside the query and is what every later question is matched
              against, so it is shown and editable rather than taken silently from the transcript: a
              person can fix a typo, or say more plainly what they meant, without touching the query
              they are confirming. What lands in the store is this text, not what was typed. */}
          <div className="flex flex-col gap-1">
            <label className="text-xs text-muted-foreground" htmlFor="answer-confirmation-question">
              Saved as this question
            </label>
            <Textarea
              id="answer-confirmation-question"
              value={question}
              onChange={(event) => setEditedQuestion(event.target.value)}
              rows={2}
              className="min-h-0 text-xs"
              data-testid="answer-confirmation-question"
            />
            <p className={cn("text-xs", tooLong ? "text-destructive" : "text-muted-foreground")}>
              {tooLong
                ? `${trimmedQuestion.length} characters, over the ${MAX_QUESTION_LENGTH}-character limit.`
                : "Later questions are matched against this text, so word it the way someone would ask it again."}
            </p>
          </div>
          {sqlBlocks.length > 1 && (
            <div className="flex flex-wrap items-center gap-1">
              <span className="text-xs text-muted-foreground">The answer carries several queries:</span>
              {sqlBlocks.map((block, index) => (
                <Button
                  key={index}
                  variant={index === selectedBlock ? "secondary" : "ghost"}
                  size="xs"
                  className={cn("font-mono text-[11px]", index === selectedBlock && "font-semibold")}
                  onClick={() => {
                    setSelectedBlock(index);
                    setEditedSql(stage === "correcting" ? block : null);
                  }}
                  title={firstLine(block)}
                >
                  {index + 1}. {shortLabel(block)}
                </Button>
              ))}
            </div>
          )}
          {/* Correcting always needs the editor. Accepting does not re-show a query the person just read
              directly above, UNLESS the answer carried several and the picker has to say which one is
              being stored. */}
          {(stage === "correcting" || sqlBlocks.length > 1) && (
            <Suspense fallback={<Skeleton className="h-[160px] w-full rounded-md" />}>
              <CodeView
                value={sql}
                language="sql"
                height={160}
                readOnly={stage === "accepting"}
                onChange={stage === "correcting" ? setEditedSql : undefined}
                data-testid="answer-confirmation-sql"
              />
            </Suspense>
          )}
          <ComboBoxField<Datasource>
            options={datasources.data ?? []}
            optionValue={(datasource) => datasource.reference}
            optionLabel={(datasource) => datasource.reference}
            renderOption={(datasource) => (
              <span className="flex w-full items-center justify-between gap-2">
                <span className="truncate font-mono text-[12px]">{datasource.reference}</span>
                {datasource.kind !== null && (
                  <span className="shrink-0 text-[11px] text-muted-foreground">{datasource.kind}</span>
                )}
              </span>
            )}
            value={sourceRef}
            onChange={(value) => setSourceRef(value)}
            label="Datasource (optional)"
            placeholder="Which connection this query runs against"
            loading={datasources.isLoading}
            loadingMessage="Loading datasources..."
            emptyMessage="No datasource in the catalog matches."
            clearOption={{ label: "No datasource", onClear: () => setSourceRef(null) }}
            testId="answer-confirmation-source"
            className="max-w-sm"
          />
          <p className="text-xs text-muted-foreground">
            Without a datasource the example is still kept and still found by later questions; only running it
            automatically needs one, since nothing else says which connection it belongs to.
          </p>
        </div>
      )}
    </div>
  );
}
