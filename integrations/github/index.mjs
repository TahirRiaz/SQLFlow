// Entry point of the action's main step: sync a repository, fire its schedules, follow and report the request.
import { run, runnerDeps } from "./lib.mjs";

process.exitCode = await run(process.env, runnerDeps(process.env));
