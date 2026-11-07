// How a repository source syncs, as every page says it: on its own every interval, or only on request holding a commit,
// what a sync now does to one that syncs on request, and what an administrator is told beside its triggers.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { ExternalTrigger, RepoSource } from "../src/api/types.ts";
import {
  autoSyncAdvice, heldCommit, intervalText, syncModeDetail, syncModeText, syncNowConfirmText, syncNowMoves, syncsOnRequest,
} from "../src/features/repos/autoSync.ts";

const sha = "0123456789abcdef0123456789abcdef01234567";

function source(overrides: Partial<RepoSource> = {}): RepoSource {
  return {
    id: "s1",
    name: "estate",
    remoteUrl: "https://github.com/acme/estate.git",
    localPath: null,
    branch: "main",
    enabled: true,
    syncIntervalSeconds: 300,
    nextSyncUtc: null,
    lastSyncUtc: "2026-11-07T09:00:00Z",
    lastSyncedSha: sha,
    lastError: null,
    credentialReference: null,
    credentialUsername: null,
    excludedFlowPaths: [],
    createdUtc: "2026-11-01T09:00:00Z",
    updatedUtc: "2026-11-07T09:00:00Z",
    syncRequestedUtc: null,
    syncStartedUtc: null,
    syncPending: false,
    autoSync: true,
    ...overrides,
  };
}

function trigger(overrides: Partial<ExternalTrigger> = {}): ExternalTrigger {
  return { id: "t1", name: "deliver", repoName: "estate", enabled: true, ...overrides } as ExternalTrigger;
}

describe("how a repository source syncs", () => {
  it("syncs on its own unless the server says otherwise, as one that predates the switch did", () => {
    assert.equal(syncsOnRequest(source({ autoSync: undefined })), false);
    assert.equal(syncsOnRequest(source({ autoSync: false })), true);
  });

  it("names its interval in the largest unit that divides it", () => {
    assert.equal(intervalText(300), "5 min");
    assert.equal(intervalText(7200), "2 h");
    assert.equal(intervalText(90), "90 s");
  });

  it("says paused, on request or its interval", () => {
    assert.equal(syncModeText(source()), "every 5 min");
    assert.equal(syncModeText(source({ autoSync: false })), "on request");
    assert.equal(syncModeText(source({ enabled: false, autoSync: false })), "paused");
  });

  it("explains what a merge does under each, and a first sync still to come", () => {
    assert.match(syncModeDetail(source()), /a merge reaches it within 5 min/);
    assert.match(syncModeDetail(source({ autoSync: false })), /holds 0123456789 until then/);
    assert.match(syncModeDetail(source({ autoSync: false, lastSyncedSha: null })), /retries that every 5 min/);
    assert.match(syncModeDetail(source({ autoSync: false, remoteUrl: null, localPath: "C:/flows", lastSyncedSha: "worktree" })), /reading the directory/);
  });

  it("holds a commit only when it synced one", () => {
    assert.equal(heldCommit(source()), "0123456789");
    assert.equal(heldCommit(source({ lastSyncedSha: "worktree@" + sha })), null);
  });

  it("asks before a sync now moves a source that syncs on request to its branch's tip", () => {
    assert.equal(syncNowMoves(source()), false);
    assert.equal(syncNowMoves(source({ autoSync: false })), true);
    assert.equal(syncNowMoves(source({ autoSync: false, lastSyncedSha: null })), false);
    assert.match(syncNowConfirmText(source({ autoSync: false })), /holds 0123456789\. Sync now moves it to the tip of main/);
  });

  it("warns when nothing syncs a source on a merge, and suggests on request when triggers already do", () => {
    assert.equal(autoSyncAdvice(source({ autoSync: false }), [])?.tone, "warning");
    assert.equal(autoSyncAdvice(source({ autoSync: false }), [trigger({ enabled: false })])?.tone, "warning");
    assert.equal(autoSyncAdvice(source({ autoSync: false }), [trigger()]), null);
    assert.equal(autoSyncAdvice(source(), [trigger()])?.tone, "info");
    assert.equal(autoSyncAdvice(source(), [trigger({ repoName: "other" })]), null);
    assert.equal(autoSyncAdvice(source({ enabled: false, autoSync: false }), []), null);
  });
});
