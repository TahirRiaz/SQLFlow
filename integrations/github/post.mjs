// Entry point of the action's post step, which runs only when the job was cancelled: cancels the request the main step made.
import { cancel, runnerDeps } from "./lib.mjs";

process.exitCode = await cancel(process.env, runnerDeps(process.env));
