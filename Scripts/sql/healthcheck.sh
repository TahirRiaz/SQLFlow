#!/bin/bash

# Simple healthcheck script for SQL Server container
# Exit codes: 0 = healthy, 1 = unhealthy

# Check if SQL Server is running first
if ! pgrep -x "sqlservr" > /dev/null; then
    echo "SQL Server process is not running"
    exit 1
fi

# Try to connect to SQL Server
if /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT 1" -C -b -N -t 5 > /dev/null 2>&1; then
    echo "SQL Server is healthy"
    exit 0
else
    echo "SQL Server connectivity check failed"
    exit 1
fi