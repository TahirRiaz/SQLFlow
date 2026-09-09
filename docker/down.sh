#!/usr/bin/env bash
# Stops the DeltaForge source databases. Pass VOLUMES=1 to also drop the data volumes so the next start
# re-seeds Sakila from scratch.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose="${root}/docker-compose.yml"

if [[ "${VOLUMES:-0}" == "1" ]]; then
  docker compose --project-directory "$root" -f "$compose" down --volumes
else
  docker compose --project-directory "$root" -f "$compose" down
fi
