#!/bin/sh
# Composes the `sqlflow worker` invocation from the container's environment. The catalog connection itself is the
# CLI default (${env:SQLFLOW_CATALOG_DB}), so it never appears on the command line or in `ps` output.
set -e

set -- worker
if [ -n "${SQLFLOW_WORKER_POOL:-}" ]; then
  set -- "$@" --pool "$SQLFLOW_WORKER_POOL"
fi
if [ -n "${SQLFLOW_WORKER_POLL_SECONDS:-}" ]; then
  set -- "$@" --poll-seconds "$SQLFLOW_WORKER_POLL_SECONDS"
fi
# How long a stopping node keeps finishing the runs it already claimed. Keep it below the orchestrator's
# termination grace period (Container Apps / Kubernetes terminationGracePeriodSeconds), or the platform's kill
# lands mid-drain and severs the work the drain exists to save.
if [ -n "${SQLFLOW_WORKER_DRAIN_SECONDS:-}" ]; then
  set -- "$@" --drain-seconds "$SQLFLOW_WORKER_DRAIN_SECONDS"
fi

exec dotnet /app/sqlflow.dll "$@"
