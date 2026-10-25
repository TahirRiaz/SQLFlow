// What "Explain" asks the assistant: about code a page shows, about a change between two versions, or about a page.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  MAX_EXPLAIN_LENGTH, MAX_OUTLINE_ENTRIES, changeSubject, codeSubject, explainChangeQuestion, explainCodeQuestion,
  explainPageQuestion, languageName, tidyOutline, type ExplainPage,
} from "../src/features/chat/explain.ts";

const PIPELINE: ExplainPage = {
  title: "welldb_wells",
  path: "/pipelines/12",
  area: "Pipelines",
  description: "Every pipeline across the repositories.",
};

describe("languageName", () => {
  it("names the languages the code windows show, and spells out any other", () => {
    assert.equal(languageName("yaml"), "YAML");
    assert.equal(languageName("plaintext"), "text");
    assert.equal(languageName("xml"), "XML");
  });
});

describe("codeSubject", () => {
  it("says whether it is the whole document, one line or a range of lines", () => {
    assert.equal(codeSubject({ code: "a\nb", language: "yaml", lines: null, totalLines: 2 }), "YAML, 2 lines");
    assert.equal(codeSubject({ code: "a", language: "sql", lines: { start: 3, end: 3 }, totalLines: 9 }), "SQL, line 3 of 9");
    assert.equal(
      codeSubject({ code: "a\nb", language: "json", lines: { start: 4, end: 18 }, totalLines: 60 }),
      "JSON, lines 4 to 18 of 60",
    );
  });
});

describe("explainCodeQuestion", () => {
  it("asks in the asker's words about the code, fenced in its language, and says where it is shown", () => {
    const question = explainCodeQuestion(
      { code: "source:\n  table: wells\n", language: "yaml", lines: { start: 4, end: 5 }, totalLines: 30 },
      PIPELINE,
      "SQLFlow",
    );
    assert.equal(
      question,
      "I'm new to SQLFlow. What does this YAML do, and what does each part of it mean? It is lines 4 to 5 of 30 "
        + 'on the "welldb_wells" page (/pipelines/12, under Pipelines).\n\n'
        + "```yaml\nsource:\n  table: wells\n```\n\n"
        + "Where you are not sure what something means here, check the reference documentation rather than guessing.",
    );
  });

  it("leaves out the area when the page is the entry's own, and never carries the page's description", () => {
    const question = explainCodeQuestion(
      { code: "select 1", language: "sql", lines: null, totalLines: 1 },
      { title: "Runs", path: "/runs", area: "Runs", description: "Every run." },
      "SQLFlow",
    );
    assert.match(question, /It is the whole document \(1 line\) on the "Runs" page \(\/runs\)\./);
    assert.doesNotMatch(question, /under Runs|Every run\./);
  });

  it("fences code that holds a fence of its own with a longer one", () => {
    const question = explainCodeQuestion(
      { code: "note: |\n  ```\n  x\n  ```", language: "yaml", lines: null, totalLines: 4 },
      PIPELINE,
      "SQLFlow",
    );
    assert.ok(question.includes("````yaml\nnote: |\n  ```\n  x\n  ```\n````\n\n"), question);
  });

  it("cuts a document longer than a question can carry at a line, and says how much is shown", () => {
    const line = "x".repeat(99);
    const code = Array.from({ length: 1000 }, () => line).join("\n");
    const question = explainCodeQuestion({ code, language: "plaintext", lines: null, totalLines: 1000 }, PIPELINE, "SQLFlow");
    assert.ok(question.length <= MAX_EXPLAIN_LENGTH, String(question.length));
    assert.match(question, /Only the first \d+ lines of 1000 lines fit in this question\.\n\n/);
    assert.ok(!question.includes(`${line}x`), "a line is never cut in two");
  });
});

describe("explainChangeQuestion", () => {
  it("carries both versions, earlier first, each fenced in its language", () => {
    const change = { original: "a: 1", modified: "a: 2", language: "yaml" };
    assert.equal(changeSubject(change), "the change to this YAML");
    const question = explainChangeQuestion(change, PIPELINE, "SQLFlow");
    assert.match(question, /What changed between these two versions of a YAML document, compared on the "welldb_wells" page/);
    assert.ok(question.includes("The earlier version:\n\n```yaml\na: 1\n```\n\nThe later version:\n\n```yaml\na: 2\n```"), question);
  });

  it("fits two long versions into one question", () => {
    const long = Array.from({ length: 800 }, (_, index) => `line ${index} ${"y".repeat(60)}`).join("\n");
    const question = explainChangeQuestion({ original: long, modified: long, language: "sql" }, PIPELINE, "SQLFlow");
    assert.ok(question.length <= MAX_EXPLAIN_LENGTH, String(question.length));
    assert.equal(question.match(/fit in this question/g)?.length, 2);
  });
});

describe("tidyOutline", () => {
  it("trims, folds white space, drops blanks, repeats and overlong entries, and caps each list", () => {
    const tidy = tidyOutline({
      headings: ["  Runs ", "Runs", "", "  ", "Recent\n  runs", "z".repeat(81)],
      tabs: Array.from({ length: MAX_OUTLINE_ENTRIES + 5 }, (_, index) => `Tab ${index}`),
      columns: [],
    });
    assert.deepEqual(tidy.headings, ["Runs", "Recent runs"]);
    assert.equal(tidy.tabs.length, MAX_OUTLINE_ENTRIES);
    assert.deepEqual(tidy.columns, []);
  });
});

describe("explainPageQuestion", () => {
  it("asks what the page is for, listing its description and what it shows", () => {
    const question = explainPageQuestion(
      { title: "Runs", path: "/runs", area: "Runs", description: "Every run with its status." },
      { headings: ["Runs"], tabs: ["All", "Failed"], columns: ["Status", "Duration"] },
      "SQLFlow",
    );
    assert.equal(
      question,
      "I'm new to SQLFlow. What is the \"Runs\" page (/runs) for, what does it show, and what do I do there? "
        + "Keep it short and concrete.\n\n"
        + "- What the page is for: Every run with its status.\n"
        + "- Its headings: Runs\n- Its tabs: All; Failed\n- Its columns: Status; Duration\n\n"
        + "Where you are not sure what something means here, check the reference documentation rather than guessing.",
    );
  });

  it("names the description as its entry's on a page reached from the entry", () => {
    const question = explainPageQuestion(PIPELINE, { headings: [], tabs: [], columns: [] }, "SQLFlow");
    assert.match(question, /\n\n- What Pipelines is for: Every pipeline across the repositories\.\n\n/);
  });

  it("asks about a page no entry describes from what it shows alone", () => {
    const question = explainPageQuestion(
      { title: "Report #4", path: "/reports/4", area: null, description: null },
      { headings: [], tabs: [], columns: [] },
      "SQLFlow",
    );
    assert.ok(question.includes("Keep it short and concrete.\n\nWhere you are not sure"), question);
  });
});
