#!/usr/bin/env bash
# Fetches the Sakila sample SQL (if missing) and starts the DeltaForge source databases in the background.
# Pass FORCE=1 to re-download the sample SQL first.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

FORCE="${FORCE:-0}" "${root}/fetch-samples.sh"

echo ''
echo 'Starting containers (docker compose up -d)...'
docker compose --project-directory "$root" -f "${root}/docker-compose.yml" up -d

echo ''
echo 'Waiting for health checks. Track progress with: docker compose -f docker/docker-compose.yml ps'
echo 'Oracle takes the longest to become healthy on a first run (it builds FREEPDB1 and loads Sakila).'
echo 'Connection strings are in docker/README.md.'
