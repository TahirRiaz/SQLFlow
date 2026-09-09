#!/usr/bin/env bash
# Downloads the Sakila sample database SQL (schema + data) for PostgreSQL, MySQL, and Oracle into the
# per-engine initdb directories, where docker-compose mounts them as first-boot initialization scripts.
#
# The scripts come from the jOOQ/sakila mirror, which publishes the same Sakila schema for every engine, so
# the three source databases end up with identical tables (actor, film, customer, ...). Existing files are
# left in place unless FORCE=1. The Oracle scripts are prefixed with a CURRENT_SCHEMA directive so they load
# into the dedicated SAKILA schema created by 00_create_sakila_schema.sql rather than SYSTEM.
#
# Usage:
#   ./fetch-samples.sh              # fetch missing files
#   FORCE=1 ./fetch-samples.sh      # re-download everything
#   REF=1.0 ./fetch-samples.sh      # pull a specific jOOQ/sakila ref (default: main)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ref="${REF:-main}"
force="${FORCE:-0}"
base="https://raw.githubusercontent.com/jOOQ/sakila/${ref}"

# MySQL and PostgreSQL run their scripts directly, in filename order. The Oracle scripts are pulled in by the
# committed wrapper 10_load_sakila.sql (which handles SET DEFINE OFF and schema targeting), so they land in a
# sql/ subfolder the image does not auto-execute and are fetched verbatim.
# rows: <url>|<relative path>
rows=(
  "${base}/mysql-sakila-db/mysql-sakila-schema.sql|mysql/initdb/01_sakila_schema.sql"
  "${base}/mysql-sakila-db/mysql-sakila-insert-data.sql|mysql/initdb/02_sakila_data.sql"
  "${base}/postgres-sakila-db/postgres-sakila-schema.sql|postgres/initdb/01_sakila_schema.sql"
  "${base}/postgres-sakila-db/postgres-sakila-insert-data.sql|postgres/initdb/02_sakila_data.sql"
  "${base}/oracle-sakila-db/oracle-sakila-schema.sql|oracle/sakila-sql/oracle-sakila-schema.sql"
  "${base}/oracle-sakila-db/oracle-sakila-insert-data.sql|oracle/sakila-sql/oracle-sakila-data.sql"
)

for row in "${rows[@]}"; do
  IFS='|' read -r url rel <<< "$row"
  target="${root}/${rel}"
  if [[ -f "$target" && "$force" != "1" ]]; then
    echo "skip   ${rel} (already present; set FORCE=1 to refresh)"
    continue
  fi

  mkdir -p "$(dirname "$target")"
  echo "fetch  ${rel}  <-  ${url}"
  curl -fsSL "$url" -o "$target"
done

echo ''
echo 'Sample SQL is ready under docker/*/initdb. Start the databases with docker/up.sh.'
