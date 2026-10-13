// The pure half of AnswerConfirmation: which queries an answer hands back, how they are told apart in a picker,
// and the question a confirmation stores. Kept free of React and of the app's path aliases so the unit suite
// (gui/unit) can run it under Node directly.

/** The longest question the store accepts, matching the column and the endpoint's own limit, so an over-long
 * question is caught before a round trip rather than coming back a 400. */
export const MAX_QUESTION_LENGTH = 1000;

/** The chat command that routes a message down the business-question path: a message that begins with it ("!what is
 * our revenue") is answered from the approved AI contexts. The store keeps the question without it
 * (QuestionExampleEndpoints.NormalizeQuestion). */
export const BUSINESS_QUESTION_COMMAND = "!";

/** The word that may follow the command, touching it ("!data what is our revenue"). */
export const BUSINESS_QUESTION_WORD = "data";

/** The words that, after the command, return a conversation to questions about the estate ("!exit", "! estate"). */
export const EXIT_WORDS: readonly string[] = ["exit", "estate"];

/** Unicode's White_Space, the white space every reader of a command skips (JavaScript's own \s differs: it holds
 * U+FEFF and not U+0085). */
const WHITE_SPACE = new Set([
  0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006,
  0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
]);

/** Characters that show nothing and that a message may carry before its command or after it, skipped as white space is. */
const INVISIBLE = new Set([0x180e, 0x200b, 0x200c, 0x200d, 0x2060, 0xfeff]);

/** What may follow a command word besides white space and the end: sentence punctuation. */
const WORD_ENDS = new Set([".", ",", ":", ";", "!", "?"].map((c) => c.charCodeAt(0)));

/** The mode a command sets, as the MCP server and the control plane name it. */
export type CommandMode = "data" | "estate";

/** What a person's message asks of the conversation: the mode its command sets, and the text after the command. */
export interface Command {
  mode: CommandMode;
  question: string;
}

const isSkippable = (code: number) => WHITE_SPACE.has(code) || INVISIBLE.has(code);

function skip(text: string, at: number, passes: (code: number) => boolean): number {
  while (at < text.length && passes(text.charCodeAt(at))) {
    at++;
  }
  return at;
}

/** Where `word` ends when `text` has it at `at` as a word of its own (its ASCII letters in any case, then the end, white
 * space, an invisible character or sentence punctuation); null otherwise. */
function afterWord(text: string, at: number, word: string): number | null {
  if (text.length - at < word.length) {
    return null;
  }
  for (let i = 0; i < word.length; i++) {
    const code = text.charCodeAt(at + i);
    if (code > 0x7f || String.fromCharCode(code).toLowerCase() !== word[i]) {
      return null;
    }
  }
  const end = at + word.length;
  if (end === text.length) {
    return end;
  }
  const next = text.charCodeAt(end);
  return isSkippable(next) || WORD_ENDS.has(next) ? end : null;
}

function trimEnd(text: string, at: number): string {
  let end = text.length;
  while (end > at && isSkippable(text.charCodeAt(end - 1))) {
    end--;
  }
  return text.slice(at, end);
}

/** The question after a command word that ends at `at`: its separator and trailing white space off. */
function questionAfter(text: string, at: number): string {
  return trimEnd(text, skip(text, at, (code) => isSkippable(code) || WORD_ENDS.has(code)));
}

/**
 * The command `message` carries, when it starts with one, by the grammar every reader shares (the MCP server's
 * `command_of`, the control plane's `AssistantQuestions.Read`, held together by
 * tools/sqlflow-mcp/tests/assistant-commands.json). White space and the invisible characters are skipped, then one or
 * more `!` start the command; `data` directly after them is the data command's word, and otherwise `exit` or `estate`,
 * after any white space, return to estate mode. Anything else after the `!` is the question of a bare `!`, which is data
 * mode. Null for a message that does not start with the command.
 */
