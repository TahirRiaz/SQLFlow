// The questions the home page offers the assistant: what a module may offer, and which the page shows.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  MAX_HOME_PROMPT_LENGTH, MAX_HOME_PROMPTS, SHOWN_HOME_PROMPTS, SQLFLOW_HOME_PROMPTS, checkHomePrompts, shownHomePrompts,
} from "../src/modules/homePrompts.ts";

describe("checkHomePrompts", () => {
  it("takes a module's questions trimmed, in the order given", () => {
    assert.deepEqual(checkHomePrompts("geo", ["  Which flows failed? ", "What changed?"]), ["Which flows failed?", "What changed?"]);
    assert.deepEqual(checkHomePrompts("geo", []), []);
  });

  it("refuses more questions than a module may offer, naming the module", () => {
    const many = Array.from({ length: MAX_HOME_PROMPTS + 1 }, (_, i) => `Question ${i}?`);
    assert.throws(() => checkHomePrompts("geo", many), /GUI module 'geo' offers 7 questions on the home page; a module offers at most 6\./);
  });

  it("refuses a blank question and one longer than the bound", () => {
    assert.throws(() => checkHomePrompts("geo", ["   "]), /which is not 1 to 160 characters/);
    assert.throws(() => checkHomePrompts("geo", ["x".repeat(MAX_HOME_PROMPT_LENGTH + 1)]), /GUI module 'geo' offers the home page question/);
    assert.equal(checkHomePrompts("geo", ["x".repeat(MAX_HOME_PROMPT_LENGTH)])[0].length, MAX_HOME_PROMPT_LENGTH);
  });
});

describe("shownHomePrompts", () => {
  it("shows SQLFlow's own when no module offers any", () => {
    assert.deepEqual(shownHomePrompts([]), SQLFLOW_HOME_PROMPTS.slice(0, SHOWN_HOME_PROMPTS));
  });

  it("shows the modules' first few, each once, and none of SQLFlow's beside them", () => {
    assert.deepEqual(shownHomePrompts(["A?", "B?", "A?", "C?", "D?"]), ["A?", "B?", "C?"]);
    assert.deepEqual(shownHomePrompts(["Only one?"]), ["Only one?"]);
  });
});
