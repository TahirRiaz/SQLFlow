// The questions the home page offers to put to the assistant (DESIGN.md 12): what a module may offer, and which the page
// shows. Kept free of imports, so the unit suite reads it.

/** The most questions one module offers, and the longest one. */
export const MAX_HOME_PROMPTS = 6;
export const MAX_HOME_PROMPT_LENGTH = 160;

/** How many questions the home page shows. */
export const SHOWN_HOME_PROMPTS = 3;

/** The questions SQLFlow offers when no module offers any: what its own tools answer about the estate. */
export const SQLFLOW_HOME_PROMPTS: readonly string[] = [
  "What ran in the last 24 hours, and what failed?",
  "Which schedules fire next, and what do they run?",
  "Which pipelines feed a table I name, and what reads from it?",
];

/**
 * A module's questions as the home page takes them, trimmed; throws naming the module when it offers more than
 * {@link MAX_HOME_PROMPTS}, or one that is blank or longer than {@link MAX_HOME_PROMPT_LENGTH}.
 */
export function checkHomePrompts(moduleId: string, prompts: readonly string[]): string[] {
  if (prompts.length > MAX_HOME_PROMPTS) {
    throw new Error(`GUI module '${moduleId}' offers ${prompts.length} questions on the home page; a module offers at most ${MAX_HOME_PROMPTS}.`);
  }

  return prompts.map((prompt) => {
    const trimmed = prompt.trim();
    if (trimmed === "" || trimmed.length > MAX_HOME_PROMPT_LENGTH) {
      throw new Error(`GUI module '${moduleId}' offers the home page question '${prompt}', which is not 1 to ${MAX_HOME_PROMPT_LENGTH} characters.`);
    }

    return trimmed;
  });
}

/** The questions the home page shows: the first few the modules offer, each once, or SQLFlow's when they offer none. */
export function shownHomePrompts(offered: readonly string[]): string[] {
  const unique = [...new Set(offered)];
  return (unique.length > 0 ? unique : SQLFLOW_HOME_PROMPTS).slice(0, SHOWN_HOME_PROMPTS);
}
