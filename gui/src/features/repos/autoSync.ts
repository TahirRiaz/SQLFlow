import type { ExternalTrigger, RepoSource } from "../../api/types";

// How a repository source syncs, said the same way wherever it shows: the Repos page, a repo's page, the External
// triggers page's Repositories tab, and any module's own sync control. Import-free (types only), so the unit suite runs it.

/** Whether the source syncs only when asked. A source from a server that predates the switch always syncs on its own. */
export function syncsOnRequest(source: RepoSource): boolean {
  return source.autoSync === false;
}

/** A sync interval in words: "30 s", "5 min", "2 h", or the seconds when they do not divide evenly. */
export function intervalText(seconds: number): string {
  if (seconds >= 3600 && seconds % 3600 === 0) {
    return `${seconds / 3600} h`;
  }

  if (seconds >= 60 && seconds % 60 === 0) {
    return `${seconds / 60} min`;
  }

  return `${seconds} s`;
}

/** The commit a source holds, as a list shows it: its first 10 characters, or null for none (or a directory's). */
export function heldCommit(source: RepoSource): string | null {
  return source.lastSyncedSha !== null && /^[0-9a-f]{7,64}$/i.test(source.lastSyncedSha) ? source.lastSyncedSha.slice(0, 10) : null;
}

/** How the source syncs in a few words, for a list cell: paused, on request, or its interval. */
export function syncModeText(source: RepoSource): string {
  if (!source.enabled) {
    return "paused";
  }

  return syncsOnRequest(source) ? "on request" : `every ${intervalText(source.syncIntervalSeconds)}`;
}

/** How the source syncs and what that means for a merge, in a sentence or two for a tooltip. */
export function syncModeDetail(source: RepoSource): string {
  const every = intervalText(source.syncIntervalSeconds);
  if (!source.enabled) {
    return "Paused: it does not sync, whoever asks. Resume it on the Repos page.";
  }

  if (!syncsOnRequest(source)) {
    return source.localPath !== null
      ? `Syncs on its own every ${every}, reading ${source.localPath} as it is then.`
      : `Syncs on its own every ${every}, reading the tip of ${source.branch}: a merge reaches it within ${every}.`;
  }

  if (source.localPath !== null) {
    return "Syncs only when a trigger request or a person asks, reading the directory as it is then.";
  }

  const held = heldCommit(source);
  return held === null
    ? `Syncs only when asked once its first sync succeeds; until then it retries that every ${every}.`
    : `Syncs only when a trigger request or a person asks, and holds ${held} until then: a merge to ${source.branch} reaches it only when asked.`;
}

/** Whether syncing the source now moves it from the commit it holds to its branch's tip, which a person confirms first. */
export function syncNowMoves(source: RepoSource): boolean {
  return syncsOnRequest(source) && source.remoteUrl !== null && heldCommit(source) !== null;
}

/** What confirming a sync now of a source that syncs only on request says it does. */
export function syncNowConfirmText(source: RepoSource): string {
  return `${source.name} syncs only on request and holds ${heldCommit(source) ?? "no commit"}. Sync now moves it to the tip of `
    + `${source.branch}, with everything merged there since, and runs started afterwards use it. A trigger request moves it `
    + "to the merge it reports instead.";
}

/** The enabled triggers that may sync the source, by its name. */
export function triggersOf(source: RepoSource, triggers: readonly ExternalTrigger[]): ExternalTrigger[] {
  return triggers.filter((trigger) => trigger.enabled && trigger.repoName === source.name);
}

/** What an administrator should know about a source's automatic sync beside its triggers; null when nothing needs saying. */
export function autoSyncAdvice(
  source: RepoSource,
  triggers: readonly ExternalTrigger[],
): { tone: "warning" | "info"; text: string } | null {
  if (!source.enabled) {
    return null;
  }

  const syncing = triggersOf(source, triggers);
  if (syncsOnRequest(source) && syncing.length === 0) {
    return {
      tone: "warning",
      text: "No enabled trigger syncs it, so a merge reaches it only when a person syncs it now. Create a trigger for it, or turn auto sync on.",
    };
  }

  if (!syncsOnRequest(source) && syncing.length > 0) {
    const every = intervalText(source.syncIntervalSeconds);
    return {
      tone: "info",
      text: `A merge reaches it within ${every} whatever its triggers do. Turn auto sync off so it moves only to the merge a trigger reports.`,
    };
  }

  return null;
}
