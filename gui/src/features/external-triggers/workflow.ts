// The GitHub workflow a trigger needs, written out for the External triggers page to show and copy: a pure function of
// the trigger, the control plane's setup and what the person picks, so it is tested on its own (unit/triggerWorkflow.test.ts).

import type { ExternalTrigger, ExternalTriggerSetup } from "../../api/types";

/** What the person picks for the workflow: the schedules a merge fires, and whether the traces are kept as an artifact. */
export interface WorkflowChoices {
  /** The schedules every merge fires; empty only syncs. */
  onMerge: string[];
  /** The schedules a person may pick from when running the workflow by hand. */
  selectable: string[];
  keepTraces: boolean;
}

/** A value as a YAML scalar: as it is when it needs no quoting, in single quotes (with quotes doubled) otherwise. */
export function yamlScalar(value: string): string {
  return /^[A-Za-z0-9._/-]+$/.test(value) && !/^(true|false|yes|no|on|off|null|~)$/i.test(value)
    ? value
    : `'${value.replace(/'/g, "''")}'`;
}

/** What `on:` a trigger's ref pattern runs on: the branches (or tags) a push to which runs the workflow. */
export function pushFilter(ref: string | null): { key: "branches" | "tags"; patterns: string[] } {
  if (ref === null || ref === "*") {
    return { key: "branches", patterns: ["**"] };
  }

  if (ref.startsWith("refs/tags/")) {
    return { key: "tags", patterns: [ref.slice("refs/tags/".length).replace(/\*/g, "**")] };
  }

  const branch = ref.startsWith("refs/heads/") ? ref.slice("refs/heads/".length) : ref;
  return { key: "branches", patterns: [branch.replace(/\*/g, "**")] };
}

/** The workflow's file name, from the trigger's workflow path, or one named after the trigger. */
export function workflowPath(trigger: ExternalTrigger): string {
  return trigger.gitHub?.workflow ?? `.github/workflows/${trigger.name}.yml`;
}

/**
 * The workflow: on a push to the trigger's branch it syncs the repository and fires the schedules picked for a merge;
 * run by hand, it offers the selectable schedules (each alone, the merge's set, or none) as a choice. It asks GitHub for
 * the job's token (id-token write), follows the request, writes the summary and keeps the traces as an artifact.
 */
export function workflowYaml(
  trigger: ExternalTrigger, setup: ExternalTriggerSetup, controlPlaneUrl: string, choices: WorkflowChoices,
): string {
  const github = trigger.gitHub;
  const key = trigger.kind === "key";
  const push = pushFilter(github?.ref ?? null);
  const onMerge = choices.onMerge.join(",");
  const options = Array.from(new Set([...(onMerge === "" ? [] : [onMerge]), ...choices.selectable, "none"]));
  const lines: string[] = [];
  const add = (indent: number, text: string) => lines.push(`${"  ".repeat(indent)}${text}`);

  add(0, `# Syncs ${trigger.repoName} in SQLFlow after every merge and fires its schedules, then reports the sync`);
  add(0, "# and every run in this job: the trace in the log, a summary on the job's page, and the traces as an artifact.");
  add(0, `name: SQLFlow ${trigger.name}`);
  add(0, "on:");
  add(1, "push:");
  add(2, `${push.key}: [${push.patterns.map(yamlScalar).join(", ")}]`);
  add(1, "workflow_dispatch:");
  add(2, "inputs:");
  add(3, "schedules:");
  add(4, "description: The schedules to fire after the sync");
  add(4, "type: choice");
  add(4, `default: ${yamlScalar(options[0])}`);
  add(4, "options:");
  for (const option of options) {
    add(5, `- ${yamlScalar(option)}`);
  }

  add(0, "permissions:");
  if (!key) {
    add(1, "id-token: write");
  }

  add(1, "contents: read");
  add(0, "concurrency:");
  add(1, `group: sqlflow-${trigger.name}`);
  add(1, "cancel-in-progress: false");
  add(0, "jobs:");
  add(1, "sync:");
  add(2, "runs-on: ubuntu-latest");
  if (github?.environment) {
    add(2, `environment: ${yamlScalar(github.environment)}`);
  }

  add(2, "steps:");
  if (setup.gitHubActionReference.startsWith("./")) {
    add(3, "# The action is kept in this repository (SQLFlow's integrations/github folder), so check it out first.");
    add(3, "- uses: actions/checkout@v4");
  }

  add(3, `- uses: ${setup.gitHubActionReference}`);
  add(4, "with:");
  add(5, `control-plane: ${controlPlaneUrl}`);
  if (key) {
    add(5, "auth: key");
    add(5, "key: ${{ secrets.SQLFLOW_TRIGGER_KEY }}");
  } else {
    add(5, `audience: ${yamlScalar(setup.gitHubAudience)}`);
    add(5, `trigger: ${trigger.name}`);
  }

  add(5, `schedules: \${{ github.event_name == 'workflow_dispatch' && (inputs.schedules == 'none' && ' ' || inputs.schedules) || '${onMerge.replace(/'/g, "''")}' }}`);
  if (choices.keepTraces) {
    add(5, "traces: sqlflow-traces");
    add(3, "- uses: actions/upload-artifact@v4");
    add(4, "if: always()");
    add(4, "with:");
    add(5, `name: sqlflow-${trigger.name}`);
    add(5, "path: sqlflow-traces");
    add(5, "if-no-files-found: ignore");
  }

  return `${lines.join("\n")}\n`;
}

/** The two calls a system without GitHub makes with a key trigger, as a shell script (curl and jq). */
export function keyScript(controlPlaneUrl: string, schedules: string[]): string {
  const body = JSON.stringify({ schedules }).replace(/'/g, "'\\''");
  return [
    "# SQLFLOW_TRIGGER_KEY holds the trigger's key; keep it in your CI system's secret store.",
    `CP=${controlPlaneUrl}`,
    "token=$(curl -sf -X POST \"$CP/api/v1/auth/trigger/key\" -H 'Content-Type: application/json' \\",
    "  -d \"{\\\"key\\\":\\\"$SQLFLOW_TRIGGER_KEY\\\"}\" | jq -r .accessToken)",
    "request=$(curl -sf -X POST \"$CP/api/v1/trigger-requests\" -H \"Authorization: Bearer $token\" \\",
    `  -H 'Content-Type: application/json' -d '${body}' | jq -r .id)`,
    "# Follow it: GET $CP/api/v1/trigger-requests/$request (conclusion is success, failure or cancelled once it ends),",
    "# and GET $CP/api/v1/trigger-requests/$request/summary for the Markdown summary.",
    "",
  ].join("\n");
}
