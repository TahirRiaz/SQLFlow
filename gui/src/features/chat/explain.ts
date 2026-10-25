// What "Explain" asks the assistant: about a piece of code a page shows, or about the page itself. The question carries
// what the assistant needs to answer without seeing the screen: the code (or the selected lines of it) and its language,
// the page it is on, the description its navigation entry gives, and the page's headings, tabs and columns. Kept free of
// imports, so the unit suite reads it.

/** The longest question sent; the control plane takes at most 32,000 characters, and the wording needs some of it. */
export const MAX_EXPLAIN_LENGTH = 30_000;

/** The most entries of each list of a page's outline, and the longest one, so a wide grid cannot crowd the question. */
export const MAX_OUTLINE_ENTRIES = 24;
export const MAX_OUTLINE_ENTRY_LENGTH = 80;

/** The page a question is asked from. */
export interface ExplainPage {
  /** The page's title, as its tab shows it ("orders_ods", "Runs"). */
  title: string;
  /** The page's route, without the query. */
  path: string;
  /** The navigation entry the page belongs to, when it is not the entry's own page ("Pipelines" for a pipeline). */
  area: string | null;
  /** What the page's navigation entry says the page is for; null for a page no entry owns. */
  description: string | null;
}

/** What a page shows, read from it when its explanation is asked. */
export interface PageOutline {
  headings: string[];
  tabs: string[];
  columns: string[];
}

/** The code to explain: a whole document, or the lines selected in it. */
export interface ExplainCode {
  code: string;
  language: string;
  /** The selected lines, 1-based and inclusive; null for the whole document. */
  lines: { start: number; end: number } | null;
  /** How many lines the whole document has. */
  totalLines: number;
}

const LANGUAGE_NAMES: Record<string, string> = { yaml: "YAML", json: "JSON", sql: "SQL", plaintext: "text" };

/** The language's name as a sentence says it. */
export function languageName(language: string): string {
  return LANGUAGE_NAMES[language] ?? language.toUpperCase();
}

/** What is being explained, in a few words: the sheet's subtitle. */
export function codeSubject(code: ExplainCode): string {
  const name = languageName(code.language);
  if (code.lines === null) {
    return `${name}, ${linesText(code.totalLines)}`;
  }

  return code.lines.start === code.lines.end
    ? `${name}, line ${code.lines.start} of ${code.totalLines}`
    : `${name}, lines ${code.lines.start} to ${code.lines.end} of ${code.totalLines}`;
}

function linesText(count: number): string {
  return count === 1 ? "1 line" : `${count} lines`;
}

