#!/usr/bin/env bash
# Restores Microsoft's official AdventureWorksDW2022 sample (the dimensional/data-warehouse edition:
# DimCustomer, DimProduct, DimReseller, DimDate, FactResellerSales, ...) into the compose stack's SQL
# Server, so PowerAI/lineage work has a real Sql.Database-backed sample to extract and resolve against.
# The DW edition, not the OLTP AdventureWorksLT edition, is the one whose shape actually matches
# samples/powerbi/AdventureWorks Sales.pbix's model (Customer/Product/Reseller/Sales/Sales
# Territory/Date), whose own Power Query sources are Excel-sourced and so never resolve to a warehouse
# object today. Runs as a one-shot init container (the "adventureworks-init" service in
# deploy/compose/docker-compose.yml), never inside the long-running mssql or controlplane containers.
#
# Idempotent: does nothing once the AdventureWorks database already exists, so re-running
# `docker compose up` never re-downloads or re-restores.
set -euo pipefail

SQLCMD=/opt/mssql-tools18/bin/sqlcmd
BAK_URL="https://github.com/Microsoft/sql-server-samples/releases/download/adventureworks/AdventureWorksDW2022.bak"
BAK_PATH="/var/opt/mssql/backup/AdventureWorksDW2022.bak"
DB_NAME="AdventureWorks"

wait_for_sql() {
    local attempt
    for attempt in $(seq 1 60); do
        if "$SQLCMD" -S "$MSSQL_HOST" -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "SELECT 1" -b >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    echo "adventureworks-restore: SQL Server never became reachable at $MSSQL_HOST" >&2
    return 1
}

already_restored() {
    "$SQLCMD" -S "$MSSQL_HOST" -U sa -P "$MSSQL_SA_PASSWORD" -C -h -1 -Q \
        "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$DB_NAME'" -b 2>/dev/null \
        | tr -d '[:space:]' | grep -qx '1'
}

wait_for_sql

if already_restored; then
    echo "adventureworks-restore: '$DB_NAME' already present, nothing to do."
    exit 0
fi

echo "adventureworks-restore: downloading AdventureWorksDW2022.bak..."
mkdir -p "$(dirname "$BAK_PATH")"
wget --tries=5 --waitretry=5 -q -O "$BAK_PATH" "$BAK_URL"

echo "adventureworks-restore: reading logical file names from the backup header..."
FILELIST=$("$SQLCMD" -S "$MSSQL_HOST" -U sa -P "$MSSQL_SA_PASSWORD" -C -h -1 -s '|' -Q \
    "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$BAK_PATH'" -b)

# Column 3 of RESTORE FILELISTONLY is the file Type ('D' data, 'L' log) per Microsoft's documented
# result-set schema; matching on that rather than the physical file extension is robust to the
# physical path being Windows-style (the backup was taken on Windows, so it always is).
DATA_LOGICAL=$(echo "$FILELIST" | awk -F'|' '{gsub(/^[ \t]+|[ \t]+$/, "", $3); gsub(/[ \t]+$/, "", $1)} $3 == "D" {print $1; exit}')
LOG_LOGICAL=$(echo "$FILELIST" | awk -F'|' '{gsub(/^[ \t]+|[ \t]+$/, "", $3); gsub(/[ \t]+$/, "", $1)} $3 == "L" {print $1; exit}')

if [ -z "$DATA_LOGICAL" ] || [ -z "$LOG_LOGICAL" ]; then
    echo "adventureworks-restore: could not parse logical file names from RESTORE FILELISTONLY output:" >&2
    echo "$FILELIST" >&2
    exit 1
fi

echo "adventureworks-restore: restoring '$DB_NAME' (data='$DATA_LOGICAL', log='$LOG_LOGICAL')..."
"$SQLCMD" -S "$MSSQL_HOST" -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "
RESTORE DATABASE [$DB_NAME]
FROM DISK = N'$BAK_PATH'
WITH MOVE N'$DATA_LOGICAL' TO N'/var/opt/mssql/data/${DB_NAME}.mdf',
     MOVE N'$LOG_LOGICAL' TO N'/var/opt/mssql/data/${DB_NAME}_log.ldf',
     REPLACE, STATS = 10;" -b

echo "adventureworks-restore: done."
