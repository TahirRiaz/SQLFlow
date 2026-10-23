// A question another page hands the chat: the navigation that carries it, and what the chat reads back from it.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { CHAT_PATH, MAX_HANDOFF_LENGTH, chatHandoff, handedOver } from "../src/features/chat/chatHandoff.ts";

describe("chatHandoff", () => {
  it("opens the chat carrying the question, trimmed, in the navigation's state", () => {
    assert.deepEqual(chatHandoff("  Which flows failed today?  "), { to: CHAT_PATH, state: { ask: "Which flows failed today?" } });
  });

  it("carries at most the longest question it hands over", () => {
    assert.equal(chatHandoff("x".repeat(MAX_HANDOFF_LENGTH + 10)).state.ask.length, MAX_HANDOFF_LENGTH);
  });
});

describe("handedOver", () => {
  it("reads back the question a handoff carried", () => {
    assert.equal(handedOver(chatHandoff("What ran last night?").state), "What ran last night?");
  });

  it("reads no question from a navigation that carried none, or something else", () => {
    for (const state of [null, undefined, "text", 42, {}, { ask: "" }, { ask: "   " }, { ask: 7 }, { question: "x" }]) {
      assert.equal(handedOver(state), null, JSON.stringify(state) ?? "undefined");
    }
  });
});
