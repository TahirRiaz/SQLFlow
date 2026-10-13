// The confirmation row's reading of an answer: which queries it hands back, how they are labelled, and the
// question a confirmation stores.

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";
import {
  MAX_QUESTION_LENGTH, extractSqlBlocks, firstLine, messageText, normalizeQuestion, readCommand, shortLabel,
} from "../src/features/chat/answerSql.ts";

describe("extractSqlBlocks", () => {
  it("reads a sql block, trimmed", () => {
    assert.deepEqual(extractSqlBlocks("Here:\n```sql\n  SELECT 1\n```\nDone."), ["SELECT 1"]);
  });

  it("reads several blocks in order, and repeats a repeated one", () => {
    const text = "```sql\nSELECT 1\n```\nand\n```sql\nSELECT 2\n```\n```sql\nSELECT 1\n```";
    assert.deepEqual(extractSqlBlocks(text), ["SELECT 1", "SELECT 2", "SELECT 1"]);
  });

  it("counts a block only when the renderer shows it as sql: the info string's first word, exactly", () => {
    assert.deepEqual(extractSqlBlocks("```sql title=\"Revenue\"\nSELECT 1\n```"), ["SELECT 1"]);
    assert.deepEqual(extractSqlBlocks("```sql   \nSELECT 1\n```"), ["SELECT 1"]);
    assert.deepEqual(extractSqlBlocks("```SQL\nSELECT 1\n```"), []);
    assert.deepEqual(extractSqlBlocks("```sqlite\nSELECT 1\n```"), []);
    assert.deepEqual(extractSqlBlocks("```\nSELECT 1\n```"), []);
    assert.deepEqual(extractSqlBlocks("```yaml\nflowType: ing\n```\n```sql\nSELECT 2\n```"), ["SELECT 2"]);
  });

  it("reads tilde fences and fences indented up to three spaces", () => {
    assert.deepEqual(extractSqlBlocks("~~~sql\nSELECT 1\n~~~"), ["SELECT 1"]);
    assert.deepEqual(extractSqlBlocks("1. Run:\n   ```sql\n   SELECT 1\n     FROM t\n   ```"), ["SELECT 1\n  FROM t"]);
    assert.deepEqual(extractSqlBlocks("    ```sql\n    SELECT 1\n    ```"), []);
  });

  it("closes only on a matching fence: the same character, at least as long, with nothing after it", () => {
    assert.deepEqual(extractSqlBlocks("````sql\nSELECT '```' AS fence\n```\nSELECT 2\n````"), ["SELECT '```' AS fence\n```\nSELECT 2"]);
    assert.deepEqual(extractSqlBlocks("~~~sql\nSELECT 1\n```\nSELECT 2\n~~~"), ["SELECT 1\n```\nSELECT 2"]);
    assert.deepEqual(extractSqlBlocks("```sql\nSELECT 1\n```sql\n```"), ["SELECT 1\n```sql"]);
  });

  it("runs an unterminated fence to the end of the answer, as the renderer does", () => {
    assert.deepEqual(extractSqlBlocks("Finally:\n```sql\nSELECT 1\nFROM t"), ["SELECT 1\nFROM t"]);
  });

  it("reads CRLF answers with no carriage return left in the query", () => {
    assert.deepEqual(extractSqlBlocks("```sql\r\nSELECT 1\r\nFROM t\r\n```\r\n"), ["SELECT 1\nFROM t"]);
  });

  it("skips an empty block, and inline code that only looks like a fence", () => {
    assert.deepEqual(extractSqlBlocks("```sql\n\n   \n```"), []);
    assert.deepEqual(extractSqlBlocks("Use ```sql `x` ``` inline."), []);
    assert.deepEqual(extractSqlBlocks(""), []);
  });
});

describe("labels", () => {
  it("a block is known by its first non-blank line, cut to 32 characters", () => {
    assert.equal(firstLine("\n  \n  SELECT a\nFROM t"), "SELECT a");
    assert.equal(firstLine("   "), "   ");
    assert.equal(shortLabel("SELECT a"), "SELECT a");
    assert.equal(shortLabel("SELECT " + "x".repeat(40)), "SELECT " + "x".repeat(25) + "...");
  });

  it("a message's text is its text parts, joined by lines, so a fence split across parts still reads", () => {
    const content = [
      { type: "text", text: "```sql\nSELECT 1" },
      { type: "tool-call" },
      { type: "text", text: "```" },
    ];
    assert.equal(messageText(content), "```sql\nSELECT 1\n```");
    assert.deepEqual(extractSqlBlocks(messageText(content)), ["SELECT 1"]);
  });
});

