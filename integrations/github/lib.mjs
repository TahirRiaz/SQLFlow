// The SQLFlow sync-and-fire action: asks a SQLFlow control plane to sync a repository and fire a selection of its
// schedules, follows the request to its end, and reports it in the job: the trace in the log as it happens, the summary
// on the job's page, the traces as files for an artifact, and the job's own outcome. Every call is injected (fetch,
// the clock, the writers), so the whole flow runs in tests against a scripted control plane.

import { appendFile, mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";

/** The exit codes the action ends with: the request succeeded, did not, or the action could not follow it. */
export const ExitCodes = Object.freeze({ success: 0, failure: 1 });

/** How many annotations the action raises at most: GitHub shows ten per step, and the rest are in the log. */
const MaxAnnotations = 10;

/** How many run traces the action writes as files at most. */
const MaxRunTraces = 200;

/**
 * The action's settings, read from the step's inputs (INPUT_* variables, as GitHub passes them) and the job's own
 * variables, checked. Throws an Error naming the input when one is unusable.
 */
export function readInputs(env) {
  const input = (name) => (env[`INPUT_${name.replace(/ /g, "_").toUpperCase()}`] ?? "").trim();
  const list = (name) =>
    input(name)
      .split(/[\n,]/)
      .map((s) => s.trim())
      .filter((s) => s.length > 0);
  const flag = (name, fallback) => {
    const value = input(name).toLowerCase();
    if (value === "") return fallback;
    if (value === "true") return true;
    if (value === "false") return false;
    throw new Error(`Input '${name}' is '${input(name)}'; give true or false.`);
  };
  const number = (name, fallback, min, max) => {
    const value = input(name);
    if (value === "") return fallback;
    const parsed = Number(value);
    if (!Number.isFinite(parsed) || parsed < min || parsed > max) {
      throw new Error(`Input '${name}' is '${value}'; give a number from ${min} to ${max}.`);
    }
    return parsed;
  };

  const controlPlane = input("control-plane").replace(/\/+$/, "");
  if (controlPlane === "") {
    throw new Error("Input 'control-plane' is required: the control plane's address, such as https://sqlflow.example.com.");
  }
  let address;
  try {
    address = new URL(controlPlane);
  } catch {
    throw new Error(`Input 'control-plane' is '${controlPlane}', which is not an address.`);
  }
  const local = ["localhost", "127.0.0.1", "[::1]"].includes(address.hostname);
  if (address.protocol !== "https:" && !(address.protocol === "http:" && local)) {
    throw new Error("Input 'control-plane' must be an https address: the credential the action holds must never cross the network in clear text.");
  }

  const auth = (input("auth") || "github").toLowerCase();
  if (auth !== "github" && auth !== "key") {
    throw new Error(`Input 'auth' is '${input("auth")}'; give github (the job's OpenID Connect token) or key.`);
  }
  const key = input("key");
  if (auth === "key" && key === "") {
    throw new Error("Input 'key' is required when 'auth' is key: pass the trigger's key from a secret.");
  }

  const commit = input("commit").toLowerCase();
  if (commit !== "" && !/^[0-9a-f]{7,64}$/.test(commit)) {
    throw new Error(`Input 'commit' is '${input("commit")}', which is not a git commit id.`);
  }

  const traces = input("traces");
  const defaultKey = [env.GITHUB_RUN_ID, env.GITHUB_RUN_ATTEMPT, env.GITHUB_JOB, env.GITHUB_ACTION].filter(Boolean).join("-");
  return {
    controlPlane,
    auth,
    key,
    audience: input("audience") || "sqlflow",
    trigger: input("trigger") || null,
    repo: input("repo") || null,
    // A commit asks for the repository to be synced at it, so without a sync there is none to ask for.
    commit: flag("sync", true) ? commit || null : null,
    sync: flag("sync", true),
    schedules: list("schedules"),
    batch: list("batch"),
    chain: flag("chain", true),
    wait: flag("wait", true),
    timeoutMinutes: number("timeout-minutes", 120, 1, 4320),
    pollSeconds: number("poll-seconds", 5, 1, 300),
    summary: flag("summary", true),
    traces: traces === "" ? null : traces,
    cancelOnTimeout: flag("cancel-on-timeout", false),
    requestKey: (input("request-key") || defaultKey || "").slice(0, 200) || null,
  };
}

/** A control plane's error answer, with its status and the problem it described. */
export class ControlPlaneError extends Error {
  constructor(status, title, detail) {
    super(`${title}${detail ? `: ${detail}` : ""} (HTTP ${status})`);
    this.status = status;
    this.title = title;
    this.detail = detail;
  }
}

/**
 * A client of one control plane that holds a trigger's credential: it gets one by exchanging the job's proof, renews
 * it before it lapses or when it is refused, and retries a call that failed for a reason worth retrying (the network, a
 * 429, a 5xx) with a growing pause, so a control plane restarting during a long follow does not fail the job.
 */
export class Client {
  constructor({ inputs, env, fetch, sleep, now, log }) {
    this.inputs = inputs;
    this.env = env;
    this.fetch = fetch;
    this.sleep = sleep;
    this.now = now;
    this.log = log;
    this.token = null;
    this.expires = 0;
    this.identity = null;
  }

  /** Exchanges the job's proof (its GitHub token, or the key) for a credential. */
  async signIn() {
    let body;
    let path;
    if (this.inputs.auth === "github") {
      const url = this.env.ACTIONS_ID_TOKEN_REQUEST_URL;
      const requestToken = this.env.ACTIONS_ID_TOKEN_REQUEST_TOKEN;
      if (!url || !requestToken) {
        throw new Error(
          "The job cannot get a GitHub token: give the job 'permissions: id-token: write' (and contents: read), or use auth: key.",
        );
      }
      const separator = url.includes("?") ? "&" : "?";
      const response = await this.fetch(`${url}${separator}audience=${encodeURIComponent(this.inputs.audience)}`, {
        headers: { Authorization: `bearer ${requestToken}`, Accept: "application/json" },
      });
      if (!response.ok) {
        throw new Error(`GitHub refused to issue the job's token (HTTP ${response.status}).`);
      }
      const minted = await response.json();
      if (!minted?.value) {
        throw new Error("GitHub answered without a token.");
      }
      this.log.mask(minted.value);
      body = { token: minted.value, trigger: this.inputs.trigger };
      path = "/api/v1/auth/trigger/github";
    } else {
      body = { key: this.inputs.key };
      path = "/api/v1/auth/trigger/key";
    }

    const session = await this.send("POST", path, body, { anonymous: true });
    this.log.mask(session.accessToken);
    this.token = session.accessToken;
    this.expires = this.now() + session.expiresIn * 1000;
    this.identity = session.trigger;
    return session.trigger;
  }

  /** Sends a call and answers its JSON (or its text when asked); throws ControlPlaneError for a refusal. */
  async send(method, path, body, { anonymous = false, text = false, attempts = 6 } = {}) {
    let pause = 2000;
    for (let attempt = 1; ; attempt++) {
      if (!anonymous && (this.token === null || this.expires - this.now() < 120_000)) {
        await this.signIn();
      }

      let response;
      try {
        response = await this.fetch(`${this.inputs.controlPlane}${path}`, {
          method,
          headers: {
            Accept: text ? "text/plain, text/markdown" : "application/json",
            ...(body === undefined ? {} : { "Content-Type": "application/json" }),
            ...(anonymous ? {} : { Authorization: `Bearer ${this.token}` }),
          },
          body: body === undefined ? undefined : JSON.stringify(body),
        });
      } catch (error) {
        if (attempt >= attempts) {
          throw new Error(`The control plane could not be reached at ${this.inputs.controlPlane}: ${error.message}`);
        }
        this.log.info(`The control plane could not be reached (${error.message}); trying again in ${pause / 1000} s.`);
        await this.sleep(pause);
        pause = Math.min(pause * 2, 30_000);
        continue;
      }

      if (response.status === 401 && !anonymous && attempt < attempts) {
        // The credential lapsed or was refused: exchange the proof again once, then repeat the call.
        this.token = null;
        continue;
      }

      if ((response.status === 429 || response.status >= 500) && attempt < attempts) {
        this.log.info(`The control plane answered ${response.status}; trying again in ${pause / 1000} s.`);
        await this.sleep(pause);
        pause = Math.min(pause * 2, 30_000);
        continue;
      }

      if (!response.ok) {
        let problem = {};
        try {
          problem = await response.json();
        } catch {
          // A body that is not a problem document: the status says enough.
        }
        throw new ControlPlaneError(response.status, problem.title ?? response.statusText ?? "Refused", problem.detail ?? "");
      }

      return text ? await response.text() : await response.json();
    }
  }
}

/** Whether a trace event is a line the request copied from its repository sync's own trace (step `sync:<its step>`). */
export function isSyncLine(event) {
  return typeof event.step === "string" && event.step.startsWith("sync:");
}

/** A trace event as one log line: its time, its step (a sync line's own step), its message. */
export function formatEvent(event) {
  const time = String(event.timestampUtc ?? "").slice(11, 19);
  const step = isSyncLine(event) ? event.step.slice("sync:".length) : event.step;
  return `${time} ${step ? `${step}: ` : ""}${event.message}`;
}

/** Escapes a value for a workflow command (::error:: and the like), so a message cannot end the command early. */
export function escapeCommand(value) {
  return String(value).replace(/%/g, "%25").replace(/\r/g, "%0D").replace(/\n/g, "%0A");
}

/**
 * Runs the action: signs in, makes the request, follows it (unless asked not to wait), and reports it. Answers the exit
 * code. Every side effect goes through `deps`: fetch, sleep, now, the log, the outputs, the summary and the file system.
 */
export async function run(env, deps) {
  const { log } = deps;
  let inputs;
  try {
    inputs = readInputs(env);
  } catch (error) {
    log.error(error.message);
    return ExitCodes.failure;
  }

  const client = new Client({ inputs, env, fetch: deps.fetch, sleep: deps.sleep, now: deps.now, log });
  let request;
  try {
    const trigger = await client.signIn();
    log.info(`Acting as trigger '${trigger.name}' of repository '${trigger.repoName}'.`);
    request = await client.send("POST", "/api/v1/trigger-requests", {
      repo: inputs.repo,
      commit: inputs.commit,
      sync: inputs.sync,
      schedules: inputs.schedules,
      batch: inputs.batch,
      chain: inputs.chain,
      requestKey: inputs.requestKey,
    });
  } catch (error) {
    log.error(error.message);
    return ExitCodes.failure;
  }

  await deps.saveState("request_id", request.id);
  await deps.setOutput("request-id", request.id);
  if (request.url) await deps.setOutput("url", request.url);
  log.info(`Trigger request ${request.id}${request.url ? ` (${request.url})` : ""}.`);
  if (!inputs.wait) {
    await deps.setOutput("state", request.state);
    log.info("Not waiting for it to end (wait: false).");
    return ExitCodes.success;
  }

  const deadline = deps.now() + inputs.timeoutMinutes * 60_000;
  let cursor = 0;
  let inSyncGroup = false;
  let annotations = 0;
  const lines = [];
  try {
    for (;;) {
      const page = await client.send("GET", `/api/v1/trigger-requests/${request.id}/events?after=${cursor}`);
      for (const event of page.events) {
        const line = formatEvent(event);
        lines.push(line);
        const syncLine = isSyncLine(event);
        if (syncLine && !inSyncGroup) {
          log.group("Repository sync");
          inSyncGroup = true;
        } else if (!syncLine && inSyncGroup) {
          log.endGroup();
          inSyncGroup = false;
        }

        if (event.level === "error" && !syncLine && annotations < MaxAnnotations && !event.terminal) {
          annotations++;
          log.annotate("error", line);
        } else if (event.level === "warning" && !syncLine && annotations < MaxAnnotations) {
          annotations++;
          log.annotate("warning", line);
        } else {
          log.info(line);
        }
      }

      cursor = page.next;
      if (page.done) break;
      if (deps.now() >= deadline) {
        if (inSyncGroup) log.endGroup();
        inSyncGroup = false;
        if (inputs.cancelOnTimeout) {
          await client.send("POST", `/api/v1/trigger-requests/${request.id}/cancel`);
          log.error(`The request was still running after ${inputs.timeoutMinutes} minutes and was cancelled.`);
        } else {
          log.error(`The request is still running after ${inputs.timeoutMinutes} minutes; follow it at ${request.url ?? request.id}.`);
        }
        await report(client, inputs, deps, request.id, lines);
        return ExitCodes.failure;
      }
      if (page.events.length === 0) await deps.sleep(inputs.pollSeconds * 1000);
    }
  } catch (error) {
    if (inSyncGroup) log.endGroup();
    log.error(`Following the request failed: ${error.message}`);
    return ExitCodes.failure;
  }

  if (inSyncGroup) log.endGroup();
  const detail = await report(client, inputs, deps, request.id, lines);
  const outcome = detail?.request;
  if (!outcome) return ExitCodes.failure;
  if (outcome.conclusion === "success") {
    log.info(`The request succeeded: ${outcome.detail ?? "done"}`);
    return ExitCodes.success;
  }

  log.annotate("error", `The request ${outcome.conclusion === "cancelled" ? "was cancelled" : "failed"}: ${outcome.detail ?? outcome.state}`);
  return ExitCodes.failure;
}

/** Reads the request's end and reports it: outputs, the summary on the job's page, and the traces as files. */
async function report(client, inputs, deps, id, lines) {
  const { log } = deps;
  let detail;
  try {
    detail = await client.send("GET", `/api/v1/trigger-requests/${id}`);
  } catch (error) {
    log.error(`The request's outcome could not be read: ${error.message}`);
    return null;
  }

  const request = detail.request;
  await deps.setOutput("state", request.state);
  await deps.setOutput("conclusion", request.conclusion ?? "");
  if (request.syncedCommit) await deps.setOutput("synced-commit", request.syncedCommit);

  if (inputs.summary) {
    try {
      const markdown = await client.send("GET", `/api/v1/trigger-requests/${id}/summary`, undefined, { text: true });
      await deps.appendSummary(markdown);
    } catch (error) {
      log.warning(`The summary could not be written: ${error.message}`);
    }
  }

  if (inputs.traces) {
    try {
      await mkdir(join(inputs.traces, "runs"), { recursive: true });
      await writeFile(join(inputs.traces, "request.log"), lines.join("\n") + "\n", "utf8");
      for (const run of detail.runs.slice(0, MaxRunTraces)) {
        const text = await client.send("GET", `/api/v1/trigger-requests/${id}/runs/${run.runId}/trace`, undefined, { text: true });
        const safe = run.flowName.replace(/[^A-Za-z0-9._-]/g, "_");
        await writeFile(join(inputs.traces, "runs", `${safe}-${run.runId}.log`), text, "utf8");
      }
      if (detail.runs.length > MaxRunTraces) {
        log.warning(`Only the first ${MaxRunTraces} of ${detail.runs.length} run traces were written.`);
      }
      await deps.setOutput("traces", inputs.traces);
    } catch (error) {
      log.warning(`The traces could not all be written: ${error.message}`);
    }
  }

  return detail;
}

/** Cancels the request the main step made, for the post step of a cancelled job. Answers the exit code. */
export async function cancel(env, deps) {
  const id = env.STATE_request_id;
  if (!id) return ExitCodes.success;
  let inputs;
  try {
    inputs = readInputs(env);
  } catch (error) {
    deps.log.error(error.message);
    return ExitCodes.failure;
  }

  const client = new Client({ inputs, env, fetch: deps.fetch, sleep: deps.sleep, now: deps.now, log: deps.log });
  try {
    await client.send("POST", `/api/v1/trigger-requests/${id}/cancel`);
    deps.log.info(`The job was cancelled, so trigger request ${id} was cancelled too.`);
  } catch (error) {
    if (error instanceof ControlPlaneError && error.status === 409) {
      deps.log.info(`Trigger request ${id} had already ended.`);
      return ExitCodes.success;
    }
    deps.log.warning(`Trigger request ${id} could not be cancelled: ${error.message}`);
  }
  return ExitCodes.success;
}

/** The real side effects on a GitHub runner: workflow commands on stdout, and the files GitHub names in its variables. */
export function runnerDeps(env) {
  const write = (line) => process.stdout.write(`${line}\n`);
  const appendTo = async (variable, text) => {
    if (env[variable]) await appendFile(env[variable], text, "utf8");
  };
  const delimited = (name, value) => {
    const marker = `sqlflow_${Math.random().toString(36).slice(2)}`;
    return `${name}<<${marker}\n${value}\n${marker}\n`;
  };
  return {
    fetch: globalThis.fetch,
    sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
    now: () => Date.now(),
    setOutput: (name, value) => appendTo("GITHUB_OUTPUT", delimited(name, value)),
    saveState: (name, value) => appendTo("GITHUB_STATE", delimited(name, value)),
    appendSummary: (markdown) => appendTo("GITHUB_STEP_SUMMARY", `${markdown}\n`),
    log: {
      info: (message) => write(message),
      warning: (message) => write(`::warning::${escapeCommand(message)}`),
      error: (message) => write(`::error::${escapeCommand(message)}`),
      annotate: (level, message) => write(`::${level}::${escapeCommand(message)}`),
      group: (title) => write(`::group::${escapeCommand(title)}`),
      endGroup: () => write("::endgroup::"),
      mask: (secret) => write(`::add-mask::${escapeCommand(secret)}`),
    },
  };
}
