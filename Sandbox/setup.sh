#!/bin/bash

# SQLFlow Setup and Database Restoration Utility for Mac
# =====================================================
#
# This comprehensive utility script streamlines SQLFlow deployment by:
# - Downloading SQLFlow backups from GitHub repositories
# - Setting necessary environment variables
# - Cleaning up existing Docker containers, networks, and volumes
# - Configuring Docker Compose paths
# - Pulling required Docker images
# - Starting SQL Server container
# - Restoring databases from backups
# - Updating connection strings in configuration
# - Starting all remaining containers
# - Providing complete system access information
#

# -----------------------------[ Global Variables ]----------------------------- #

# Default repository for backups (TahirRiaz/SQLFlow)
REPO_OWNER="TahirRiaz"
REPO_NAME="SQLFlow"

# Script-level paths
# By default, we assume the script is in the same folder as docker-compose.yml
SCRIPT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_COMPOSE_PATH="$SCRIPT_ROOT/docker-compose.yml"

# Will store the final chosen path to download backups
DOWNLOAD_PATH="" # Will be set based on user input

# We store the backup assets discovered from GitHub so that subsequent steps can reference them
BACKUP_ASSETS=()
BACKUP_ASSET_NAMES=()
BACKUP_ASSET_URLS=()
BACKUP_ASSET_SIZES=()
BACKUP_IS_ZIP=()

# By default, we do not skip DB restore
SKIP_DB_RESTORE=false

# Docker Compose command detection
if command -v docker-compose &> /dev/null; then
    COMPOSE_COMMAND="docker-compose"
else
    COMPOSE_COMMAND="docker compose"
fi

# For Docker-based SQL Server default paths
DEFAULT_DATA_PATH="/var/opt/mssql/data"
DEFAULT_LOG_PATH="/var/opt/mssql/log"

# Connection info for local container
LOCAL_SQL_SERVER_INSTANCE="host.docker.internal,1477"
LOCAL_SQL_USERNAME="SQLFlow"
LOCAL_SQL_PASSWORD="Passw0rd123456"

# ---------------------------------[ Functions ]--------------------------------- #

# Color definitions
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
CYAN='\033[0;36m'
RED='\033[0;31m'
WHITE='\033[1;37m'
GRAY='\033[0;37m'
NC='\033[0m' # No Color

# Function to run a step with proper formatting
run_step() {
    local step_name="$1"
    local function_name="$2"

    echo -e "\n${CYAN}=== $step_name ===${NC}\n"
    
    $function_name
    
    if [ $? -ne 0 ]; then
        echo -e "${RED}Error in '$step_name'${NC}"
        read -p "Do you want to continue to the next step? (Y/N) " choice
        if [[ ! $choice =~ ^[Yy]$ ]]; then
            echo -e "${YELLOW}Exiting script at user request.${NC}"
            exit 1
        fi
    fi
}

# Function to get GitHub releases
get_github_releases() {
    local owner="$1"
    local repo="$2"
    local api_url="https://api.github.com/repos/$owner/$repo/releases"
    
    echo -e "${CYAN}Requesting releases from: $api_url${NC}"
    
    # Try with user agent
    releases=$(curl -s -H "Accept: application/vnd.github.v3+json" -H "User-Agent: Bash GitHub Release Downloader" "$api_url")
    
    # Check if we got a valid response
    if [[ "$releases" == "null" || -z "$releases" ]]; then
        echo -e "${RED}Failed to fetch releases or received null response.${NC}"
        
        # Fallback: Try without headers (simpler request)
        echo -e "${YELLOW}Retrying with simpler request...${NC}"
        releases=$(curl -s "$api_url")
        
        if [[ "$releases" == "null" || -z "$releases" ]]; then
            echo -e "${RED}Retry also failed.${NC}"
            return 1
        fi
    fi
    
    # Count releases (using jq if available, otherwise grep)
    if command -v jq &> /dev/null; then
        release_count=$(echo "$releases" | jq 'length')
    else
        release_count=$(echo "$releases" | grep -o '"tag_name"' | wc -l)
    fi
    
    echo -e "${GREEN}Successfully retrieved $release_count releases.${NC}"
    echo "$releases"
    return 0
}

