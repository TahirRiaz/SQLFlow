// The workflow the External triggers page writes out for a trigger: what it runs on, the schedules a merge fires and the
// ones a person may pick, how the job proves who it is, and that every value lands in YAML a workflow can read.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { ExternalTrigger, ExternalTriggerSetup } from "../src/api/types.ts";
import { keyScript, pushFilter, workflowPath, workflowYaml, yamlScalar } from "../src/features/external-triggers/workflow.ts";

function trigger(overrides: Partial<ExternalTrigger> = {}): ExternalTrigger {
  return {
    id: "t1",
    name: "deliver",
    description: null,
    kind: "github",
    repoName: "estate",
    allSchedules: false,
    schedules: ["nightly", "hourly"],
    gitHub: { repository: "acme/estate", repositoryId: "1", ref: "refs/heads/main", workflow: ".github/workflows/deliver.yml", environment: null },
    keyPrefix: null,
    keyExpiresUtc: null,
    enabled: true,
    configuredBy: null,
    createdBy: "admin",
    createdUtc: "2026-10-26T00:00:00Z",
    updatedUtc: "2026-10-26T00:00:00Z",
    lastUsedUtc: null,
    lastUsedDetail: null,
    lastRequest: null,
    ...overrides,
  };
}

const setup: ExternalTriggerSetup = {
  enabled: true,
  gitHubIssuer: "https://token.actions.githubusercontent.com",
  gitHubAudience: "https://cp.example.com",
  gitHubServerUrl: "https://github.com",
  gitHubActionReference: "./.github/actions/sqlflow",
  tokenMinutes: 60,
};

describe("the push the workflow runs on", () => {
  it("follows the trigger's ref", () => {
    assert.deepEqual(pushFilter("refs/heads/main"), { key: "branches", patterns: ["main"] });
    assert.deepEqual(pushFilter("refs/heads/release/*"), { key: "branches", patterns: ["release/**"] });
    assert.deepEqual(pushFilter("refs/tags/v*"), { key: "tags", patterns: ["v**"] });
    assert.deepEqual(pushFilter("*"), { key: "branches", patterns: ["**"] });
  });

  it("is named after the trusted workflow, or after the trigger", () => {
    assert.equal(workflowPath(trigger()), ".github/workflows/deliver.yml");
    assert.equal(workflowPath(trigger({ gitHub: { ...trigger().gitHub!, workflow: null } })), ".github/workflows/deliver.yml");
    assert.equal(workflowPath(trigger({ name: "sync-only", gitHub: null, kind: "key" })), ".github/workflows/sync-only.yml");
  });
});

describe("a GitHub trigger's workflow", () => {
  const yaml = workflowYaml(trigger(), setup, "https://cp.example.com", { onMerge: ["nightly"], selectable: ["nightly", "hourly"], keepTraces: true });

  it("asks GitHub for the job's token and acts as the trigger", () => {
    assert.match(yaml, /permissions:\n  id-token: write\n  contents: read/);
    assert.match(yaml, /audience: 'https:\/\/cp\.example\.com'|audience: https:\/\/cp\.example\.com/);
    assert.match(yaml, /trigger: deliver/);
    assert.doesNotMatch(yaml, /auth: key/);
  });

  it("fires the merge's schedules on a push and offers each selectable schedule, the merge's set and none by hand", () => {
    assert.match(yaml, /push:\n    branches: \[main\]/);
    assert.match(yaml, /options:\n          - nightly\n          - hourly\n          - none\n/);
    assert.match(yaml, /default: nightly/);
    assert.match(yaml, /\|\| 'nightly' \}\}/);
  });

  it("checks out a copy of the action kept in the repository, and keeps the traces", () => {
    assert.match(yaml, /- uses: actions\/checkout@v4\n {6}- uses: \.\/\.github\/actions\/sqlflow/);
    assert.match(yaml, /traces: sqlflow-traces/);
    assert.match(yaml, /actions\/upload-artifact@v4\n {8}if: always\(\)/);
  });

  it("runs in the environment the trigger asks for", () => {
    const withEnvironment = workflowYaml(
      trigger({ gitHub: { ...trigger().gitHub!, environment: "production" } }), setup, "https://cp.example.com",
      { onMerge: [], selectable: ["nightly"], keepTraces: false });
    assert.match(withEnvironment, /runs-on: ubuntu-latest\n    environment: production/);
    assert.match(withEnvironment, /\|\| '' \}\}/);
    assert.doesNotMatch(withEnvironment, /upload-artifact/);
  });

  it("takes an action an organization keeps elsewhere without checking anything out", () => {
    const shared = workflowYaml(trigger(), { ...setup, gitHubActionReference: "acme/actions/sqlflow@v1" }, "https://cp.example.com",
      { onMerge: ["nightly", "hourly"], selectable: [], keepTraces: false });
    assert.doesNotMatch(shared, /checkout/);
    assert.match(shared, /- uses: acme\/actions\/sqlflow@v1/);
    assert.match(shared, /default: 'nightly,hourly'/);
  });
});

describe("a key trigger's workflow", () => {
  it("passes the key from a secret and needs no token permission", () => {
    const yaml = workflowYaml(trigger({ kind: "key", gitHub: null }), setup, "https://cp.example.com", { onMerge: ["nightly"], selectable: [], keepTraces: false });
    assert.match(yaml, /auth: key\n {10}key: \$\{\{ secrets\.SQLFLOW_TRIGGER_KEY \}\}/);
    assert.doesNotMatch(yaml, /id-token/);
    assert.doesNotMatch(yaml, /trigger: /);
  });

  it("is also two calls a system without GitHub makes", () => {
    const script = keyScript("https://cp.example.com", ["night'ly"]);
    assert.match(script, /CP=https:\/\/cp\.example\.com/);
    assert.match(script, /\/api\/v1\/auth\/trigger\/key/);
    assert.ok(script.includes(`-d '{"schedules":["night'\\''ly"]}'`), script);
  });
});

describe("a YAML scalar", () => {
  it("is quoted only when it would not read back as itself", () => {
    assert.equal(yamlScalar("nightly"), "nightly");
    assert.equal(yamlScalar("release/2026.10"), "release/2026.10");
    assert.equal(yamlScalar("night ly"), "'night ly'");
    assert.equal(yamlScalar("it's"), "'it''s'");
    assert.equal(yamlScalar("yes"), "'yes'");
    assert.equal(yamlScalar("a:b"), "'a:b'");
  });
});
