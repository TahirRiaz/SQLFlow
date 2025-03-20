#!/bin/bash
set -e

# Logging function
log() {
    echo "$(date +"%Y-%m-%d %H:%M:%S") [sqlflow-init] $*"
}

# Wait until SQL Server is up and running
function wait_for_sql() {
    log "Waiting for SQL Server to start up..."
    for i in {1..60}; do
        if /opt/mssql-tools/bin/sqlcmd -S localhost -U SA -P "${MSSQL_SA_PASSWORD}" -Q "SELECT 1" &>/dev/null; then
            log "SQL Server is up and running."
            return 0
        fi
        log "SQL Server not ready yet (attempt $i of 60)..."
        sleep 2
    done
    log "Could not connect to SQL Server after multiple attempts. Check SQL Server logs."
    return 1
}

# Main initialization logic
log "Starting initialization process..."

# Wait for SQL Server to be available
wait_for_sql

# Check if Service Master Key needs regeneration
log "Checking Service Master Key status..."
if /opt/mssql-tools/bin/sqlcmd -S localhost -U SA -P "${MSSQL_SA_PASSWORD}" -Q "SELECT TOP 1 name FROM sys.databases" &>/dev/null; then
    log "Service Master Key appears to be working correctly."
else
    log "Service Master Key issue detected. Attempting to regenerate..."
    /opt/mssql-tools/bin/sqlcmd -S localhost -U SA -P "${MSSQL_SA_PASSWORD}" -b -Q "ALTER SERVICE MASTER KEY FORCE REGENERATE;"
    if [ $? -eq 0 ]; then
        log "Service Master Key regenerated successfully."
    else
        log "Warning: Failed to regenerate Service Master Key."
    fi
fi

log "Initialization complete."