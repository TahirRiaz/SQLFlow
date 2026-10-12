// The pure half of AnswerConfirmation: which queries an answer hands back, how they are told apart in a picker,
// and the question a confirmation stores. Kept free of React and of the app's path aliases so the unit suite
// (gui/unit) can run it under Node directly.

/** The longest question the store accepts, matching the column and the endpoint's own limit, so an over-long
 * question is caught before a round trip rather than coming back a 400. */
export const MAX_QUESTION_LENGTH = 1000;

/** The chat command that routes a message down the business-question path ("!cwd what is our revenue"). The store
 * keeps the question without it (QuestionExampleEndpoints.NormalizeQuestion). */
export const BUSINESS_QUESTION_COMMAND = "!cwd";

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

/** A question as the store keeps it: trimmed, and without a leading business-question command. */
export function normalizeQuestion(question: string): string {
  const trimmed = question.trim();
  const command = BUSINESS_QUESTION_COMMAND.length;
  if (trimmed.slice(0, command).toLowerCase() === BUSINESS_QUESTION_COMMAND
    && (trimmed.length === command || /\s/.test(trimmed[command]))) {
    return trimmed.slice(command).trimStart();
  }

  return trimmed;
}
