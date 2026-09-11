#!/bin/sh
# Composes the `sqlflow worker` invocation from the container's environment. The control plane URL and the node
# token come from SQLFLOW_URL and SQLFLOW_TOKEN, both read by the CLI itself, so neither ever appears on the
# command line or in `ps` output. The node needs no catalog connection: the run's definition, its snapshotted YAML,
# its lineage context and its live trace all travel over the node protocol.
set -e

set -- worker
if [ -n "${SQLFLOW_WORKER_POOL:-}" ]; then
  set -- "$@" --pool "$SQLFLOW_WORKER_POOL"
fi
if [ -n "${SQLFLOW_WORKER_POLL_SECONDS:-}" ]; then
  set -- "$@" --poll-seconds "$SQLFLOW_WORKER_POLL_SECONDS"
fi
# How long a stopping node keeps finishing the runs it already holds. Keep it below the orchestrator's
# termination grace period (Container Apps / Kubernetes terminationGracePeriodSeconds), or the platform's kill
# lands mid-drain and severs the work the drain exists to save.
if [ -n "${SQLFLOW_WORKER_DRAIN_SECONDS:-}" ]; then
  set -- "$@" --drain-seconds "$SQLFLOW_WORKER_DRAIN_SECONDS"
fi

exec dotnet /app/sqlflow.dll "$@"