export function readCommand(message: string | null | undefined): Command | null {
  if (message == null) {
    return null;
  }
  const bang = BUSINESS_QUESTION_COMMAND.charCodeAt(0);
  let at = skip(message, 0, isSkippable);
  if (at >= message.length || message.charCodeAt(at) !== bang) {
    return null;
  }
  at = skip(message, at + 1, (code) => code === bang || INVISIBLE.has(code));
  const afterData = afterWord(message, at, BUSINESS_QUESTION_WORD);
  if (afterData !== null) {
    return { mode: "data", question: questionAfter(message, afterData) };
  }
  at = skip(message, at, isSkippable);
  for (const word of EXIT_WORDS) {
    const afterExit = afterWord(message, at, word);
    if (afterExit !== null) {
      return { mode: "estate", question: questionAfter(message, afterExit) };
    }
  }
  return { mode: "data", question: trimEnd(message, at) };
}

const OPENING_FENCE = /^( {0,3})(`{3,}|~{3,})(.*)$/;
const CLOSING_FENCE = /^ {0,3}(`{3,}|~{3,})[ \t]*$/;

/**
 * The queries an answer hands back: the bodies of its fenced code blocks whose info string names the language `sql`,
 * read the way the chat's Markdown renderer reads them, so a block counts here exactly when the answer shows it as a
 * runnable query. A fence is three or more backticks or tildes, indented at most three spaces; it closes on a fence of
 * the same character at least as long, carrying nothing else, or at the end of the answer. The language is the info
 * string's first word, matched exactly. An untagged fence never counts: it is as likely to be YAML, a path, or a table
 * of results, and storing one of those as a confirmed example would poison the store. Empty blocks are skipped.
 */
export function extractSqlBlocks(text: string): string[] {
  const lines = text.split(/\r?\n/);
  const blocks: string[] = [];
  let i = 0;
  while (i < lines.length) {
    const open = OPENING_FENCE.exec(lines[i]);
    i++;
    if (open === null) {
      continue;
    }

    const [, indent, fence, rest] = open;
    // A backtick fence's info string may not itself carry a backtick: such a line is inline code, not a fence.
    if (fence.startsWith("`") && rest.includes("`")) {
      continue;
    }

    const language = rest.trim().split(/\s+/)[0] ?? "";
    const body: string[] = [];
    while (i < lines.length) {
      const close = CLOSING_FENCE.exec(lines[i]);
      if (close !== null && close[1][0] === fence[0] && close[1].length >= fence.length) {
        i++;
        break;
      }

      body.push(stripIndent(lines[i], indent.length));
      i++;
    }

    const sql = body.join("\n").trim();
    if (language === "sql" && sql.length > 0) {
      blocks.push(sql);
    }
  }

  return blocks;
}

/** A line inside a fence, with as much of the fence's own indentation taken off as it has. */
function stripIndent(line: string, width: number): string {
  let taken = 0;
  while (taken < width && line[taken] === " ") {
    taken++;
  }

  return line.slice(taken);
}

/** The first non-blank line of a query, which is what tells one block apart from another in a picker. */
export function firstLine(sql: string): string {
  return sql.split("\n").find((candidate) => candidate.trim().length > 0)?.trim() ?? sql;
}

/** The same line, cut short enough that several fit on one row; the full text rides on the title. */
export function shortLabel(sql: string): string {
  const line = firstLine(sql);
  return line.length > 32 ? `${line.slice(0, 32)}...` : line;
}

/** Flattens a message's parts down to its text, which is what the SQL fences live in. */
export function messageText(content: readonly { type: string; text?: string }[]): string {
  return content
    .filter((part): part is { type: "text"; text: string } => part.type === "text" && typeof part.text === "string")
    .map((part) => part.text)
    .join("\n");
}

/** A question as the store keeps it: trimmed, and without the leading data command (`readCommand`: one or more `!`,
 * then the command's word when it touches them and ends there; "! data quality" keeps its first word, since "data" is a
 * word). A message whose command returns to the estate is no question for the data, and is kept as typed. */
export function normalizeQuestion(question: string): string {
  const trimmed = question.trim();
  const command = readCommand(trimmed);
  return command?.mode === "data" ? command.question : trimmed;
}
