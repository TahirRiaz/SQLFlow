// How the repos pages say which git credential a source reads with, and which credential references they accept.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { GitCredentialsStatus } from "../src/api/types.ts";
import {
  credentialKindLabel,
  credentialLabel,
  credentialTone,
  hostCredentialSummary,
  isCredentialReferenceValid,
  isGitHubAppReference,
} from "../src/features/repos/gitCredential.ts";

function status(overrides: { app?: Partial<GitCredentialsStatus["gitHubApp"]>; token?: boolean }): GitCredentialsStatus {
  return {
    gitHubApp: { configured: false, apiUrl: "https://api.github.com", host: "github.com", ...overrides.app },
    hostToken: { configured: overrides.token ?? false },
  };
}

describe("isCredentialReferenceValid", () => {
  it("takes a blank value, which reads with the host's credential", () => {
    assert.equal(isCredentialReferenceValid(""), true);
    assert.equal(isCredentialReferenceValid("   "), true);
  });

  it("takes a whole reference of any scheme, the GitHub App's included", () => {
    assert.equal(isCredentialReferenceValid("${keyvault:vault/git-token}"), true);
    assert.equal(isCredentialReferenceValid(" ${env:GIT_TOKEN} "), true);
    assert.equal(isCredentialReferenceValid("${githubapp:app=Iv23li;key=keyvault:vault/app-key}"), true);
  });

  it("refuses a raw token and anything that is not one whole reference", () => {
    assert.equal(isCredentialReferenceValid("ghp_0123456789abcdef0123456789abcdef0123"), false);
    assert.equal(isCredentialReferenceValid("${keyvault:vault/git-token} extra"), false);
    assert.equal(isCredentialReferenceValid("keyvault:vault/git-token"), false);
  });
});

describe("isGitHubAppReference", () => {
  it("recognizes a GitHub App reference, whatever its case", () => {
    assert.equal(isGitHubAppReference("${githubapp:app=1;key=env:K}"), true);
    assert.equal(isGitHubAppReference("${GitHubApp:installation=42}"), true);
  });

  it("does not mistake a token reference or nothing for one", () => {
    assert.equal(isGitHubAppReference("${keyvault:vault/githubapp}"), false);
    assert.equal(isGitHubAppReference(null), false);
    assert.equal(isGitHubAppReference(undefined), false);
  });
});

describe("credential labels and tones", () => {
  it("names each kind", () => {
    assert.equal(credentialKindLabel("github-app"), "GitHub App");
    assert.equal(credentialKindLabel("reference"), "Token");
    assert.equal(credentialKindLabel("host-token"), "Host token");
    assert.equal(credentialKindLabel("none"), "None");
  });

  it("says when the host's App reads a source", () => {
    assert.equal(credentialLabel({ kind: "github-app", fromHost: true, summary: "" }), "GitHub App (host)");
    assert.equal(credentialLabel({ kind: "github-app", fromHost: false, summary: "" }), "GitHub App");
    assert.equal(credentialLabel({ kind: "host-token", fromHost: true, summary: "" }), "Host token");
  });

  it("warns on a problem whatever the kind, and marks an App as the recommended state", () => {
    assert.equal(credentialTone({ kind: "github-app", fromHost: true, summary: "", problem: "SQLFLOW_GITHUB_APP_PRIVATE_KEY is not set" }), "warning");
    assert.equal(credentialTone({ kind: "github-app", fromHost: false, summary: "" }), "success");
    assert.equal(credentialTone({ kind: "reference", fromHost: false, summary: "", problem: null }), "info");
    assert.equal(credentialTone({ kind: "none", fromHost: false, summary: "" }), "muted");
  });
});

describe("hostCredentialSummary", () => {
  it("says the App reads its GitHub's repositories, then the token", () => {
    assert.match(hostCredentialSummary(status({ app: { configured: true, appId: "Iv23li" }, token: true })),
      /GitHub App Iv23li for github\.com repositories it is installed on, then its SQLFLOW_GIT_TOKEN/);
  });

  it("says a remote the App does not serve is read without a credential when there is no token", () => {
    assert.match(hostCredentialSummary(status({ app: { configured: true, appId: "Iv23li" } })), /without a credential/);
  });

  it("says only a public remote can be read when the host has neither", () => {
    assert.match(hostCredentialSummary(status({})), /only a public remote/);
  });

  it("reports an App that cannot be used before anything else", () => {
    assert.match(hostCredentialSummary(status({ app: { problem: "SQLFLOW_GITHUB_APP_PRIVATE_KEY is not set" }, token: true })),
      /cannot be used: SQLFLOW_GITHUB_APP_PRIVATE_KEY is not set/);
  });
});
