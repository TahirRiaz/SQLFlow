// A question another page hands the assistant (the home page's question box): the chat opens a fresh conversation and
// sends it as the first message. It travels in the navigation's state rather than the address, so it is asked once: a
// reload, or coming Back to the chat, finds the conversation it started instead of asking again.

/** The chat's route. */
export const CHAT_PATH = "/chat";

/** The longest question handed over; the composer takes longer ones typed in it. */
export const MAX_HANDOFF_LENGTH = 4000;

interface ChatHandoffState {
  ask: string;
}

/** The navigation that opens the chat asking `question`, for `navigate(to, { state })`. */
export function chatHandoff(question: string): { to: string; state: ChatHandoffState } {
  return { to: CHAT_PATH, state: { ask: question.trim().slice(0, MAX_HANDOFF_LENGTH) } };
}

/** The question a navigation handed the chat, or null when it handed none. */
export function handedOver(state: unknown): string | null {
  if (state === null || typeof state !== "object" || !("ask" in state)) {
    return null;
  }

  const ask = (state as { ask: unknown }).ask;
  return typeof ask === "string" && ask.trim() !== "" ? ask.trim().slice(0, MAX_HANDOFF_LENGTH) : null;
}