/** A fence the code cannot close early: one backtick longer than the longest run of backticks in it. */
function fence(code: string): string {
  const longest = Math.max(0, ...Array.from(code.matchAll(/`+/g), (match) => match[0].length));
  return "`".repeat(Math.max(3, longest + 1));
}

/** What the page's navigation entry says it is for, named as the entry's when the page is one reached from it. */
function purposeLine(page: ExplainPage): string | null {
  if (page.description === null) {
    return null;
  }

  return page.area !== null && page.area !== page.title
    ? `What ${page.area} is for: ${page.description}`
    : `What the page is for: ${page.description}`;
}

function pageLine(page: ExplainPage): string {
  const area = page.area !== null && page.area !== page.title ? `, under ${page.area}` : "";
  return `the "${page.title}" page (${page.path}${area})`;
}

/** How the asker hands the assistant the reference documentation, after the code or the page it asks about. */
const LOOK_IT_UP = "Where you are not sure what something means here, check the reference documentation rather than guessing.";

/**
 * The question that explains a piece of code, in the asker's words: the code is its own context, so it says only where
 * the code is shown. A document longer than the question can carry is cut at a line, and the question says how much of
 * it is shown.
 */
export function explainCodeQuestion(code: ExplainCode, page: ExplainPage, productName: string): string {
  const name = languageName(code.language);
  const where = code.lines === null
    ? `the whole document (${linesText(code.totalLines)})`
    : code.lines.start === code.lines.end
      ? `line ${code.lines.start} of ${code.totalLines}`
      : `lines ${code.lines.start} to ${code.lines.end} of ${code.totalLines}`;
  const intro = `I'm new to ${productName}. What does this ${name} do, and what does each part of it mean? `
    + `It is ${where} on ${pageLine(page)}.`;

  const shown = fit(code.code, MAX_EXPLAIN_LENGTH - intro.length - LOOK_IT_UP.length - 200);
  return `${intro}\n\n${block(shown.text, code.language)}${shown.note}\n\n${LOOK_IT_UP}`;
}

/** Two versions of one document, as a comparison window shows them. */
export interface ExplainChange {
  original: string;
  modified: string;
  language: string;
}

/** What is being explained when it is a change, in a few words: the sheet's subtitle. */
export function changeSubject(change: ExplainChange): string {
  return `the change to this ${languageName(change.language)}`;
}

/** The question that explains what changed between two versions of a document, and what the change does. */
export function explainChangeQuestion(change: ExplainChange, page: ExplainPage, productName: string): string {
  const name = languageName(change.language);
  const intro = `I'm new to ${productName}. What changed between these two versions of a ${name} document, compared `
    + `on ${pageLine(page)}, and what does the change do?`;
  const room = Math.floor((MAX_EXPLAIN_LENGTH - intro.length - LOOK_IT_UP.length - 300) / 2);
  const before = fit(change.original, room);
  const after = fit(change.modified, room);
  return `${intro}\n\nThe earlier version:\n\n${block(before.text, change.language)}${before.note}`
    + `\n\nThe later version:\n\n${block(after.text, change.language)}${after.note}\n\n${LOOK_IT_UP}`;
}

/** A fenced block of code. */
function block(text: string, language: string): string {
  const marks = fence(text);
  return `${marks}${language}\n${text}\n${marks}`;
}

/** Code cut at a line to fit in `room` characters, with a note saying how much of it is shown when it was cut. */
function fit(code: string, room: number): { text: string; note: string } {
  const text = code.replace(/\s+$/, "");
  if (text.length <= room) {
    return { text, note: "" };
  }

  const end = text.lastIndexOf("\n", room);
  const shown = text.slice(0, end > 0 ? end : room);
  const total = text.split("\n").length;
  return {
    text: shown,
    note: `\n\nOnly the first ${linesText(shown.split("\n").length)} of ${linesText(total)} fit in this question.`,
  };
}

/** A page's outline as the question carries it: each list trimmed, without blanks or repeats, and capped. */
export function tidyOutline(outline: PageOutline): PageOutline {
  const tidy = (entries: string[]): string[] => {
    const seen = new Set<string>();
    const kept: string[] = [];
    for (const entry of entries) {
      const text = entry.replace(/\s+/g, " ").trim();
      if (text === "" || seen.has(text) || text.length > MAX_OUTLINE_ENTRY_LENGTH) {
        continue;
      }

      seen.add(text);
      kept.push(text);
      if (kept.length === MAX_OUTLINE_ENTRIES) {
        break;
      }
    }

    return kept;
  };

  return { headings: tidy(outline.headings), tabs: tidy(outline.tabs), columns: tidy(outline.columns) };
}

/**
 * The question that explains a page, in the asker's words: what the feature is for, what the page shows, and what a
 * person does there, with what its navigation entry says it is for and what it shows as a list under it.
 */
export function explainPageQuestion(page: ExplainPage, outline: PageOutline, productName: string): string {
  const tidy = tidyOutline(outline);
  const facts: string[] = [];
  const purpose = purposeLine(page);
  if (purpose !== null) {
    facts.push(purpose);
  }

  if (tidy.headings.length > 0) {
    facts.push(`Its headings: ${tidy.headings.join("; ")}`);
  }

  if (tidy.tabs.length > 0) {
    facts.push(`Its tabs: ${tidy.tabs.join("; ")}`);
  }

  if (tidy.columns.length > 0) {
    facts.push(`Its columns: ${tidy.columns.join("; ")}`);
  }

  const intro = `I'm new to ${productName}. What is ${pageLine(page)} for, what does it show, and what do I do there? `
    + "Keep it short and concrete.";
  const list = facts.length > 0 ? `\n\n${facts.map((fact) => `- ${fact}`).join("\n")}` : "";
  return `${intro}${list}\n\n${LOOK_IT_UP}`;
}
