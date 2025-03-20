#!/bin/bash
set -e

# Function to log messages
log() {
    echo "[$(date --rfc-3339=seconds)]: $*"
}

# Start SQL Server in the background
log "Starting SQL Server..."
/opt/mssql/bin/sqlservr &
SQLSERVER_PID=$!

# Wait for SQL Server to start (with TrustServerCertificate)
log "Waiting for SQL Server to start..."
until /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT 1" -C -b &> /dev/null
do
    log "SQL Server is starting up. Waiting..."
    sleep 5
done
log "SQL Server started successfully!"


# Add this after SQL Server has started but before running other initialization
log "Checking for SQLFlow user and creating if needed..."
cat > /tmp/create_sqlflow_user.sql << EOF
-- Check if login exists before creating
IF NOT EXISTS (SELECT * FROM sys.server_principals WHERE name = 'SQLFlow')
BEGIN
    -- Create login for SQLFlow
    CREATE LOGIN SQLFlow WITH PASSWORD = '$MSSQL_SA_PASSWORD';
    
    -- Make the login a sysadmin
    ALTER SERVER ROLE sysadmin ADD MEMBER SQLFlow;
END

-- Check if user exists in master before creating
USE master;
IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = 'SQLFlow')
BEGIN
    -- Create a user in master database mapped to this login
    CREATE USER SQLFlow FOR LOGIN SQLFlow;
END
EOF

# Execute the dynamic script
/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -i /tmp/create_sqlflow_user.sql -C -b
log "SQLFlow user setup completed!"

# Remove the temporary file with sensitive information
rm /tmp/create_sqlflow_user.sql

# Execute initial SQL script if it exists
if [ -f /var/opt/mssql/scripts/init.sql ]; then
    log "Running initial SQL setup script..."
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -i /var/opt/mssql/scripts/init.sql -C -b
    log "Initial SQL setup completed!"
fi

# Define all system databases to skip - expanded list
SYSTEM_DBS=("master" "model" "msdb" "tempdb" "msdbdata" "modeldev" "tempdev" "resource" "mssqlsystemresource")

# Function to check if database is a system database
is_system_db() {
    local db_name="$1"
    for sys_db in "${SYSTEM_DBS[@]}"; do
        if [[ "$db_name" == "$sys_db" ]]; then
            return 0  # True, it is a system database
        fi
    done
    
    # Check if it starts with any system database pattern
    if [[ "$db_name" == "model_"* ]] || [[ "$db_name" == "msdb"* ]] || [[ "$db_name" == "temp"* ]]; then
        return 0  # True, it is a system database
    fi
    
    return 1  # False, it is not a system database
}

# Get list of already attached databases
log "Getting list of currently attached databases..."
ATTACHED_DBS=$(/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT name FROM sys.databases" -C -h -1)
log "Currently attached databases: $ATTACHED_DBS"

# More robust function to extract database name from filename
extract_db_name() {
    local filename="$1"
    local basename=$(basename "$filename")
    
    # Handle common patterns
    # 1. Simple case: database.mdf → database
    if [[ "$basename" =~ ^([^_\.]+)\.mdf$ ]]; then
        echo "${BASH_REMATCH[1]}"
        return
    fi
    
    # 2. Handle duplicated names: database_database.mdf → database
    if [[ "$basename" =~ ^([^_]+)_\1\.mdf$ ]]; then
        echo "${BASH_REMATCH[1]}"
        return
    fi
    
    # 3. More complex patterns with underscores
    if [[ "$basename" =~ ^([^_]+)_ ]]; then
        # Get the first part before underscore
        echo "${BASH_REMATCH[1]}"
        return
    fi
    
    # 4. Fallback to removing extension
    echo "${basename%.mdf}"
}

# Check for existing database files first
log "Checking for existing database files..."
if ls /var/opt/mssql/data/*.mdf &> /dev/null; then
    log "Found existing database files. Attempting to attach..."
    
    # Get a list of all .mdf files
    for MDF_FILE in /var/opt/mssql/data/*.mdf; do
        # Skip system database files immediately based on filename pattern
        if [[ "$MDF_FILE" == *"/master.mdf" ]] || [[ "$MDF_FILE" == *"/model"* ]] || 
           [[ "$MDF_FILE" == *"/msdb"* ]] || [[ "$MDF_FILE" == *"/tempdb"* ]] ||
           [[ "$MDF_FILE" == *"/resource"* ]]; then
            log "Skipping system database file: $MDF_FILE"
            continue
        fi
        
        # Extract database name using our more robust function
        DB_NAME=$(extract_db_name "$MDF_FILE")
        
        log "Found MDF file: $MDF_FILE"
        log "Extracted database name: $DB_NAME"
        
        # Skip system databases
        if is_system_db "$DB_NAME"; then
            log "Skipping system database: $DB_NAME"
            continue
        fi
        
        # Check if the database name appears in the list of attached databases
        if echo "$ATTACHED_DBS" | grep -q "$DB_NAME"; then
            log "Database $DB_NAME already attached, skipping..."
            continue
        fi
        
        # Find associated log file with more flexible pattern matching
        # Try several possible patterns for log files
        LOG_FILE=""
        
        # Pattern 1: Same path structure as database name
        if [ -f "/var/opt/mssql/log/${DB_NAME}.ldf" ]; then
            LOG_FILE="/var/opt/mssql/log/${DB_NAME}.ldf"
        # Pattern 2: Database name with _log suffix
        elif [ -f "/var/opt/mssql/log/${DB_NAME}_log.ldf" ]; then
            LOG_FILE="/var/opt/mssql/log/${DB_NAME}_log.ldf"
        # Pattern 3: Search for any file that might match
        else
            LOG_FILE=$(find /var/opt/mssql/log -name "${DB_NAME}*_log.ldf" | head -1)
            if [ -z "$LOG_FILE" ]; then
                LOG_FILE=$(find /var/opt/mssql/log -name "${DB_NAME}*.ldf" | head -1)
            fi
        fi
        
        if [ -z "$LOG_FILE" ]; then
            log "Warning: Could not find log file for $DB_NAME. Looking for any log file..."
            LOG_FILE=$(find /var/opt/mssql/log -name "*.ldf" | head -1)
        fi
        
        if [ -n "$LOG_FILE" ]; then
            # Attach the database
            log "Attaching database $DB_NAME with data file $MDF_FILE and log file $LOG_FILE..."
            /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "CREATE DATABASE [$DB_NAME] ON (FILENAME='$MDF_FILE'), (FILENAME='$LOG_FILE') FOR ATTACH" -C
            
            if [ $? -eq 0 ]; then
                log "Successfully attached $DB_NAME database!"
            else
                log "Error attaching $DB_NAME database."
            fi
        else
            log "Could not find log file for $DB_NAME. Cannot attach without log file."
        fi
    done
else
    log "No existing database files found."
fi

log "Database attachment process completed!"

# Keep container running by waiting for SQL Server process
log "SQL Server is running. Use Ctrl+C to stop."
wait $SQLSERVER_PID