// The sync-and-fire action against a scripted control plane: what it reads from its inputs, how it signs in and stays
// signed in, how it follows a request and reports it, and how it ends.

import { strict as assert } from "node:assert";
import { mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { cancel, ControlPlaneError, escapeCommand, formatEvent, readInputs, run } from "../lib.mjs";

const Base = "https://cp.example.com";

function env(overrides = {}) {
  return {
    "INPUT_CONTROL-PLANE": Base,
    INPUT_SCHEDULES: "nightly, hourly",
    INPUT_COMMIT: "0123456789abcdef0123456789abcdef01234567",
    INPUT_AUDIENCE: "https://cp.example.com",
    ACTIONS_ID_TOKEN_REQUEST_URL: "https://pipelines.actions.githubusercontent.com/token?api-version=2.0",
    ACTIONS_ID_TOKEN_REQUEST_TOKEN: "runtime-token",
    GITHUB_RUN_ID: "77",
    GITHUB_RUN_ATTEMPT: "1",
    GITHUB_JOB: "deliver",
    GITHUB_ACTION: "__run",
    ...overrides,
  };
}

/** A control plane (and GitHub's token service) answering from a script, recording every call. */
function controlPlane(script = {}) {
  const calls = [];
  const pages = [...(script.pages ?? [{ events: [event(1, "request", "asked")], next: 1, done: true }])];
  const handler = async (url, init = {}) => {
    const method = init.method ?? "GET";
    const body = init.body ? JSON.parse(init.body) : undefined;
    const call = { method, url, body, headers: init.headers ?? {} };
    calls.push(call);
    const custom = script.answer?.(call, calls);
    if (custom) return custom;
    if (url.startsWith("https://pipelines.actions.githubusercontent.com/token")) {
      return json(200, { value: "github-oidc-token" });
    }
    const path = url.slice(Base.length);
    if (path === "/api/v1/auth/trigger/github" || path === "/api/v1/auth/trigger/key") {
      return json(200, {
        accessToken: `cp-token-${calls.filter((c) => c.url.includes("/auth/trigger/")).length}`,
        tokenType: "Bearer",
        expiresIn: script.expiresIn ?? 3600,
        trigger: { id: "t1", name: "deliver", repoName: "estate", allSchedules: false, schedules: ["nightly", "hourly"] },
      });
    }
    if (method === "POST" && path === "/api/v1/trigger-requests") {
      return json(script.createStatus ?? 202, script.createBody ?? { id: "r1", state: "syncing", url: "https://gui.example.com/r1" });
    }
    if (path.startsWith("/api/v1/trigger-requests/r1/events")) {
      return json(200, pages.length > 1 ? pages.shift() : pages[0]);
    }
    if (path === "/api/v1/trigger-requests/r1") {
      return json(200, script.detail ?? detail("success"));
    }
    if (path === "/api/v1/trigger-requests/r1/summary") {
      return text(200, "## ✅ estate: synced");
    }
    if (path.startsWith("/api/v1/trigger-requests/r1/runs/")) {
      return text(200, `trace of ${path.split("/")[6]}`);
    }
    if (method === "POST" && path === "/api/v1/trigger-requests/r1/cancel") {
      return json(script.cancelStatus ?? 202, { id: "r1", state: "syncing" });
    }
    return json(404, { title: "Not found", detail: path });
  };
  return { calls, fetch: handler };
}

function json(status, value) {
  return { ok: status >= 200 && status < 300, status, statusText: String(status), json: async () => value, text: async () => JSON.stringify(value) };
}

function text(status, value) {
  return { ok: status >= 200 && status < 300, status, statusText: String(status), json: async () => JSON.parse(value), text: async () => value };
}

function event(id, step, message, level = "info", terminal = false) {
  return { id, step, message, level, terminal, timestampUtc: "2026-10-26T10:11:12Z" };
}

function detail(conclusion) {
  return {
    request: {
      id: "r1",
      state: conclusion === "success" ? "succeeded" : conclusion === "cancelled" ? "cancelled" : "failed",
      conclusion,
      detail: conclusion === "success" ? "All 2 schedules succeeded." : "Schedule 'hourly' did not succeed.",
      syncedCommit: "0123456789abcdef0123456789abcdef01234567",
    },
    runs: [
      { runId: "run-a", flowName: "land wells", status: "succeeded" },
      { runId: "run-b", flowName: "deliver", status: "failed" },
    ],
  };
}

/** The side effects the action makes, recorded. */
function recorder(clock = { now: 0 }) {
  const out = { lines: [], outputs: {}, state: {}, summary: [], sleeps: [] };
  return {
    out,
    deps: (fetch) => ({
      fetch,
      sleep: async (ms) => {
        out.sleeps.push(ms);
        clock.now += ms;
      },
      now: () => clock.now,
      setOutput: async (name, value) => {
        out.outputs[name] = value;
      },
      saveState: async (name, value) => {
        out.state[name] = value;
      },
      appendSummary: async (markdown) => {
        out.summary.push(markdown);
      },
      log: {
        info: (m) => out.lines.push(m),
        warning: (m) => out.lines.push(`::warning::${m}`),
        error: (m) => out.lines.push(`::error::${m}`),
        annotate: (level, m) => out.lines.push(`::${level}::${m}`),
        group: (title) => out.lines.push(`::group::${title}`),
        endGroup: () => out.lines.push("::endgroup::"),
        mask: (secret) => out.lines.push(`::add-mask::${secret}`),
      },
    }),
  };
}

test("inputs: the schedules, the commit and a key for the request are read and checked", () => {
  const inputs = readInputs(env({ INPUT_SCHEDULES: "nightly\nhourly,, weekly ", INPUT_BATCH: "small" }));
  assert.deepEqual(inputs.schedules, ["nightly", "hourly", "weekly"]);
  assert.deepEqual(inputs.batch, ["small"]);
  assert.equal(inputs.commit, "0123456789abcdef0123456789abcdef01234567");
  assert.equal(inputs.requestKey, "77-1-deliver-__run");
  assert.equal(inputs.auth, "github");
  assert.equal(inputs.timeoutMinutes, 120);
  assert.equal(inputs.wait, true);
});

test("inputs: without a sync there is no commit to ask for", () => {
  const inputs = readInputs(env({ INPUT_SYNC: "false" }));
  assert.equal(inputs.sync, false);
  assert.equal(inputs.commit, null);
});

test("inputs: every unusable input is named", () => {
  assert.throws(() => readInputs(env({ "INPUT_CONTROL-PLANE": "" })), /'control-plane' is required/);
  assert.throws(() => readInputs(env({ "INPUT_CONTROL-PLANE": "http://cp.example.com" })), /must be an https address/);
  assert.doesNotThrow(() => readInputs(env({ "INPUT_CONTROL-PLANE": "http://localhost:5000" })));
  assert.throws(() => readInputs(env({ INPUT_AUTH: "password" })), /'auth' is 'password'/);
  assert.throws(() => readInputs(env({ INPUT_AUTH: "key" })), /'key' is required/);
  assert.throws(() => readInputs(env({ INPUT_COMMIT: "main" })), /not a git commit id/);
  assert.throws(() => readInputs(env({ INPUT_WAIT: "yes" })), /'wait' is 'yes'/);
  assert.throws(() => readInputs(env({ "INPUT_TIMEOUT-MINUTES": "0" })), /from 1 to 4320/);
});

test("a run signs in with the job's GitHub token, makes the request and follows it to success", async () => {
  const plane = controlPlane({
    pages: [
      { events: [event(1, "request", "trigger:deliver asked"), event(2, "sync:clone", "Checked out abc"), event(3, "sync:result", "2 added")], next: 3, done: false },
      { events: [], next: 3, done: false },
      { events: [event(4, "fire", "Fired schedule 'nightly'"), event(5, "run", "load failed: boom", "error"), event(6, "done", "All done", "info", true)], next: 6, done: true },
    ],
  });
  const { out, deps } = recorder();

  const code = await run(env(), deps(plane.fetch));

  assert.equal(code, 0);
  const oidc = plane.calls[0];
  assert.match(oidc.url, /&audience=https%3A%2F%2Fcp\.example\.com$/);
  assert.equal(oidc.headers.Authorization, "bearer runtime-token");
  const exchange = plane.calls[1];
  assert.deepEqual(exchange.body, { token: "github-oidc-token", trigger: null });
  const create = plane.calls.find((c) => c.method === "POST" && c.url.endsWith("/api/v1/trigger-requests"));
  assert.deepEqual(create.body.schedules, ["nightly", "hourly"]);
  assert.equal(create.body.requestKey, "77-1-deliver-__run");
  assert.equal(create.headers.Authorization, "Bearer cp-token-1");
  assert.ok(out.lines.includes("::add-mask::github-oidc-token"));
  assert.ok(out.lines.includes("::add-mask::cp-token-1"));
  const group = out.lines.indexOf("::group::Repository sync");
  assert.ok(group > 0);
  assert.equal(out.lines[group + 1], "10:11:12 clone: Checked out abc");
  assert.equal(out.lines[group + 3], "::endgroup::");
  assert.ok(out.lines.includes("::error::10:11:12 run: load failed: boom"));
  assert.deepEqual(out.sleeps, [5000]);
  assert.equal(out.outputs["request-id"], "r1");
  assert.equal(out.outputs.conclusion, "success");
  assert.equal(out.outputs["synced-commit"], "0123456789abcdef0123456789abcdef01234567");
  assert.equal(out.state.request_id, "r1");
  assert.deepEqual(out.summary, ["## ✅ estate: synced"]);
});

test("a request that does not succeed fails the step with its reason", async () => {
  const plane = controlPlane({ detail: detail("failure") });
  const { out, deps } = recorder();

  const code = await run(env(), deps(plane.fetch));

  assert.equal(code, 1);
  assert.ok(out.lines.includes("::error::The request failed: Schedule 'hourly' did not succeed."));
  assert.equal(out.outputs.conclusion, "failure");
});

test("a job without the id-token permission is told how to get it", async () => {
  const plane = controlPlane();
  const { out, deps } = recorder();

  const code = await run(env({ ACTIONS_ID_TOKEN_REQUEST_URL: "" }), deps(plane.fetch));

  assert.equal(code, 1);
  assert.match(out.lines.at(-1), /permissions: id-token: write/);
  assert.equal(plane.calls.length, 0);
});

test("a key signs in without GitHub", async () => {
  const plane = controlPlane();
  const { deps } = recorder();

  const code = await run(env({ INPUT_AUTH: "key", INPUT_KEY: "sqlft_secret", ACTIONS_ID_TOKEN_REQUEST_URL: "" }), deps(plane.fetch));

  assert.equal(code, 0);
  assert.deepEqual(plane.calls[0].body, { key: "sqlft_secret" });
  assert.ok(plane.calls[0].url.endsWith("/api/v1/auth/trigger/key"));
});

test("a refusal of the request fails the step with the control plane's words", async () => {
  const plane = controlPlane({ createStatus: 403, createBody: { title: "Schedule not allowed", detail: "Trigger 'deliver' may not fire 'weekly'." } });
  const { out, deps } = recorder();

  const code = await run(env(), deps(plane.fetch));

  assert.equal(code, 1);
  assert.equal(out.lines.at(-1), "::error::Schedule not allowed: Trigger 'deliver' may not fire 'weekly'. (HTTP 403)");
});

test("a control plane that is briefly unavailable is waited for, not failed on", async () => {
  let unavailable = 2;
  const plane = controlPlane({
    answer: (call) => (call.url.endsWith("/events?after=0") && unavailable-- > 0 ? json(503, { title: "Unavailable" }) : null),
  });
  const { out, deps } = recorder();

  const code = await run(env(), deps(plane.fetch));

  assert.equal(code, 0);
  assert.deepEqual(out.sleeps.slice(0, 2), [2000, 4000]);
  assert.ok(out.lines.some((l) => l.includes("answered 503; trying again in 2 s")));
});

test("a refused credential is exchanged again and the call repeated", async () => {
  let refuse = true;
  const plane = controlPlane({
    answer: (call) => {
      if (call.url.endsWith("/events?after=0") && refuse) {
        refuse = false;
        return json(401, { title: "Unauthorized" });
      }
      return null;
    },
  });
  const { deps } = recorder();

  const code = await run(env(), deps(plane.fetch));

  assert.equal(code, 0);
  const exchanges = plane.calls.filter((c) => c.url.includes("/auth/trigger/github"));
  assert.equal(exchanges.length, 2);
  const lastEvents = plane.calls.filter((c) => c.url.includes("/events")).at(-1);
  assert.equal(lastEvents.headers.Authorization, "Bearer cp-token-2");
});

test("a credential about to lapse is renewed before the next call", async () => {
  const clock = { now: 0 };
  const plane = controlPlane({
    expiresIn: 150,
    pages: [{ events: [], next: 0, done: false }, { events: [], next: 0, done: false }, { events: [event(1, "done", "ok", "info", true)], next: 1, done: true }],
  });
  const { deps } = recorder(clock);

  const code = await run(env({ "INPUT_POLL-SECONDS": "40" }), deps(plane.fetch));

  assert.equal(code, 0);
  assert.ok(plane.calls.filter((c) => c.url.includes("/auth/trigger/github")).length >= 2);
});

test("a request still running at the timeout fails the step, and is cancelled when asked", async () => {
  const running = { events: [], next: 0, done: false };
  for (const cancelOnTimeout of [false, true]) {
    const plane = controlPlane({ pages: [running] });
    const { out, deps } = recorder({ now: 0 });

    const code = await run(env({ "INPUT_TIMEOUT-MINUTES": "1", "INPUT_POLL-SECONDS": "30", "INPUT_CANCEL-ON-TIMEOUT": String(cancelOnTimeout) }), deps(plane.fetch));

    assert.equal(code, 1);
    const cancelled = plane.calls.some((c) => c.method === "POST" && c.url.endsWith("/cancel"));
    assert.equal(cancelled, cancelOnTimeout);
    assert.ok(out.lines.some((l) => l.startsWith("::error::The request") && l.includes("after 1 minutes")));
  }
});

test("the traces are written as files for an artifact", async () => {
  const folder = await mkdtemp(join(tmpdir(), "sqlflow-action-"));
  try {
    const plane = controlPlane();
    const { out, deps } = recorder();

    const code = await run(env({ INPUT_TRACES: folder }), deps(plane.fetch));

    assert.equal(code, 0);
    assert.equal(await readFile(join(folder, "request.log"), "utf8"), "10:11:12 request: asked\n");
    assert.deepEqual((await readdir(join(folder, "runs"))).sort(), ["deliver-run-b.log", "land_wells-run-a.log"]);
    assert.equal(await readFile(join(folder, "runs", "land_wells-run-a.log"), "utf8"), "trace of run-a");
    assert.equal(out.outputs.traces, folder);
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

test("without waiting, the step answers as soon as the request is made", async () => {
  const plane = controlPlane();
  const { out, deps } = recorder();

  const code = await run(env({ INPUT_WAIT: "false" }), deps(plane.fetch));

  assert.equal(code, 0);
  assert.equal(out.outputs.state, "syncing");
  assert.ok(!plane.calls.some((c) => c.url.includes("/events")));
});

test("the post step of a cancelled job cancels the request, and a request already ended is left alone", async () => {
  for (const [status, expected] of [[202, /was cancelled too/], [409, /had already ended/]]) {
    const plane = controlPlane({ cancelStatus: status });
    const { out, deps } = recorder();

    const code = await cancel(env({ STATE_request_id: "r1" }), deps(plane.fetch));

    assert.equal(code, 0);
    assert.match(out.lines.at(-1), expected);
  }

  const idle = controlPlane();
  assert.equal(await cancel(env(), recorder().deps(idle.fetch)), 0);
  assert.equal(idle.calls.length, 0);
});

test("a message cannot end a workflow command early", () => {
  assert.equal(escapeCommand("a%b\nc\rd"), "a%25b%0Ac%0Dd");
  assert.equal(formatEvent(event(1, "sync:clone", "x")), "10:11:12 clone: x");
  assert.equal(formatEvent(event(1, "fire", "Fired")), "10:11:12 fire: Fired");
  assert.ok(new ControlPlaneError(403, "Refused", "why") instanceof Error);
});
