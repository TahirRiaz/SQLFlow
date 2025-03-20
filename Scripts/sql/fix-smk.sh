#!/bin/bash

# Script to fix Service Master Key issues after SQL Server startup
# To be run manually if needed

log() {
    echo "$(date +"%Y-%m-%d %H:%M:%S") [sqlflow-fix-smk] $*"
}

log "Attempting to fix Service Master Key issues..."

# Wait for SQL Server to be responsive
attempts=0
max_attempts=30
until /opt/mssql-tools/bin/sqlcmd -S localhost -U SA -P "$MSSQL_SA_PASSWORD" -Q "SELECT @@SERVERNAME" -t 5 > /dev/null 2>&1 || [ $attempts -eq $max_attempts ]
do
    log "Waiting for SQL Server to start... ($((++attempts))/$max_attempts)"
    sleep 2
done

if [ $attempts -eq $max_attempts ]; then
    log "SQL Server did not start in time. Trying to force regenerate Service Master Key anyway..."
    # Try to force regenerate even if we couldn't connect
    /opt/mssql-tools/bin/sqlcmd -S localhost -U SA -P "$MSSQL_SA_PASSWORD" -Q "ALTER SERVICE MASTER KEY FORCE REGENERATE;" -b -t 30
    if [ $? -eq 0 ]; then
        log "Service Master Key regeneration attempted."
        exit 0
    else
        log "Failed to regenerate Service Master Key."
        exit 1
    fi
fi

log "SQL Server is running. Attempting to regenerate Service Master Key..."
/opt/mssql-tools/bin/sqlcmd -S localhost -U SA -P "$MSSQL_SA_PASSWORD" -Q "ALTER SERVICE MASTER KEY FORCE REGENERATE;" -b -t 30

if [ $? -eq 0 ]; then
    log "Service Master Key regenerated successfully."
    exit 0
else
    log "Failed to regenerate Service Master Key."
    exit 1
fi