# Function to show filtered release assets
show_filtered_release_assets() {
    local releases="$1"
    local search_pattern="${2:-*}"
    
    # Check if releases is empty
    if [[ -z "$releases" || "$releases" == "[]" ]]; then
        echo -e "${YELLOW}No releases found for this repository.${NC}"
        return 1
    fi
    
    echo -e "${CYAN}Available releases with backup files (matching: $search_pattern):${NC}"
    
    # Clear previous asset arrays
    BACKUP_ASSETS=()
    BACKUP_ASSET_NAMES=()
    BACKUP_ASSET_URLS=()
    BACKUP_ASSET_SIZES=()
    BACKUP_IS_ZIP=()
    
    global_asset_index=0
    
    # Parse releases using jq if available
    if command -v jq &> /dev/null; then
        # If jq is found, use JSON-based parsing
        release_count=$(echo "$releases" | jq 'length')
        
        for ((i=0; i<release_count; i++)); do
            release_name=$(echo "$releases" | jq -r ".[$i].name")
            published_at=$(echo "$releases" | jq -r ".[$i].published_at")
            assets=$(echo "$releases" | jq -r ".[$i].assets")
            
            asset_count=$(echo "$assets" | jq 'length')
            if [[ "$asset_count" -eq 0 ]]; then
                continue
            fi
            
            release_has_filtered_assets=false
            
            for ((j=0; j<asset_count; j++)); do
                asset_name=$(echo "$assets" | jq -r ".[$j].name")
                
                # We only automatically include .zip for backups
                if [[ "$asset_name" == *.zip ]]; then
                    if [[ "$release_has_filtered_assets" == false ]]; then
                        echo -e "${GREEN}[$i] $release_name (Published: $published_at)${NC}"
                        echo -e "${CYAN}  Assets:${NC}"
                        release_has_filtered_assets=true
                    fi
                    
                    asset_url=$(echo "$assets" | jq -r ".[$j].browser_download_url")
                    asset_size=$(echo "$assets" | jq -r ".[$j].size")
                    size_in_mb=$(echo "scale=2; $asset_size / 1048576" | bc)
                    
                    if [[ "$asset_name" == *.zip ]]; then
                        echo -e "    ${WHITE}[$global_asset_index] $asset_name ($size_in_mb MB) [ZIP]${NC}"
                        BACKUP_IS_ZIP+=("true")
                    else
                        echo -e "    ${WHITE}[$global_asset_index] $asset_name ($size_in_mb MB)${NC}"
                        BACKUP_IS_ZIP+=("false")
                    fi
                    
                    BACKUP_ASSETS+=("$global_asset_index")
                    BACKUP_ASSET_NAMES+=("$asset_name")
                    BACKUP_ASSET_URLS+=("$asset_url")
                    BACKUP_ASSET_SIZES+=("$asset_size")
                    
                    global_asset_index=$((global_asset_index + 1))
                fi
            done
            
            if [[ "$release_has_filtered_assets" == true ]]; then
                echo ""
            fi
        done
        
    else
        # Fallback to grep & sed if jq is not available
        echo -e "${YELLOW}jq not found, using basic text processing. Results may be limited.${NC}"
        
        # 1) Find .zip URLs via grep
        zip_urls=$(echo "$releases" | grep -o 'https://[^"]*\.zip')
        if [[ -z "$zip_urls" ]]; then
            return 1
        fi
        
        # 2) Convert these lines into an array without using 'mapfile'
        zip_urls_array=()
        while IFS= read -r line; do
            zip_urls_array+=("$line")
        done <<< "$zip_urls"
        
        # 3) Also build a corresponding array of filenames
        zip_names=()
        for url in "${zip_urls_array[@]}"; do
            zip_names+=( "$(basename "$url")" )
        done
        
        # 4) Display them
        for ((i=0; i<${#zip_urls_array[@]}; i++)); do
            echo -e "    ${WHITE}[$i] ${zip_names[$i]} (size unknown) [ZIP]${NC}"
            
            BACKUP_ASSETS+=("$i")
            BACKUP_ASSET_NAMES+=("${zip_names[$i]}")
            BACKUP_ASSET_URLS+=("${zip_urls_array[$i]}")
            BACKUP_ASSET_SIZES+=("unknown")
            BACKUP_IS_ZIP+=("true")
            
            global_asset_index=$((global_asset_index + 1))
        done
    fi
    
    # If no .zip assets were found, exit with error
    if [[ ${#BACKUP_ASSETS[@]} -eq 0 ]]; then
        echo -e "${YELLOW}No matching backup (.zip) files found in any release.${NC}"
        return 1
    fi
    
    return 0
}

# Function to download an asset
download_asset() {
    local asset_index="$1"
    local destination_path="$2"
    
    if [[ ! -d "$destination_path" ]]; then
        mkdir -p "$destination_path"
    fi
    
    local download_url="${BACKUP_ASSET_URLS[$asset_index]}"
    local file_name="${BACKUP_ASSET_NAMES[$asset_index]}"
    local file_path="$destination_path/$file_name"
    
    echo -e "${CYAN}Downloading '$file_name' to '$destination_path'...${NC}"
    
    # Download with progress
    curl -L --progress-bar "$download_url" -o "$file_path"
    
    if [[ $? -ne 0 ]]; then
        echo -e "${RED}Failed to download the file.${NC}"
        return 1
    fi
    
    echo -e "${GREEN}Download completed: $file_path${NC}"
    
    # Check if it's a ZIP file
    if [[ "${BACKUP_IS_ZIP[$asset_index]}" == "true" ]]; then
        echo -e "${CYAN}Extracting ZIP file to root path: $destination_path${NC}"
        
        # Extract to destination path directly (not to subfolder)
        unzip -o "$file_path" -d "$destination_path"
        
        if [[ $? -ne 0 ]]; then
            echo -e "${RED}Failed to extract the ZIP file.${NC}"
            return 1
        fi
        
        # Count extracted files
        extracted_count=$(find "$destination_path" -type f -not -path "$file_path" | wc -l)
        echo -e "${GREEN}Extracted $extracted_count files.${NC}"
        
        # Look for .bak files
        backup_files=$(find "$destination_path" -name "*.bak" -o -name "*.BAK" -type f)
        backup_count=$(echo "$backup_files" | grep -v "^$" | wc -l)
        
        if [[ $backup_count -gt 0 ]]; then
            echo -e "${GREEN}Found $backup_count .bak file(s):${NC}"
            echo "$backup_files" | head -5 | while read backup_file; do
                echo -e "  - $backup_file"
            done
            
            if [[ $backup_count -gt 5 ]]; then
                echo -e "  - ... and $((backup_count - 5)) more"
            fi
            
            # Return the first backup file path
            echo "$backup_files" | head -1
            return 0
        else
            echo -e "${YELLOW}No .bak files found in the extracted ZIP. Looking for any database files...${NC}"
            
            # Look for database files
            db_files=$(find "$destination_path" -name "*database*" -o -name "*db*" -o -name "*.mdf" -type f)
            db_count=$(echo "$db_files" | grep -v "^$" | wc -l)
            
            if [[ $db_count -gt 0 ]]; then
                echo -e "${GREEN}Found $db_count potential database files:${NC}"
                echo "$db_files" | head -5 | while read db_file; do
                    echo -e "  - $db_file"
                done
                
                # Return the first database file path
                echo "$db_files" | head -1
                return 0
            fi
            
            echo -e "${YELLOW}No database files found. Returning empty value.${NC}"
            echo ""
            return 0
        fi
    fi
    
    echo "$file_path"
    return 0
}

# Function to test SQL connection
test_sql_connection() {
    local server_instance="$1"
    local username="$2"
    local password="$3"
    
    # Extract host and port from server_instance
    local host=$(echo "$server_instance" | cut -d',' -f1)
    local port=$(echo "$server_instance" | cut -d',' -f2)
    
    # Try to connect using the sqlcmd utility in the Docker container
    echo -e "${YELLOW}Testing connection to SQL Server at $host:$port...${NC}"
    
    # First check if the container exists
    local container_exists=$(docker ps -a --filter "name=sqlflow-mssql" --format "{{.ID}}")
    
    if [[ -n "$container_exists" ]]; then
        # Use the sqlcmd utility in the container
        docker exec sqlflow-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U "$username" -P "$password" -C -Q "SELECT @@VERSION" > /dev/null 2>&1
        
        if [[ $? -eq 0 ]]; then
            echo -e "${GREEN}Successfully connected to SQL Server at $server_instance${NC}"
            return 0
        else
            echo -e "${RED}Failed to connect to SQL Server at $server_instance${NC}"
            return 1
        fi
    else
        echo -e "${YELLOW}SQL Server container not found, connection test skipped.${NC}"
        return 1
    fi
}

# Function to restore SQL database
restore_sql_database() {
    local backup_file="$1"
    local database_name="$2"
    local server_instance="$3"
    local username="$4"
    local password="$5"
    local data_path="${6:-/var/opt/mssql/data}"
    local log_path="${7:-/var/opt/mssql/log}"
    
    echo -e "${CYAN}Preparing to restore '$database_name' from '$backup_file'...${NC}"
    
    if [[ ! -f "$backup_file" ]]; then
        echo -e "${RED}Backup file not found: $backup_file${NC}"
        return 1
    fi
    
    local backup_filename
    backup_filename=$(basename "$backup_file")
    
    local container_name="sqlflow-mssql"
    local container_backup_path="/var/opt/mssql/bak/$backup_filename"
    
    echo -e "${CYAN}Creating backup directory in container...${NC}"
    docker exec $container_name mkdir -p /var/opt/mssql/bak > /dev/null 2>&1
    
    echo -e "${CYAN}Copying backup file to container: $backup_filename...${NC}"
    docker cp "$backup_file" "${container_name}:/var/opt/mssql/bak/" > /dev/null 2>&1
    
    local verify_file
    verify_file=$(docker exec $container_name ls -la "$container_backup_path" 2>&1)
    if [[ $? -ne 0 || -z "$verify_file" ]]; then
        echo -e "${RED}Failed to verify backup file in container${NC}"
        return 1
    fi
    
    echo -e "${GREEN}Backup file verified in container${NC}"
    docker exec $container_name chown -R mssql:mssql /var/opt/mssql/bak > /dev/null 2>&1
    docker exec $container_name chmod -R 755 /var/opt/mssql/bak > /dev/null 2>&1
    
    local tsql_script="
DECLARE @BackupFile NVARCHAR(255) = N'$container_backup_path';
DECLARE @DatabaseName NVARCHAR(128) = N'$database_name';
DECLARE @DefaultDataPath NVARCHAR(512) = N'$data_path';
DECLARE @DefaultLogPath NVARCHAR(512) = N'$log_path';

IF RIGHT(@DefaultDataPath, 1) = '/' OR RIGHT(@DefaultDataPath, 1) = '\\'
    SET @DefaultDataPath = LEFT(@DefaultDataPath, LEN(@DefaultDataPath) - 1);
IF RIGHT(@DefaultLogPath, 1) = '/' OR RIGHT(@DefaultLogPath, 1) = '\\'
    SET @DefaultLogPath = LEFT(@DefaultLogPath, LEN(@DefaultLogPath) - 1);

CREATE TABLE #FileList (
    LogicalName NVARCHAR(128),
    PhysicalName NVARCHAR(512),
    [Type] CHAR(1),
    FileGroupName NVARCHAR(128),
    Size BIGINT,
    MaxSize BIGINT,
    FileID INT,
    CreateLSN NUMERIC(25, 0) NULL,
    DropLSN NUMERIC(25, 0) NULL,
    UniqueID UNIQUEIDENTIFIER NULL,
    ReadOnlyLSN NUMERIC(25, 0) NULL,
    ReadWriteLSN NUMERIC(25, 0) NULL,
    BackupSizeInBytes BIGINT NULL,
    SourceBlockSize INT NULL,
    FileGroupID INT NULL,
    LogGroupGUID UNIQUEIDENTIFIER NULL,
    DifferentialBaseLSN NUMERIC(25, 0) NULL,
    DifferentialBaseGUID UNIQUEIDENTIFIER NULL,
    IsReadOnly BIT NULL,
    IsPresent BIT NULL,
    TDEThumbprint VARBINARY(32) NULL,
    SnapshotURL NVARCHAR(360) NULL
);

INSERT INTO #FileList
EXEC('RESTORE FILELISTONLY FROM DISK = ''' + @BackupFile + '''');

DECLARE @RestoreSQL NVARCHAR(MAX) = 'RESTORE DATABASE [' + @DatabaseName + '] FROM DISK = ''' + @BackupFile + ''' WITH ';
DECLARE @MoveStatements NVARCHAR(MAX) = '';

SELECT @MoveStatements = @MoveStatements +
    'MOVE ''' + LogicalName + ''' TO ''' +
    CASE 
        WHEN [Type] = 'D' THEN @DefaultDataPath + '/' + @DatabaseName +
            CASE 
                WHEN FileID = 1 THEN '_Primary.mdf'
                ELSE '_' + LogicalName + '.ndf'
            END
        WHEN [Type] = 'L' THEN @DefaultLogPath + '/' + @DatabaseName + '_' + LogicalName + '.ldf'
        ELSE @DefaultDataPath + '/' + @DatabaseName + '_' + LogicalName
    END + ''', '
FROM #FileList;

SET @RestoreSQL = @RestoreSQL + @MoveStatements + 'REPLACE, STATS = 10;';

PRINT 'Executing restore with following command:';
PRINT @RestoreSQL;

EXEC sp_executesql @RestoreSQL;

DROP TABLE #FileList;
"

    local temp_script_file="/tmp/robust_restore_$(date +%s).sql"
    echo "$tsql_script" > "$temp_script_file"
    
    docker cp "$temp_script_file" "${container_name}:/tmp/robust_restore.sql" > /dev/null 2>&1
    
    echo -e "${YELLOW}Executing robust T-SQL restore script...${NC}"
    local restore_cmd="docker exec $container_name /opt/mssql-tools18/bin/sqlcmd -S localhost -U \"$username\" -P \"$password\" -C -i /tmp/robust_restore.sql"
    local restore_output
    restore_output=$(eval $restore_cmd 2>&1)
    
    echo "$restore_output" | while read -r line; do
        if [[ "$line" =~ Error|failed|"No such file"|"Msg "[0-9]+ ]]; then
            echo -e "${RED}$line${NC}"
        elif [[ "$line" =~ "RESTORE DATABASE successfully" ]]; then
            echo -e "${GREEN}$line${NC}"
        else
            echo -e "${GRAY}$line${NC}"
        fi
    done
    
    if echo "$restore_output" | grep -q "Msg [0-9]*, Level [0-9]*, State [0-9]*"; then
        echo -e "${RED}Errors detected during restore. Database may not have been restored properly.${NC}"
        
        local verify_sql="SELECT name FROM sys.databases WHERE name = '$database_name'"
        local verify_cmd="docker exec $container_name /opt/mssql-tools18/bin/sqlcmd -S localhost -U \"$username\" -P \"$password\" -C -Q \"$verify_sql\""
        local verify_result
        verify_result=$(eval $verify_cmd 2>&1)
        
        if echo "$verify_result" | grep -q "$database_name"; then
            echo -e "${YELLOW}Despite errors, database '$database_name' appears to exist!${NC}"
            rm -f "$temp_script_file"
            return 0
        fi
        
        rm -f "$temp_script_file"
        return 1
    else
        echo -e "${YELLOW}Waiting for SQL Server to register the restored database...${NC}"
        sleep 5
        
        local verify_sql="SELECT name FROM sys.databases WHERE name = '$database_name'"
        local verify_cmd="docker exec $container_name /opt/mssql-tools18/bin/sqlcmd -S localhost -U \"$username\" -P \"$password\" -C -Q \"$verify_sql\""
        local verify_result
        verify_result=$(eval $verify_cmd 2>&1)
        
        if echo "$verify_result" | grep -q "$database_name"; then
            echo -e "${GREEN}Database '$database_name' restored successfully!${NC}"
            rm -f "$temp_script_file"
            return 0
        else
            echo -e "${YELLOW}Database not found on first check, waiting longer...${NC}"
            sleep 15
            verify_result=$(eval $verify_cmd 2>&1)
            
            if echo "$verify_result" | grep -q "$database_name"; then
                echo -e "${GREEN}Database '$database_name' restored successfully!${NC}"
                rm -f "$temp_script_file"
                return 0
            else
                echo -e "${RED}Database '$database_name' not found after restore attempt${NC}"
                rm -f "$temp_script_file"
                return 1
            fi
        fi
    fi
}

# Function to ensure paths end with a trailing slash
ensure_trailing_slash() {
    local path="$1"
    if [[ "${path}" != */ ]]; then
        path="${path}/"
    fi
    echo "$path"
}

# New helper function to remove repeated slashes in a path/string except in URLs
remove_redundant_slashes() {
    # We'll transform only the slash sequences NOT preceded by a colon
    # so "https://example" won't be touched, but "/Users//Me" becomes "/Users/Me".
    sed -E ':start; s|([^:])/+|\1/|g; t start'
}

# Function to convert path to Docker format (on Mac we often keep it the same)
convert_to_docker_path() {
    local path="$1"
    
    # Ensure trailing slash, then remove accidental double slashes.
    path=$(ensure_trailing_slash "$path")
    path="$(echo "$path" | remove_redundant_slashes)"
    
    echo "$path"
}

# -----------------------------[ Wizard Steps ]----------------------------- #

# Step 1: Download Release
step1_download_release() {
    echo -e "${CYAN}Fetching releases from '$REPO_OWNER/$REPO_NAME'...${NC}"
    local releases
    releases=$(get_github_releases "$REPO_OWNER" "$REPO_NAME")
    
    if [[ $? -ne 0 || -z "$releases" ]]; then
        echo -e "${RED}No releases found or unable to retrieve from $REPO_OWNER/$REPO_NAME.${NC}"
        return 1
    fi
    
    show_filtered_release_assets "$releases"
    if [[ $? -ne 0 || ${#BACKUP_ASSETS[@]} -eq 0 ]]; then
        echo -e "${RED}No backup files found in any release. Cannot continue.${NC}"
        return 1
    fi
    
    echo -e "${CYAN}Please select a single file to download:${NC}"
    read -p "Enter the index of the file to download (0..$(( ${#BACKUP_ASSETS[@]} - 1 ))) " selection
    
    local asset_index=0
    if ! [[ "$selection" =~ ^[0-9]+$ ]] || [[ "$selection" -lt 0 || "$selection" -ge ${#BACKUP_ASSETS[@]} ]]; then
        echo -e "${YELLOW}Invalid selection. Using the first backup file instead.${NC}"
        asset_index=0
    else
        asset_index=$selection
    fi
    
    echo -e "${GREEN}Selected file: ${BACKUP_ASSET_NAMES[$asset_index]}${NC}"
    
    local default_download_path="$HOME/Documents/SQLFlow01"
    read -p "Enter the local download location for this backup file (Press Enter for '$default_download_path') " answer
    
    if [[ -z "$answer" ]]; then
        DOWNLOAD_PATH="$default_download_path"
    else
        # If user didn't provide an absolute path, prepend current dir
        if [[ "$answer" = /* ]]; then
            DOWNLOAD_PATH="$answer"
        else
            DOWNLOAD_PATH="$(pwd)/$answer"
        fi
    fi
    
    DOCKER_COMPOSE_PATH="$DOWNLOAD_PATH/docker-compose.yml"
    
    echo -e "${GREEN}Will use download path: $DOWNLOAD_PATH${NC}"
    
    if [[ ! -d "$DOWNLOAD_PATH" ]]; then
        mkdir -p "$DOWNLOAD_PATH"
        echo -e "${GREEN}Created directory: $DOWNLOAD_PATH${NC}"
    fi
    
    echo -e "${CYAN}Downloading ${BACKUP_ASSET_NAMES[$asset_index]}...${NC}"
    local downloaded_path
    downloaded_path=$(download_asset "$asset_index" "$DOWNLOAD_PATH")
    
    if [[ -n "$downloaded_path" ]]; then
        echo -e "${GREEN}Successfully downloaded and extracted to: $DOWNLOAD_PATH${NC}"
    else
        echo -e "${YELLOW}Download failed for ${BACKUP_ASSET_NAMES[$asset_index]}. Will retry in Step 7 if needed.${NC}"
    fi
    
    local bakfiles
    bakfiles=$(find "$DOWNLOAD_PATH" -name "*.bak" -o -name "*.BAK" -type f)
    local bakcount
    bakcount=$(echo "$bakfiles" | grep -v "^$" | wc -l)
    
    if [[ $bakcount -gt 0 ]]; then
        echo -e "${GREEN}Found $bakcount .bak files ready for restoration:${NC}"
        echo "$bakfiles" | head -5 | while read -r bakfile; do
            echo -e "  - $(basename "$bakfile")"
        done
        
        if [[ $bakcount -gt 5 ]]; then
            echo -e "  - ... and $((bakcount - 5)) more"
        fi
    else
        echo -e "${YELLOW}No .bak files were found after download/extraction. Check the downloaded files manually.${NC}"
    fi
    
    return 0
}

# Step 2: Set Environment Variables
step2_set_environment_variables() {
    echo -e "${CYAN}Setting up environment variables for SQLFlow...${NC}"

    local sql_server_instance="host.docker.internal,1477"
    local sql_database="dw-sqlflow-prod"

    # Construct a default connection string
    export SQLFlowConStr="Server=$sql_server_instance;Database=$sql_database;User ID=$LOCAL_SQL_USERNAME;Password=$LOCAL_SQL_PASSWORD;TrustServerCertificate=True;"
    export SQLFlowOpenAiApiKey="your-openai-api-key"

    # Determine which shell profile to update
    local profile_file
    if [[ "$SHELL" == *"zsh"* ]]; then
        profile_file="$HOME/.zshrc"
    else
        profile_file="$HOME/.bash_profile"
        if [[ ! -f "$profile_file" ]]; then
            profile_file="$HOME/.bashrc"
        fi
    fi

    # Update or add environment variables
    if grep -q "SQLFlowConStr" "$profile_file" 2>/dev/null; then
        sed -i '' "s|export SQLFlowConStr=.*|export SQLFlowConStr=\"$SQLFlowConStr\"|g" "$profile_file"
        sed -i '' "s|export SQLFlowOpenAiApiKey=.*|export SQLFlowOpenAiApiKey=\"$SQLFlowOpenAiApiKey\"|g" "$profile_file"
    else
        echo "# SQLFlow Environment Variables" >> "$profile_file"
        echo "export SQLFlowConStr=\"$SQLFlowConStr\"" >> "$profile_file"
        echo "export SQLFlowOpenAiApiKey=\"$SQLFlowOpenAiApiKey\"" >> "$profile_file"
    fi

    echo -e "${GREEN}Environment variables set:${NC}"
    echo -e "${GREEN} - SQLFlowConStr: $SQLFlowConStr${NC}"
    
    echo -e "${YELLOW}Note: Run 'source $profile_file' to apply the environment variables in the current shell session.${NC}"
    
    return 0
}

# Step 3: Clean Up Existing Containers
step3_cleanup_existing_containers() {
    echo -e "${CYAN}Performing comprehensive cleanup of existing Docker resources...${NC}"
    
    if [[ ! -f "$DOCKER_COMPOSE_PATH" ]]; then
        echo -e "${YELLOW}docker-compose.yml not found at path: $DOCKER_COMPOSE_PATH${NC}"
        echo -e "${YELLOW}Will proceed with direct Docker commands for cleanup.${NC}"
    fi

    pushd "$SCRIPT_ROOT" > /dev/null
    
    # 1. Take down Docker Compose environment if possible
    if [[ -f "$DOCKER_COMPOSE_PATH" ]]; then
        echo -e "${YELLOW}Taking down Docker Compose environment (with volumes)...${NC}"
        $COMPOSE_COMMAND -f "$DOCKER_COMPOSE_PATH" down -v --remove-orphans 2>&1 | while read -r line; do
            if [[ -n "$line" ]]; then
                echo -e "${GRAY}$line${NC}"
            fi
        done
    fi
    
    # 2. Find and stop any remaining SQLFlow containers
    echo -e "${YELLOW}Finding all SQLFlow-related containers...${NC}"
    local container_filters=(
        "name=sqlflow-ui"
        "name=sqlflow-api"
        "name=sqlflow-sql"
        "name=sqlflow-mssql"
        "ancestor=businessiq"
    )
    
    local all_container_ids=()
    for filter in "${container_filters[@]}"; do
        local container_ids
        container_ids=$(docker ps -a --filter "$filter" --format "{{.ID}}" 2>/dev/null)
        if [[ -n "$container_ids" ]]; then
            all_container_ids+=($(echo "$container_ids"))
        fi
    done
    
    local unique_container_ids
    IFS=$'\n' unique_container_ids=($(printf "%s\n" "${all_container_ids[@]}" | sort -u))
    IFS=$' '
    
    if [[ ${#unique_container_ids[@]} -gt 0 ]]; then
        echo -e "${YELLOW}Found ${#unique_container_ids[@]} SQLFlow-related containers to remove.${NC}"
        for id in "${unique_container_ids[@]}"; do
            echo -e "${YELLOW}Stopping container $id...${NC}"
            docker stop "$id" 2>/dev/null
            echo -e "${YELLOW}Removing container $id...${NC}"
            docker rm "$id" 2>/dev/null
        done
    else
        echo -e "${GREEN}No existing SQLFlow containers found.${NC}"
    fi
    
    # 3. Remove SQLFlow networks
    echo -e "${YELLOW}Removing SQLFlow networks...${NC}"
    local networks
    networks=$(docker network ls --filter "name=sqlflow" --format "{{.ID}}" 2>/dev/null)
    if [[ -n "$networks" ]]; then
        for network_id in $networks; do
            echo -e "${YELLOW}Removing network $network_id...${NC}"
            docker network rm "$network_id" 2>/dev/null
        done
    fi
    
    # 4. Remove SQLFlow volumes
    echo -e "${YELLOW}Removing SQLFlow volumes...${NC}"
    local volume_filters=("name=sqlflow")
    local all_volumes=()
    
    for filter in "${volume_filters[@]}"; do
        local volumes
        volumes=$(docker volume ls --filter "$filter" --format "{{.Name}}" 2>/dev/null)
        if [[ -n "$volumes" ]]; then
            all_volumes+=($(echo "$volumes"))
        fi
    done
    
    local unique_volumes
    IFS=$'\n' unique_volumes=($(printf "%s\n" "${all_volumes[@]}" | sort -u))
    IFS=$' '
    
    if [[ ${#unique_volumes[@]} -gt 0 ]]; then
        echo -e "${YELLOW}Found ${#unique_volumes[@]} SQLFlow-related volumes to remove:${NC}"
        for volume in "${unique_volumes[@]}"; do
            echo -e "  - ${GRAY}$volume${NC}"
        done
        
        docker volume rm "${unique_volumes[@]}" 2>&1 > /dev/null
        
        local remaining_volumes
        IFS=$'\n'
        remaining_volumes=($(docker volume ls --format "{{.Name}}" | grep -E "$(echo "${unique_volumes[@]}" | tr ' ' '|')" 2>/dev/null))
        IFS=$' '
        
        if [[ ${#remaining_volumes[@]} -gt 0 ]]; then
            echo -e "${YELLOW}${#remaining_volumes[@]} volumes could not be removed. Trying individual removal...${NC}"
            for volume in "${remaining_volumes[@]}"; do
                echo -e "${YELLOW}Force removing volume $volume...${NC}"
                local using_containers
                using_containers=$(docker ps -a --filter "volume=$volume" --format "{{.ID}}" 2>/dev/null)
                if [[ -n "$using_containers" ]]; then
                    for container_id in $using_containers; do
                        echo -e "  - ${GRAY}Stopping container $container_id using $volume...${NC}"
                        docker stop "$container_id" 2>/dev/null
                        docker rm -f "$container_id" 2>/dev/null
                    done
                fi
                docker volume rm "$volume" 2>/dev/null
            done
        fi
    else
        echo -e "${GREEN}No SQLFlow-related volumes found.${NC}"
    fi
    
    popd > /dev/null
    echo -e "${GREEN}Cleanup complete.${NC}"
    
    local remaining_containers
    remaining_containers=$(docker ps -a --filter "name=sqlflow" --format "{{.Names}}" 2>/dev/null)
    local remaining_volumes
    remaining_volumes=$(docker volume ls --filter "name=sqlflow" --format "{{.Name}}" 2>/dev/null)
    
    if [[ -n "$remaining_containers" || -n "$remaining_volumes" ]]; then
        echo -e "${RED}WARNING: Some SQLFlow resources could not be removed:${NC}"
        if [[ -n "$remaining_containers" ]]; then
            echo -e "${RED}  Containers: $remaining_containers${NC}"
        fi
        if [[ -n "$remaining_volumes" ]]; then
            echo -e "${RED}  Volumes: $remaining_volumes${NC}"
        fi
        echo -e "${YELLOW}You may need to remove these resources manually.${NC}"
    else
        echo -e "${GREEN}All SQLFlow Docker resources successfully removed!${NC}"
    fi
    
    return 0
}

# Step 4: Update Docker Compose Paths
step4_update_docker_compose_paths() {
    echo -e "${CYAN}Updating volume paths in docker-compose.yml...${NC}"
    
    local docker_compose_path="$DOWNLOAD_PATH/docker-compose.yml"
    DOCKER_COMPOSE_PATH="$docker_compose_path"

    if [[ ! -f "$docker_compose_path" ]]; then
        echo -e "${RED}docker-compose.yml not found at: $docker_compose_path${NC}"
        return 1
    fi
    
    cp "$docker_compose_path" "$docker_compose_path.backup"
    
    # Convert any backslashes to forward slashes
    local normalized_path="${DOWNLOAD_PATH//\\/\/}"
    echo -e "${CYAN}Using normalized path: $normalized_path${NC}"
    
    # Convert to Docker container style path and remove double slashes
    local containerized_path
    containerized_path=$(convert_to_docker_path "$DOWNLOAD_PATH")
    echo -e "${CYAN}Using containerized path: $containerized_path${NC}"
    
    local content
    content=$(cat "$docker_compose_path")
    
    # Replace references to Windows-style 'C:/SQLFlow' with the normalized path
    content="${content//C:\/SQLFlow/$normalized_path}"
    # Replace references to '/c/SQLFlow' (a common Docker on Windows notation)
    content="${content//\/c\/SQLFlow/$containerized_path}"
    
    # Final pass to remove leftover // that aren't in a URL
    content="$(echo "$content" | remove_redundant_slashes)"
    
    echo "$content" > "$docker_compose_path"
    
    echo -e "${GREEN}docker-compose.yml updated successfully.${NC}"
    return 0
}

# Step 5: Pull Docker Images
step5_pull_docker_images() {
    echo -e "${CYAN}Pulling Docker images using '$COMPOSE_COMMAND pull'...${NC}"
    
    pushd "$DOWNLOAD_PATH" > /dev/null
    
    $COMPOSE_COMMAND -f "$DOCKER_COMPOSE_PATH" pull 2>&1 | while read -r line; do
        if [[ "$line" =~ Pulling|Pulled ]]; then
            echo -e "${GRAY}$line${NC}"
        fi
    done
    
    echo -e "${GREEN}All Docker images pulled successfully.${NC}"
    
    popd > /dev/null
    return 0
}

# Step 6: Start SQL Server Container
step6_start_sql_server_container() {
    echo -e "${CYAN}Starting only the SQL Server container (sqlflow-mssql)...${NC}"
    
    pushd "$DOWNLOAD_PATH" > /dev/null
    
    $COMPOSE_COMMAND -f "$DOCKER_COMPOSE_PATH" up -d sqlflow-mssql 2>&1 | while read -r line; do
        if [[ "$line" =~ Creating|Created|Starting|Started ]]; then
            echo -e "${GRAY}$line${NC}"
        fi
    done

    local sql_container_check
    sql_container_check=$($COMPOSE_COMMAND -f "$DOCKER_COMPOSE_PATH" ps sqlflow-mssql 2>&1)
    if ! echo "$sql_container_check" | grep -qE "running|healthy"; then
        echo -e "${YELLOW}Warning: sqlflow-mssql container not clearly marked as 'running' or 'healthy' yet.${NC}"
    fi
    
    echo -e "${YELLOW}Waiting for SQL Server to finish initializing...${NC}"
    sleep 10
    
    local max_attempts=10
    local attempt=0
    local sql_ready=false
    
    while [[ "$sql_ready" == false && $attempt -lt $max_attempts ]]; do
        attempt=$((attempt + 1))
        echo -e "${YELLOW}Checking readiness (attempt $attempt of $max_attempts)...${NC}"
        local logs
        logs=$($COMPOSE_COMMAND -f "$DOCKER_COMPOSE_PATH" logs sqlflow-mssql 2>&1)
        
        if echo "$logs" | grep -q "SQL Server is now ready for client connections"; then
            echo -e "${YELLOW}SQL Server reports ready, waiting 15 more seconds for full initialization...${NC}"
            sleep 15
            sql_ready=true
            echo -e "${GREEN}SQL Server should now be fully initialized!${NC}"
        else
            sleep 10
        fi
    done

    if [[ "$sql_ready" == false ]]; then
        echo -e "${YELLOW}Warning: SQL Server may not be fully initialized, continuing anyway...${NC}"
    fi
    
    popd > /dev/null
    return 0
}

# Step 7: Restore Databases
step7_restore_databases() {
    echo -e "${CYAN}Starting database restoration from downloaded backup...${NC}"
    
    if [[ "$SKIP_DB_RESTORE" == true ]]; then
        echo -e "${YELLOW}Skipping database restoration (SKIP_DB_RESTORE = $SKIP_DB_RESTORE).${NC}"
        return 0
    fi
    
    local data_path="$DEFAULT_DATA_PATH"
    local log_path="$DEFAULT_LOG_PATH"
    echo -e "${CYAN}Data path in container: $data_path${NC}"
    echo -e "${CYAN}Log path in container: $log_path${NC}"
    
    test_sql_connection "$LOCAL_SQL_SERVER_INSTANCE" "$LOCAL_SQL_USERNAME" "$LOCAL_SQL_PASSWORD"
    if [[ $? -ne 0 ]]; then
        echo -e "${RED}Cannot connect to local SQL Server container with 'SQLFlow' user${NC}"
        return 1
    fi
    
    local bakfiles
    bakfiles=$(find "$DOWNLOAD_PATH" -name "*.bak" -o -name "*.BAK" -type f 2>/dev/null)
    local bakcount
    bakcount=$(echo "$bakfiles" | grep -v "^$" | wc -l)
    
    if [[ $bakcount -eq 0 ]]; then
        echo -e "${YELLOW}No .bak files found in $DOWNLOAD_PATH. Skipping database restoration.${NC}"
        return 0
    fi
    
    local container_name="sqlflow-mssql"
    echo -e "${CYAN}Ensuring backup directory exists in container...${NC}"
    docker exec $container_name mkdir -p /var/opt/mssql/bak 2>/dev/null
    docker exec $container_name chown -R mssql:mssql /var/opt/mssql/bak 2>/dev/null
    
    local backup_file_paths=()
    
    if [[ $bakcount -gt 1 ]]; then
        echo -e "${CYAN}Multiple .bak files found. Select files to restore:${NC}"
        local i=0
        while read -r bakfile; do
            echo -e "${WHITE}[$i] $(basename "$bakfile")${NC}"
            i=$((i + 1))
        done <<< "$bakfiles"
        
        echo -e "${CYAN}Enter the indexes of files to restore (comma-separated, e.g., '0,2,3' or 'all' for all):${NC}"
        read selection
        
        if [[ "$selection" == "all" ]]; then
            while read -r bakfile; do
                backup_file_paths+=("$bakfile")
            done <<< "$bakfiles"
        else
            IFS=',' read -ra selected_indexes <<< "$selection"
            for index_str in "${selected_indexes[@]}"; do
                index_str=$(echo "$index_str" | tr -d ' ')
                if [[ "$index_str" =~ ^[0-9]+$ ]]; then
                    local bak_index=$index_str
                    if [[ $bak_index -ge 0 && $bak_index -lt $bakcount ]]; then
                        local file
                        file=$(echo "$bakfiles" | sed -n "$((bak_index+1))p")
                        backup_file_paths+=("$file")
                    else
                        echo -e "${YELLOW}Invalid selection index: '$index_str'. Skipping.${NC}"
                    fi
                else
                    echo -e "${YELLOW}Invalid selection index: '$index_str'. Skipping.${NC}"
                fi
            done
            
            if [[ ${#backup_file_paths[@]} -eq 0 ]]; then
                echo -e "${YELLOW}No valid files selected. Using the first backup file.${NC}"
                backup_file_paths+=("$(echo "$bakfiles" | head -1)")
            fi
        fi
    else
        backup_file_paths+=("$(echo "$bakfiles")")
    fi
    
    for backup_file_path in "${backup_file_paths[@]}"; do
        local filename_no_ext
        filename_no_ext=$(basename "$backup_file_path" | sed 's/\.[^.]*$//')
        
        local clean_db_name
        clean_db_name=$(echo "$filename_no_ext" | sed 's/_[0-9]\{8\}$//')
        
        local proposed_db_name="$clean_db_name"
        
        echo -e "${CYAN}Restoring database from $backup_file_path as '$proposed_db_name'...${NC}"
        restore_sql_database "$backup_file_path" "$proposed_db_name" \
                             "$LOCAL_SQL_SERVER_INSTANCE" "$LOCAL_SQL_USERNAME" "$LOCAL_SQL_PASSWORD" \
                             "$data_path" "$log_path"
        
        if [[ $? -ne 0 ]]; then
            echo -e "${YELLOW}Database restoration for '$proposed_db_name' failed or partially succeeded. Check logs for details.${NC}"
        else
            echo -e "${GREEN}Database '$proposed_db_name' restored successfully.${NC}"
        fi
    done
    
    return 0
}

# Step 7b: Update Connection Strings
step7b_update_connection_strings() {
    echo -e "\n${CYAN}Running update statements against [flw].[SysDataSource]...${NC}"
    
    local server_instance="$LOCAL_SQL_SERVER_INSTANCE"
    local userid="$LOCAL_SQL_USERNAME"
    local password="$LOCAL_SQL_PASSWORD"
    
    # Construct the SQLFlow DB connection
    local sql_flow_conn_str="Server=$server_instance;Database=dw-sqlflow-prod;User ID=$userid;Password=$password;TrustServerCertificate=True;Command Timeout=360;"
    
    # Setup dictionary of aliases to actual database names
    declare -A databases
    databases["dw-ods-prod-db"]="dw-ods-prod"
    databases["dw-pre-prod-db"]="dw-pre-prod"
    databases["wwi-db"]="WideWorldImporters"
    
    local update_statements=()
    
    for alias in "${!databases[@]}"; do
        local db_name="${databases[$alias]}"
        local conn_string="Server=host.docker.internal,1477;Initial Catalog=$db_name;User ID=$userid;Password=$password;Persist Security Info=False;"
        
        # Add TrustServerCertificate and Encrypt settings
        conn_string+="TrustServerCertificate=True;Encrypt=False;"
        conn_string+="Command Timeout=360;"
        
        local update_statement="UPDATE [flw].[SysDataSource] SET ConnectionString = '$conn_string' WHERE Alias = '$alias';"
        update_statements+=("$update_statement")
        
        local log_conn_string="${conn_string//Password=[^;]*/Password=*****}"
        echo -e "${YELLOW}Preparing update for $alias with connection string: $log_conn_string${NC}"
    done
    
    echo -e "\n${YELLOW}Executing update statements...${NC}"
    for statement in "${update_statements[@]}"; do
        local log_statement="${statement//Password=[^;]*/Password=*****}"
        echo -e "${YELLOW}Executing: $log_statement${NC}"
        
        docker exec sqlflow-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U "$LOCAL_SQL_USERNAME" -P "$LOCAL_SQL_PASSWORD" -d "dw-sqlflow-prod" -C -Q "$statement"
        
        if [[ $? -ne 0 ]]; then
            echo -e "${RED}Error executing update statement${NC}"
        fi
    done
    
    echo -e "${GREEN}Update statements executed successfully against dw-sqlflow-prod database.${NC}"
    return 0
}

# Step 7c: Replace Paths
step7c_replace_paths() {
    echo -e "\n${CYAN}Executing flw.ReplacePaths stored procedure to update path references...${NC}"
    
    local containerized_path
    containerized_path=$(convert_to_docker_path "$DOWNLOAD_PATH")
    echo -e "${YELLOW}Using containerized path for replacement: $containerized_path${NC}"
    
    local sp_command="EXEC [flw].[ReplacePaths] @NewPathBase = '$containerized_path'"
    echo -e "${YELLOW}Executing stored procedure against dw-sqlflow-prod database...${NC}"
    
    docker exec sqlflow-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U "$LOCAL_SQL_USERNAME" -P "$LOCAL_SQL_PASSWORD" -d "dw-sqlflow-prod" -C -Q "$sp_command" -t 300
    
    if [[ $? -eq 0 ]]; then
        echo -e "${GREEN}Stored procedure executed successfully.${NC}"
    else
        echo -e "${RED}Error executing stored procedure${NC}"
        echo -e "\n${YELLOW}Troubleshooting tips:${NC}"
        echo -e "${YELLOW}1. Ensure the dw-sqlflow-prod database was properly restored${NC}"
        echo -e "${YELLOW}2. Verify the [flw].[ReplacePaths] stored procedure exists in the database${NC}"
        echo -e "${YELLOW}3. Check if the path format is compatible with the stored procedure${NC}"
        
        read -p "Would you like to continue with the rest of the setup? (Y/N) " choice
        if [[ ! "$choice" =~ ^[Yy]$ ]]; then
            echo -e "${YELLOW}Exiting script at user request.${NC}"
            exit 1
        fi
    fi
    
    return 0
}

# Step 8: Start Remaining Containers
step8_start_remaining_containers() {
    echo -e "${CYAN}Starting all remaining containers (docker-compose up -d)...${NC}"
    
    pushd "$DOWNLOAD_PATH" > /dev/null
    
    $COMPOSE_COMMAND -f "$DOCKER_COMPOSE_PATH" up -d 2>&1 | while read -r line; do
        if [[ "$line" =~ Creating|Created|Starting|Started ]]; then
            echo -e "${GRAY}$line${NC}"
        fi
    done

    echo -e "${YELLOW}Verifying containers' status...${NC}"
    sleep 5
    
    local check
    check=$($COMPOSE_COMMAND -f "$DOCKER_COMPOSE_PATH" ps 2>&1)
    if echo "$check" | grep -qE "running|healthy"; then
        echo -e "${GREEN}All containers appear to be running!${NC}"
        echo -e "${CYAN}Access SQLFlow at http://localhost:8110 or https://localhost:8111${NC}"
        
        if command -v nc &> /dev/null; then
            if ! nc -z localhost 8110 &>/dev/null; then
                echo -e "${YELLOW}Note: Port 8110 is not yet listening. The containers may still be starting up.${NC}"
            fi
        else
            echo -e "${YELLOW}Note: Cannot check if port 8110 is listening. The containers may still be starting.${NC}"
        fi
    else
        echo -e "${YELLOW}Warning: Not all containers appear to be running. Check 'docker ps' manually.${NC}"
    fi
    
    popd > /dev/null
    return 0
}

# Step 9: Summary
step9_summary() {
    echo -e "\n${GREEN}SQLFlow Setup and Database Restoration Complete!${NC}"
    echo -e "${GREEN}------------------------------------------------${NC}"
    echo -e "${GREEN}Environment variables are set.${NC}"
    echo -e "${GREEN}SQL Server container is running, backups restored (if selected), and the rest of the containers are up.${NC}"
    echo -e "${GREEN}You can now access SQLFlow via http://localhost:8110 or https://localhost:8111${NC}"
    echo -e "${GREEN}Login credentials: demo@sqlflow.io/@Demo123${NC}"

    echo -e "\n${CYAN}Connection String for your environment:${NC}"
    echo -e "${WHITE}  $SQLFlowConStr${NC}"

    echo -e "\n${CYAN}Troubleshooting Tips:${NC}"
    echo -e "${WHITE}  1. Make sure Docker Desktop is running.${NC}"
    echo -e "${WHITE}  2. Check docker-compose syntax:  $COMPOSE_COMMAND config${NC}"
    echo -e "${WHITE}  3. Ensure ports 8110, 8111, etc. are not in use by other apps.${NC}"
    echo -e "${WHITE}  4. Check logs:  $COMPOSE_COMMAND logs${NC}"
    echo -e "${WHITE}  5. Confirm your .bak files were restored: Connect to $LOCAL_SQL_SERVER_INSTANCE with user '$LOCAL_SQL_USERNAME'.${NC}"
   
   return 0
}

# Step 10: Open Browser
step10_open_browser() {
    echo -e "\n${CYAN}Would you like to open the SQLFlow UI in your browser now?${NC}"
    echo -e "${GREEN}Login credentials: demo@sqlflow.io/@Demo123${NC}"
    
    read -p "Open SQLFlow UI in browser? (Y/N) " open_browser
    
    if [[ "$open_browser" =~ ^[Yy]$ ]]; then
        echo -e "${CYAN}Opening SQLFlow UI in your default browser...${NC}"
        
        if command -v open &> /dev/null; then
            open "http://localhost:8110"
            echo -e "${GREEN}Browser launched successfully!${NC}"
        else
            echo -e "${RED}Failed to open browser automatically. Please manually navigate to:${NC}"
            echo -e "${WHITE}http://localhost:8110${NC}"
        fi
        
        echo -e "\n${YELLOW}Remember to log in with:${NC}"
        echo -e "${WHITE}  Username: demo@sqlflow.io${NC}"
        echo -e "${WHITE}  Password: @Demo123${NC}"
        echo -e "\n${YELLOW}Note: It may take a few moments for all services to fully initialize.${NC}"
    else
        echo -e "\n${CYAN}You can access SQLFlow UI later at: http://localhost:8110${NC}"
        echo -e "${GREEN}Remember the login credentials: demo@sqlflow.io/@Demo123${NC}"
    fi
    
    echo -e "\n${CYAN}Quick Start Tips:${NC}"
    echo -e "${WHITE}1. After logging in, you'll see the SQLFlow dashboard.${NC}"
    echo -e "${WHITE}2. Click on 'New Analysis' to start exploring your databases.${NC}"
    echo -e "${WHITE}3. Select a datasource from the dropdown menu.${NC}"
    echo -e "${WHITE}4. You can run SQL queries or use the visual interface to build queries.${NC}"
    echo -e "${WHITE}5. Explore data lineage and impact analysis features in the 'Data Lineage' section.${NC}"
    
    return 0
}

# -----------------------------[ Main Script Flow ]----------------------------- #
echo -e "${GREEN}Starting SQLFlow Setup and Database Restoration Wizard...${NC}"

run_step "Step 1: Download Backup Release Info" step1_download_release
run_step "Step 2: Set Environment Variables" step2_set_environment_variables
run_step "Step 3: Clean Up Existing Containers" step3_cleanup_existing_containers
run_step "Step 4: Update Docker Compose Paths" step4_update_docker_compose_paths
run_step "Step 5: Pull Docker Images" step5_pull_docker_images
run_step "Step 6: Start SQL Server Container" step6_start_sql_server_container
run_step "Step 7: Database Restoration" step7_restore_databases
run_step "Step 7b: Update Connection Strings" step7b_update_connection_strings
run_step "Step 7c: Replace Paths" step7c_replace_paths
run_step "Step 8: Start Remaining Containers" step8_start_remaining_containers
run_step "Step 9: Summary" step9_summary
run_step "Step 10: Open SQLFlow UI" step10_open_browser

echo -e "\n${GREEN}Wizard complete! Review any warnings above for additional actions.${NC}"
