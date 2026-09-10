import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * Builds the e2e fixture: a real local git repository holding one CSV flow (copied from samples/), which the
 * suite registers as a repo source through the GUI. The control plane then syncs it exactly as it would a
 * customer's remote, so pipelines, runs, schedules, lineage, and search are all exercised against real data
 * flowing through the product's own path.
 */
export default function globalSetup(): void {
  const here = import.meta.dirname;
  const fixturesDir = resolve(here, ".fixtures");
  const repoDir = join(fixturesDir, "e2e-repo");
  const samplesDir = resolve(here, "..", "..", "samples", "csv");

  rmSync(repoDir, { recursive: true, force: true });
  mkdirSync(join(repoDir, "data"), { recursive: true });

  // The target table is unique per suite run: the first triggered run then always CREATEs it, so the run
  // detail's statements drill-down deterministically has DDL to show (appends record no statements).
  const tableTag = `E2E_${Date.now().toString(36)}`;
  const flowYaml = readFileSync(join(samplesDir, "csv-basic.flow.yaml"), "utf8")
    .replace("table: Csv_Basic", `table: Csv_Basic_${tableTag}`);
  writeFileSync(join(repoDir, "csv-basic.flow.yaml"), flowYaml);
  cpSync(join(samplesDir, "data", "orders.csv"), join(repoDir, "data", "orders.csv"));

  const git = (...args: string[]) =>
    execFileSync("git", args, { cwd: repoDir, stdio: "pipe" }).toString("utf8").trim();

  git("init", "-b", "main");
  git("config", "user.email", "e2e@sqlflow.test");
  git("config", "user.name", "SQLFlow E2E");
  git("add", "-A");
  git("commit", "-m", "e2e fixture: basic csv flow");
  const headSha = git("rev-parse", "HEAD");

  // Tests read the repo path and the exact commit to expect from this meta file (globalSetup runs in a
  // separate process from the specs). Waiting on THIS sha makes re-runs deterministic: a stale synced sha from
  // a previous suite run never satisfies the seed assertions.
  writeFileSync(
    join(fixturesDir, "meta.json"),
    JSON.stringify({ repoDir: repoDir.replace(/\\/g, "/"), headSha }, null, 2),
  );

  if (!existsSync(join(repoDir, ".git"))) {
    throw new Error(`Fixture repo was not initialized at ${repoDir}.`);
  }
}
