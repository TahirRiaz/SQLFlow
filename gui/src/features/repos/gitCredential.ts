// How the repos pages say which git credential a source reads with. The server decides (it resolves every git
// operation through one path); these turn its answer into the words and tone every page shows the same way.

import type { GitCredential, GitCredentialKind, GitCredentialsStatus } from "../../api/types.ts";

/** A whole ${scheme:locator} reference, never a raw token; mirrors the server's guard. */
export const REFERENCE_RE = /^\$\{[a-zA-Z]+:[^}]+\}$/;

/** The reference form that names a GitHub App, for help text and placeholders. */
export const GITHUB_APP_REFERENCE_EXAMPLE = "${githubapp:app=<client ID>;key=keyvault:<vault>/<secret>}";

/** Where the GitHub App guide lives, for an operator setting one up. */
export const GITHUB_APP_GUIDE = "sqlflow/docs/reference/guides/github-app.md";

/** Whether a credential reference field holds an acceptable value: blank (the host's credential) or a whole reference. */
export function isCredentialReferenceValid(value: string): boolean {
  const trimmed = value.trim();
  return trimmed === "" || REFERENCE_RE.test(trimmed);
}

/** Whether a reference names a GitHub App rather than a token. */
export function isGitHubAppReference(value: string | null | undefined): boolean {
  return value != null && /^\$\{githubapp:[^}]+\}$/i.test(value.trim());
}

/** The short label of a credential kind. */
export function credentialKindLabel(kind: GitCredentialKind): string {
  switch (kind) {
    case "github-app":
      return "GitHub App";
    case "reference":
      return "Token";
    case "host-token":
      return "Host token";
    default:
      return "None";
  }
}

/** How a credential reads at a glance: a problem warns, a GitHub App is the recommended state, the rest are neutral. */
export function credentialTone(credential: GitCredential): "warning" | "success" | "info" | "muted" {
  if (credential.problem != null && credential.problem !== "") {
    return "warning";
  }

  switch (credential.kind) {
    case "github-app":
      return "success";
    case "none":
      return "muted";
    default:
      return "info";
  }
}

/** The label shown for a source's credential: its kind, and "host" when the host's configuration supplies it. */
export function credentialLabel(credential: GitCredential): string {
  const label = credentialKindLabel(credential.kind);
  return credential.fromHost && credential.kind === "github-app" ? `${label} (host)` : label;
}

/**
 * What a source naming no credential reads with on this control plane, in one sentence: the order the server follows
 * (its GitHub App for remotes on the App's GitHub, then its token, then nothing).
 */
export function hostCredentialSummary(status: GitCredentialsStatus): string {
  const app = status.gitHubApp;
  if (app.problem != null && app.problem !== "") {
    return `This control plane's GitHub App cannot be used: ${app.problem}`;
  }

  const token = status.hostToken.configured ? "SQLFLOW_GIT_TOKEN" : null;
  if (app.configured) {
    return token === null
      ? `This control plane's GitHub App ${app.appId ?? ""} for ${app.host} repositories it is installed on; any other remote is read without a credential.`
      : `This control plane's GitHub App ${app.appId ?? ""} for ${app.host} repositories it is installed on, then its ${token}.`;
  }

  return token === null
    ? "No credential: this control plane has no GitHub App and no SQLFLOW_GIT_TOKEN, so only a public remote can be read."
    : `This control plane's ${token}.`;
}