describe("normalizeQuestion", () => {
  it("takes the business-question command off the front, as the store does", () => {
    assert.equal(normalizeQuestion("!what is our revenue"), "what is our revenue");
    assert.equal(normalizeQuestion("! what"), "what");
    assert.equal(normalizeQuestion("!!what"), "what");
    assert.equal(normalizeQuestion("!"), "");
  });

  it("takes the command's word off too, only touching the command and as a word of its own", () => {
    assert.equal(normalizeQuestion("!data what is our revenue"), "what is our revenue");
    assert.equal(normalizeQuestion("  !DATA \t what is our revenue  "), "what is our revenue");
    assert.equal(normalizeQuestion("!!data what is our revenue"), "what is our revenue");
    assert.equal(normalizeQuestion("!data"), "");
    assert.equal(normalizeQuestion("!datax what"), "datax what");
    assert.equal(normalizeQuestion("!database size by schema"), "database size by schema");
    assert.equal(normalizeQuestion("! data quality by region"), "data quality by region");
  });

  it("keeps a message whose command returns to the estate as it was typed", () => {
    assert.equal(normalizeQuestion("!exit which flow loads the sales table?"), "!exit which flow loads the sales table?");
    assert.equal(normalizeQuestion("! estate"), "! estate");
  });

  it("leaves a question that does not start with the command as it is, trimmed", () => {
    assert.equal(normalizeQuestion("what about !data"), "what about !data");
    assert.equal(normalizeQuestion("  what is our revenue  "), "what is our revenue");
  });

  it("measures a question as it is stored, so the command never pushes it over the limit", () => {
    const atLimit = "q".repeat(MAX_QUESTION_LENGTH);
    assert.equal(normalizeQuestion("!data " + atLimit).length, MAX_QUESTION_LENGTH);
    assert.equal(normalizeQuestion("!" + atLimit).length, MAX_QUESTION_LENGTH);
  });
});

describe("readCommand", () => {
  // One table holds every reader to one grammar: the MCP server's, the control plane's and this one.
  const table = JSON.parse(
    readFileSync(new URL("../../tools/sqlflow-mcp/tests/assistant-commands.json", import.meta.url), "utf8"),
  ) as { cases: { message: string; mode: "data" | "estate" | null; question: string | null }[] };

  it("reads every message in the shared table as the table says", () => {
    assert.ok(table.cases.length >= 50, `the table covers the grammar, ${table.cases.length} cases`);
    for (const entry of table.cases) {
      const read = readCommand(entry.message);
      assert.equal(read?.mode ?? null, entry.mode, JSON.stringify(entry.message));
      assert.equal(read?.question ?? null, entry.question, JSON.stringify(entry.message));
    }
  });

  it("skips only Unicode white space and the invisible characters before the command", () => {
    const whiteSpace = [
      0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006,
      0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
    ];
    const invisible = [0x180e, 0x200b, 0x200c, 0x200d, 0x2060, 0xfeff];
    for (let code = 0; code <= 0xffff; code++) {
      const read = readCommand(String.fromCharCode(code) + "!exit");
      if (whiteSpace.includes(code) || invisible.includes(code) || code === 0x21) {
        assert.deepEqual(read, { mode: "estate", question: "" }, `U+${code.toString(16)}`);
      } else {
        assert.equal(read, null, `U+${code.toString(16)}`);
      }
    }
  });

  it("ends a command word only at white space or sentence punctuation", () => {
    const ends = new Set([0x180e, 0x200b, 0x200c, 0x200d, 0x2060, 0xfeff, ...".,:;!?"].map((c) => (typeof c === "number" ? c : c.charCodeAt(0))));
    const whiteSpace = new Set([
      0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006,
      0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000,
    ]);
    for (const [word, mode] of [["data", "data"], ["exit", "estate"], ["estate", "estate"]] as const) {
      for (let code = 0; code <= 0xffff; code++) {
        const c = String.fromCharCode(code);
        const read = readCommand(`!${word}${c}x`);
        const expected = whiteSpace.has(code) || ends.has(code)
          ? { mode, question: "x" }
          : { mode: "data", question: `${word}${c}x` };
        assert.deepEqual(read, expected, JSON.stringify(`!${word}${c}x`));
      }
    }
  });
});
