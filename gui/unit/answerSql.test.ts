// The confirmation row's reading of an answer: which queries it hands back, how they are labelled, and the
// question a confirmation stores.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  MAX_QUESTION_LENGTH, extractSqlBlocks, firstLine, messageText, normalizeQuestion, shortLabel,
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
    assert.equal(normalizeQuestion("!cwd what is our revenue"), "what is our revenue");
    assert.equal(normalizeQuestion("  !CWD \t what is our revenue  "), "what is our revenue");
    assert.equal(normalizeQuestion("!cwd"), "");
    assert.equal(normalizeQuestion("!cwdx what"), "!cwdx what");
    assert.equal(normalizeQuestion("what about !cwd"), "what about !cwd");
  });

  it("measures a question as it is stored, so the command never pushes it over the limit", () => {
    const atLimit = "q".repeat(MAX_QUESTION_LENGTH);
    assert.equal(normalizeQuestion("!cwd " + atLimit).length, MAX_QUESTION_LENGTH);
  });
});
