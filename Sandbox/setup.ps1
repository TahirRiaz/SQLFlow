<#
.SYNOPSIS
    SQLFlow Setup and Database Restoration Utility - Automates complete deployment of SQLFlow environment with database restoration.

.DESCRIPTION
    This comprehensive utility script streamlines SQLFlow deployment by:
    - Downloading SQLFlow backups from GitHub repositories
    - Setting necessary environment variables (requires admin rights)
    - Cleaning up existing Docker containers, networks, and volumes
    - Configuring Docker Compose paths
    - Pulling required Docker images
    - Starting SQL Server container
    - Restoring databases from backups
    - Updating connection strings in configuration
    - Starting all remaining containers
    - Providing complete system access information

    Administrator privileges are required to run this script due to environment variable configuration.
#>

# -----------------------------[ Global Variables ]----------------------------- #

# Default repository for backups (TahirRiaz/SQLFlow)
$repoOwner = "TahirRiaz"  
$repoName  = "SQLFlow"

# Script-level paths
# By default, we assume the script is in the same folder as docker-compose.yml
# You can customize these as needed or prompt the user for them.
$global:ScriptRoot          = $PSScriptRoot
$global:dockerComposePath   = Join-Path -Path $ScriptRoot -ChildPath "docker-compose.yml"

# Will store the final chosen path to download backups
$global:DownloadPath        = $null  # Will be set based on user input

# We store the backup assets discovered from GitHub so that subsequent steps can reference them
$global:BackupAssets        = @()

# By default, we do not skip DB restore
$global:skipDbRestore       = $false

# Docker Compose command detection
if (Get-Command "docker-compose" -ErrorAction SilentlyContinue) {
    $global:ComposeCommand = "docker-compose"
}
else {
    $global:ComposeCommand = "docker compose"
}

# For Docker-based SQL Server default paths
$global:defaultDataPath     = "/var/opt/mssql/data"
$global:defaultLogPath      = "/var/opt/mssql/log"

# Connection info for local container
$global:localSqlServerInstance = "localhost,1477"
$global:localSqlUsername       = "SQLFlow"
$global:localSqlPassword       = "Passw0rd123456"

# ---------------------------------[ Functions ]--------------------------------- #

function Run-Step {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$StepName,
        [Parameter(Mandatory)] [ScriptBlock]$Action
    )

    Write-Host "`n=== $StepName ===`n" -ForegroundColor Cyan
    try {
        & $Action
    }
    catch {
        Write-Host "Error in '$StepName': $($_.Exception.Message)" -ForegroundColor Red
        $choice = Read-Host "Do you want to continue to the next step? (Y/N)"
        if ($choice.ToUpper() -ne "Y") {
            Write-Host "Exiting script at user request." -ForegroundColor Yellow
            exit 1
        }
    }
}

function Get-GitHubReleases {
    param (
        [string]$Owner,
        [string]$Repo
    )
    $apiUrl = "https://api.github.com/repos/$Owner/$Repo/releases"
    $headers = @{
        "Accept"    = "application/vnd.github.v3+json"
        "User-Agent"= "PowerShell GitHub Release Downloader"
    }
    try {
        Write-Host "Requesting releases from: $apiUrl" -ForegroundColor Cyan
        $releases = Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Get -UseBasicParsing
        if ($null -eq $releases) {
            Write-Error "Received null response from GitHub API."
            return @()
        }
        # Force array conversion
        $releases = @($releases)
        Write-Host "Successfully retrieved $($releases.Count) releases." -ForegroundColor Green
        return $releases
    }
    catch {
        Write-Error "Failed to fetch releases: $_"
        
        # Fallback: Try without headers (simpler request)
        try {
            Write-Host "Retrying with simpler request..." -ForegroundColor Yellow
            $releases = Invoke-RestMethod -Uri $apiUrl -Method Get -UseBasicParsing
            $releases = @($releases)
            Write-Host "Successfully retrieved $($releases.Count) releases on retry." -ForegroundColor Green
            return $releases
        }
        catch {
            Write-Error "Retry also failed: $_"
            return @()
        }
    }
}

function Show-FilteredReleaseAssets {
    param (
        [Array]$Releases,
        [string]$SearchPattern = "*"
    )
    
    if ($null -eq $Releases -or $Releases.Count -eq 0) {
        Write-Host "No releases found for this repository." -ForegroundColor Yellow
        return @()
    }
    
    Write-Host "Available releases with backup files (matching: $SearchPattern):" -ForegroundColor Cyan
    
    $filteredAssets = @()
    $globalAssetIndex = 0
    
    for ($i = 0; $i -lt $Releases.Count; $i++) {
        $release = $Releases[$i]
        $releaseHasFilteredAssets = $false
        $releaseAssets = @()
        
        if ($release.assets.Count -gt 0) {
            for ($j = 0; $j -lt $release.assets.Count; $j++) {
                $asset = $release.assets[$j]
                $includeAsset = $false
                
                # We only automatically include .zip for backups
                # (You can adjust logic if your backups come in other forms)
                if ($asset.name -like "*.zip") {
                    $includeAsset = $true
                }
                
                if ($includeAsset) {
                    if (-not $releaseHasFilteredAssets) {
                        Write-Host "[$i] $($release.name) (Published: $($release.published_at))" -ForegroundColor Green
                        Write-Host "  Assets:" -ForegroundColor Cyan
                        $releaseHasFilteredAssets = $true
                    }
                    
                    $sizeInMB = [math]::Round($asset.size / 1MB, 2)
                    $displayText = if ($asset.name -like "*.zip") {
                        "    [$globalAssetIndex] $($asset.name) ($sizeInMB MB) [ZIP]"
                    }
                    else {
                        "    [$globalAssetIndex] $($asset.name) ($sizeInMB MB)"
                    }
                    
                    Write-Host $displayText -ForegroundColor White
                    $releaseAssets += [PSCustomObject]@{
                        Index         = $globalAssetIndex
                        Name          = $asset.name
                        URL           = $asset.browser_download_url
                        Size          = $asset.size
                        ReleaseId     = $i
                        AssetId       = $j
                        OriginalAsset = $asset
                        IsZip         = $asset.name -like "*.zip"
                    }
                    $globalAssetIndex++
                }
            }
        }
        
        if ($releaseAssets.Count -gt 0) {
            $filteredAssets += $releaseAssets
            Write-Host ""
        }
    }
    
    if ($filteredAssets.Count -eq 0) {
        Write-Host "No matching backup (.zip) files found in any release." -ForegroundColor Yellow
    }
    
    return $filteredAssets
}

function Download-Asset {
    param (
        [PSObject]$Asset,
        [string]$DestinationPath
    )
    
    if (-not (Test-Path $DestinationPath)) {
        New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    }
    
    $downloadUrl = $Asset.URL
    $fileName    = $Asset.Name
    $filePath    = Join-Path -Path $DestinationPath -ChildPath $fileName
    
    Write-Host "Downloading '$fileName' to '$DestinationPath'..." -ForegroundColor Cyan
    try {
        # Create a more robust web client with timeout
        $webClient = New-Object System.Net.WebClient
        $webClient.Headers.Add("User-Agent", "PowerShell")
        $webClient.DownloadFile($downloadUrl, $filePath)
        
        Write-Host "Download completed: $filePath" -ForegroundColor Green
        
        if ($Asset.IsZip) {
            Write-Host "Extracting ZIP file to root path: $DestinationPath" -ForegroundColor Cyan
            # Extract to destination path directly (not to subfolder)
            Expand-Archive -Path $filePath -DestinationPath $DestinationPath -Force
            
            # List extracted files more efficiently
            $extractedFiles = Get-ChildItem -Path $DestinationPath -Recurse -File | 
                              Where-Object { $_.FullName -ne $filePath }
            
            Write-Host "Extracted $($extractedFiles.Count) files." -ForegroundColor Green
            
            # Attempt to find .bak files in the extracted content with improved search
            $backupFiles = Get-ChildItem -Path $DestinationPath -Recurse -Include "*.bak", "*.BAK" -File
            if ($backupFiles.Count -gt 0) {
                Write-Host "Found $($backupFiles.Count) .bak file(s):" -ForegroundColor Green
                $backupFiles | ForEach-Object { Write-Host "  - $($_.FullName)" }
                return $backupFiles[0].FullName
            }
            else {
                $possibleBackupFiles = Get-ChildItem -Path $DestinationPath -Recurse -File | Where-Object {
                    $_.Name -like "*backup*" -or $_.Name -like "*bak*" -or 
                    $_.Extension -eq ".bak" -or $_.Extension -eq ".BAK"
                }
                if ($possibleBackupFiles.Count -gt 0) {
                    Write-Host "Found $($possibleBackupFiles.Count) potential backup files:" -ForegroundColor Green
                    $possibleBackupFiles | ForEach-Object { Write-Host "  - $($_.FullName)" }
                    return $possibleBackupFiles[0].FullName
                }
                else {
                    Write-Warning "No .bak files found in the extracted ZIP. Looking for any database files..."
                    $dbFiles = Get-ChildItem -Path $DestinationPath -Recurse -File | Where-Object {
                        $_.Name -like "*database*" -or $_.Name -like "*db*" -or $_.Extension -eq ".mdf"
                    }
                    
                    if ($dbFiles.Count -gt 0) {
                        Write-Host "Found $($dbFiles.Count) potential database files:" -ForegroundColor Green
                        $dbFiles | ForEach-Object { Write-Host "  - $($_.FullName)" }
                        return $dbFiles[0].FullName
                    }
                    
                    Write-Warning "No database files found. Returning first file as fallback."
                    if ($extractedFiles.Count -gt 0) {
                        return $extractedFiles[0].FullName
                    }
                    return $null
                }
            }
        }
        
        return $filePath
    }
    catch {
        Write-Error "Failed to download or process the file: $_"
        # More detailed error information
        if ($_.Exception.Message -like "*404*") {
            Write-Error "File not found on server (404). URL may be incorrect: $downloadUrl"
        }
        elseif ($_.Exception.Message -like "*time*out*") {
            Write-Error "Download timed out. File may be too large or connection is slow."
        }
        return $null
    }
}

function Test-SqlConnection {
    param (
        [string]$ServerInstance,
        [string]$Username,
        [string]$Password
    )
    try {
        $connectionString = "Server=$ServerInstance;User ID=$Username;Password=$Password;TrustServerCertificate=yes"
        $sqlConnection   = New-Object System.Data.SqlClient.SqlConnection($connectionString)
        $sqlConnection.Open()
        
        if ($sqlConnection.State -eq 'Open') {
            Write-Host "Successfully connected to SQL Server at $ServerInstance" -ForegroundColor Green
            $sqlConnection.Close()
            return $true
        }
        else {
            Write-Error "Failed to open connection to $ServerInstance."
            return $false
        }
    }
    catch {
        Write-Error "SQL Server connection test failed: $_"
        return $false
    }
}

function Get-DatabaseFilesFromBackup {
    param (
        [string]$BackupFile,
        [string]$ServerInstance,
        [string]$Username,
        [string]$Password
    )
    
    try {
        # Get container name
        $containerName = "sqlflow-mssql"
        
        # Get just the filename from the backup path with proper error handling
        if (-not (Test-Path $BackupFile)) {
            Write-Error "Backup file not found: $BackupFile"
            return @()
        }
        $backupFileInfo = Get-Item $BackupFile
        $backupFileName = $backupFileInfo.Name
        
        # Path inside container
        $containerBackupPath = "/var/opt/mssql/bak/$backupFileName"
        
        # Execute FILELISTONLY inside the container for more reliable results
        $filelistQuery = "RESTORE FILELISTONLY FROM DISK = N'$containerBackupPath'"
        Write-Host "Executing filelistQuery directly in container: $filelistQuery" -ForegroundColor Cyan
        
        # Use direct docker exec with sqlcmd to get the file list
        $filelistCmd = "docker exec $containerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P `"$Password`" -C -h -1 -W -s`",`" -Q `"$filelistQuery`" -o /tmp/filelist.csv"
        Invoke-Expression $filelistCmd 2>&1 | Out-Null
        
        # Copy the CSV from the container
        $tempCsvPath = Join-Path -Path $env:TEMP -ChildPath "filelist_$([Guid]::NewGuid().ToString()).csv"
        docker cp "${containerName}:/tmp/filelist.csv" $tempCsvPath 2>&1 | Out-Null
        
        if (-not (Test-Path $tempCsvPath)) {
            Write-Error "Failed to retrieve file list from backup"
            return @()
        }
        
        # Read and parse CSV content
        $filelistContent = Get-Content -Path $tempCsvPath -Raw
        $filelistRows = $filelistContent -split "`n" | Where-Object { $_ -match '\S' }
        
        # Initialize files array
        $files = @()
        
        # Process header row to find column indexes
        $headerRow = $filelistRows[0]
        $columns = $headerRow -split ','
        $logicalNameIndex = $columns.IndexOf("LogicalName")
        $typeIndex = $columns.IndexOf("Type")
        
        # If we can't find the columns by name, try heuristic approach
        if ($logicalNameIndex -lt 0 -or $typeIndex -lt 0) {
            Write-Host "CSV format not as expected, trying direct parsing from sqlcmd output..." -ForegroundColor Yellow
            
            # Try a different approach - direct text output
            $filelistCmd = "docker exec $containerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P `"$Password`" -Q `"$filelistQuery`""
            $rawOutput = Invoke-Expression $filelistCmd 2>&1 | Out-String
            
            # Parse the text output
            $lines = $rawOutput -split "`n" | Where-Object { $_ -match '\S' }
            
            # Find data and log files using pattern matching
            foreach ($line in $lines) {
                if ($line -match '\s*([^\s]+)\s+.*' -and $line -notmatch 'LogicalName' -and $line -notmatch '-{10,}') {
                    $logicalName = $matches[1]
                    $fileType = if ($line -match 'LOG|\.ldf' -or $logicalName -match '_log') { "Log" } else { "Data" }
                    
                    Write-Host "Found file from text output: $logicalName (Type: $fileType)" -ForegroundColor Gray
                    
                    $files += [PSCustomObject]@{
                        LogicalName = $logicalName
                        Type = $fileType
                    }
                }
            }
            
            # If still no files, perform a last-resort parse looking for specific filename patterns
            if ($files.Count -eq 0) {
                Write-Host "Attempting last-resort pattern matching for database files..." -ForegroundColor Yellow
                
                # Common patterns for data and log files
                if ($rawOutput -match '([^\s]+\.mdf)') {
                    $dataFileName = $matches[1]
                    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($dataFileName)
                    
                    # Guess the logical name from the filename
                    $dataLogicalName = $baseName -replace 'dw-', ''
                    $dataLogicalName = if ($dataLogicalName -match '_data$') { $dataLogicalName } else { $dataLogicalName + "_Data" }
                    
                    Write-Host "Inferring data file: $dataLogicalName" -ForegroundColor Gray
                    $files += [PSCustomObject]@{
                        LogicalName = $dataLogicalName
                        Type = "Data"
                    }
                    
                    # Also look for the log file
                    $logLogicalName = $baseName -replace 'dw-', ''
                    $logLogicalName = if ($logLogicalName -match '_log$') { $logLogicalName } else { $logLogicalName + "_Log" }
                    
                    Write-Host "Inferring log file: $logLogicalName" -ForegroundColor Gray
                    $files += [PSCustomObject]@{
                        LogicalName = $logLogicalName
                        Type = "Log"
                    }
                }
            }
        }
        else {
            # Process data rows
            for ($i = 1; $i -lt $filelistRows.Count; $i++) {
                $row = $filelistRows[$i]
                $values = $row -split ','
                
                if ($values.Count -gt [Math]::Max($logicalNameIndex, $typeIndex)) {
                    $logicalName = $values[$logicalNameIndex].Trim()
                    $fileType = $values[$typeIndex].Trim()
                    
                    # Convert numeric type to "Data" or "Log"
                    if ($fileType -eq "D" -or $fileType -eq "0") {
                        $fileType = "Data"
                    }
                    elseif ($fileType -eq "L" -or $fileType -eq "1") {
                        $fileType = "Log"
                    }
                    
                    $files += [PSCustomObject]@{
                        LogicalName = $logicalName
                        Type = $fileType
                    }
                }
            }
        }
        
        # Clean up
        Remove-Item -Path $tempCsvPath -Force -ErrorAction SilentlyContinue
        
        # If still no files found, create fallback entries based on database name
        if ($files.Count -eq 0) {
            $dbName = [System.IO.Path]::GetFileNameWithoutExtension($backupFileName) -replace '_\d{8}$', ''
            Write-Host "No files found, creating fallback entries for database: $dbName" -ForegroundColor Yellow
            
            $files += [PSCustomObject]@{
                LogicalName = "$dbName"
                Type = "Data"
            }
            $files += [PSCustomObject]@{
                LogicalName = "${dbName}_log"
                Type = "Log"
            }
        }
        
        Write-Host "Identified $($files.Count) files in backup:" -ForegroundColor Green
        foreach ($file in $files) {
            Write-Host "  - $($file.LogicalName) (Type: $($file.Type))" -ForegroundColor Gray
        }
        
        return $files
    }
    catch {
        Write-Error "Failed to get database files from backup: $_"
        # Create minimal fallback based on backup filename
        $dbName = [System.IO.Path]::GetFileNameWithoutExtension($backupFileName) -replace '_\d{8}$', ''
        
        Write-Host "Error occurred, creating minimal fallback entries" -ForegroundColor Yellow
        return @(
            [PSCustomObject]@{
                LogicalName = "$dbName"
                Type = "Data"
            },
            [PSCustomObject]@{
                LogicalName = "${dbName}_log"
                Type = "Log"
            }
        )
    }
}


function Restore-SqlDatabase {
    param (
        [string]$BackupFile,
        [string]$DatabaseName,
        [string]$ServerInstance,
        [string]$Username,
        [string]$Password,
        [string]$DataPath = "/var/opt/mssql/data",
        [string]$LogPath = "/var/opt/mssql/log"
    )
    try {
        Write-Host "Preparing to restore '$DatabaseName' from '$BackupFile'..." -ForegroundColor Cyan
        
        # Get just the filename from the backup path
        if (-not (Test-Path $BackupFile)) {
            Write-Error "Backup file not found: $BackupFile"
            return $false
        }
        $backupFileInfo = Get-Item $BackupFile
        $backupFileName = $backupFileInfo.Name
        
        # Container name and paths
        $containerName = "sqlflow-mssql"
        $containerBackupPath = "/var/opt/mssql/bak/$backupFileName"
        
        # Step 1: Create backup directory in container
        Write-Host "Creating backup directory in container..." -ForegroundColor Cyan
        docker exec $containerName mkdir -p /var/opt/mssql/bak 2>&1 | Out-Null
        
        # Step 2: Copy the backup file directly to the container
        Write-Host "Copying backup file to container: $backupFileName..." -ForegroundColor Cyan
        docker cp $BackupFile "${containerName}:/var/opt/mssql/bak/" 2>&1 | Out-Null
        
        # Verify the file was copied
        $verifyFile = docker exec $containerName ls -la $containerBackupPath 2>&1
        if ($LASTEXITCODE -ne 0 -or -not $verifyFile) {
            Write-Error "Failed to verify backup file in container"
            return $false
        }
        Write-Host "Backup file verified in container" -ForegroundColor Green
        
        # Set proper permissions
        docker exec $containerName chown -R mssql:mssql /var/opt/mssql/bak 2>&1 | Out-Null
        docker exec $containerName chmod -R 755 /var/opt/mssql/bak 2>&1 | Out-Null
        
        # Create the improved T-SQL restore script
        $tsqlScript = @"
-- Backup file and database name parameters
DECLARE @BackupFile NVARCHAR(255) = N'$containerBackupPath';
DECLARE @DatabaseName NVARCHAR(128) = N'$DatabaseName';

-- Get default data and log paths from server properties or use provided paths
DECLARE @DefaultDataPath NVARCHAR(512) = N'$DataPath';
DECLARE @DefaultLogPath NVARCHAR(512) = N'$LogPath';

-- Ensure paths don't end with a slash
IF RIGHT(@DefaultDataPath, 1) = '/' OR RIGHT(@DefaultDataPath, 1) = '\'
    SET @DefaultDataPath = LEFT(@DefaultDataPath, LEN(@DefaultDataPath) - 1);
    
IF RIGHT(@DefaultLogPath, 1) = '/' OR RIGHT(@DefaultLogPath, 1) = '\'
    SET @DefaultLogPath = LEFT(@DefaultLogPath, LEN(@DefaultLogPath) - 1);

-- Create table to store file list information 
CREATE TABLE #FileList (
    LogicalName NVARCHAR(128),
    PhysicalName NVARCHAR(512),
    Type CHAR(1),
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

-- Get file information
INSERT INTO #FileList EXEC('RESTORE FILELISTONLY FROM DISK = ''' + @BackupFile + '''');

-- Build dynamic restore command
DECLARE @RestoreSQL NVARCHAR(MAX) = 'RESTORE DATABASE [' + @DatabaseName + '] FROM DISK = ''' + @BackupFile + ''' WITH ';
DECLARE @MoveStatements NVARCHAR(MAX) = '';

-- Generate MOVE statements for each file
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

-- Finalize the command
SET @RestoreSQL = @RestoreSQL + @MoveStatements + 'REPLACE, STATS = 10;';

-- Display the command
PRINT 'Executing restore with following command:';
PRINT @RestoreSQL;

-- Execute the restore
EXEC sp_executesql @RestoreSQL;

-- Clean up
DROP TABLE #FileList;
"@

        # Save the T-SQL script to a temporary file
        $tempScriptFile = [System.IO.Path]::GetTempFileName() + ".sql"
        $tsqlScript | Set-Content -Path $tempScriptFile -Encoding ASCII
        
        # Copy the script to the container
        docker cp $tempScriptFile "${containerName}:/tmp/robust_restore.sql" 2>&1 | Out-Null
        
        # Execute the T-SQL script with certificate trust
        Write-Host "Executing robust T-SQL restore script..." -ForegroundColor Yellow
        $restoreCmd = "docker exec $containerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U `"$Username`" -P `"$Password`" -C -i /tmp/robust_restore.sql"
        $restoreOutput = Invoke-Expression $restoreCmd 2>&1
        
        # Display restore output
        foreach ($line in $restoreOutput) {
            if ($line -match "Error|failed|No such file|Msg \d+") {
                Write-Host $line -ForegroundColor Red
            } elseif ($line -match "RESTORE DATABASE successfully") {
                Write-Host $line -ForegroundColor Green
            } else {
                Write-Host $line -ForegroundColor Gray
            }
        }
        
        # Verify restore was successful by checking for errors
        $hasErrors = $restoreOutput -match "Msg \d+, Level \d+, State \d+"
        
        if ($hasErrors) {
            Write-Host "Errors detected during restore. Database may not have been restored properly." -ForegroundColor Red
            
            # Check specifically for database existence despite errors
            $verifySql = "SELECT name FROM sys.databases WHERE name = '$DatabaseName'"
            $verifyCmd = "docker exec $containerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U `"$Username`" -P `"$Password`" -C -Q `"$verifySql`""
            $verifyResult = Invoke-Expression $verifyCmd 2>&1
            
            if ($verifyResult -match $DatabaseName) {
                Write-Host "Despite errors, database '$DatabaseName' appears to exist!" -ForegroundColor Yellow
                return $true
            }
            
            return $false
        } else {
            # Add delay to allow SQL Server to register the database
            Write-Host "Waiting for SQL Server to register the restored database..." -ForegroundColor Yellow
            Start-Sleep -Seconds 5
            
            # Verify the database exists with certificate trust
            $verifySql = "SELECT name FROM sys.databases WHERE name = '$DatabaseName'"
            $verifyCmd = "docker exec $containerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U `"$Username`" -P `"$Password`" -C -Q `"$verifySql`""
            $verifyResult = Invoke-Expression $verifyCmd 2>&1
            
            if ($verifyResult -match $DatabaseName) {
                Write-Host "Database '$DatabaseName' restored successfully!" -ForegroundColor Green
                return $true
            } else {
                # Try one more time with a longer delay
                Write-Host "Database not found on first check, waiting longer..." -ForegroundColor Yellow
                Start-Sleep -Seconds 15
                $verifyResult = Invoke-Expression $verifyCmd 2>&1
                
                if ($verifyResult -match $DatabaseName) {
                    Write-Host "Database '$DatabaseName' restored successfully!" -ForegroundColor Green
                    return $true
                } else {
                    Write-Error "Database '$DatabaseName' not found after restore attempt"
                    return $false
                }
            }
        }
    }
    catch {
        Write-Error "Failed to restore database: $_"
        return $false
    }
    finally {
        # Clean up temp files
        if (Test-Path $tempScriptFile) { Remove-Item -Path $tempScriptFile -Force }
    }
}

# -----------------------------[ Wizard Steps ]----------------------------- #

# Modify the Step1-DownloadRelease function to immediately download after path is specified
function Step1-DownloadRelease {
    # This step fetches the GitHub releases and shows potential backup files (.zip),
    # then allows the user to select a single file to download.
    
    Write-Host "Fetching releases from '$repoOwner/$repoName'..." -ForegroundColor Cyan
    $releases = Get-GitHubReleases -Owner $repoOwner -Repo $repoName
    
    if (-not $releases -or $releases.Count -eq 0) {
        throw "No releases found or unable to retrieve from $repoOwner/$repoName."
    }
    
    # Show the user the release assets (mostly .zip containing .bak)
    $global:BackupAssets = Show-FilteredReleaseAssets -Releases $releases -SearchPattern "*"
    if ($BackupAssets.Count -eq 0) {
        throw "No backup files found in any release. Cannot continue."
    }
    
    # Always select a specific file - removed the "all files" option
    Write-Host "Please select a single file to download:" -ForegroundColor Cyan
    $selection = Read-Host "Enter the index of the file to download (0..$($BackupAssets.Count - 1))"
    $assetIndex = 0
    if (-not [int]::TryParse($selection, [ref] $assetIndex) -or 
        $assetIndex -lt 0 -or $assetIndex -ge $BackupAssets.Count) {
        Write-Host "Invalid selection. Using the first backup file instead." -ForegroundColor Yellow
        $assetIndex = 0
    }
    
    $selectedAsset = $BackupAssets[$assetIndex]
    Write-Host "Selected file: $($selectedAsset.Name)" -ForegroundColor Green
    
    # Now ask for download location after file selection
    $defaultDownloadPath = [Environment]::GetFolderPath("MyDocuments")
    $answer = Read-Host "Enter the local download location for this backup file (Press Enter for '$defaultDownloadPath')"
    if ([string]::IsNullOrWhiteSpace($answer)) {
        $global:DownloadPath = $defaultDownloadPath
    }
    else {
        $global:DownloadPath = $answer
    }
    
    $global:dockerComposePath = Join-Path -Path $global:DownloadPath -ChildPath "docker-compose.yml"

    Write-Host "Will use download path: $DownloadPath" -ForegroundColor Green
    
    # Create the directory if it doesn't exist
    if (-not (Test-Path $DownloadPath)) {
        New-Item -ItemType Directory -Path $DownloadPath -Force | Out-Null
        Write-Host "Created directory: $DownloadPath" -ForegroundColor Green
    }
    
    # Download the selected file
    Write-Host "Downloading $($selectedAsset.Name)..." -ForegroundColor Cyan
    $downloadedPath = Download-Asset -Asset $selectedAsset -DestinationPath $DownloadPath
    
    if ($downloadedPath) {
        Write-Host "Successfully downloaded and extracted to: $DownloadPath" -ForegroundColor Green
    }
    else {
        Write-Warning "Download failed for $($selectedAsset.Name). Will retry in Step 7."
    }
    
    # Check if any .bak files were extracted
    $bakFiles = Get-ChildItem -Path $DownloadPath -Recurse -Include "*.bak", "*.BAK" -File -ErrorAction SilentlyContinue
    if ($bakFiles.Count -gt 0) {
        Write-Host "Found $($bakFiles.Count) .bak files ready for restoration:" -ForegroundColor Green
        $bakFiles | Select-Object -First 5 | ForEach-Object { 
            Write-Host "  - $($_.Name)" -ForegroundColor White 
        }
        if ($bakFiles.Count -gt 5) {
            Write-Host "  - ... and $($bakFiles.Count - 5) more" -ForegroundColor White
        }
    }
    else {
        Write-Warning "No .bak files were found after download/extraction. Check the downloaded files manually."
    }
}

function Step2-SetEnvironmentVariables {
    Write-Host "Setting up environment variables for SQLFlow..." -ForegroundColor Cyan

    # Just re-using the script-level variables:
    # Format: Server=host,port;Database=database;User ID=username;Password=password;
    $sqlServerInstance = "sqlflow-mssql,1433"
    $sqlDatabase       = "dw-sqlflow-prod"

    # Construct a default connection string
    $env:SQLFlowConStr = "Server=$sqlServerInstance;Database=$sqlDatabase;User ID=$localSqlUsername;Password=$localSqlPassword;TrustServerCertificate=True;"

    # Placeholder for OpenAI key if needed
    $env:SQLFlowOpenAiApiKey = "your-openai-api-key"

    # Persist these environment variables at machine-level
    [Environment]::SetEnvironmentVariable("SQLFlowConStr", $env:SQLFlowConStr, "Machine")
    [Environment]::SetEnvironmentVariable("SQLFlowOpenAiApiKey", $env:SQLFlowOpenAiApiKey, "Machine")

    Write-Host "Environment variables set:" -ForegroundColor Green
    Write-Host " - SQLFlowConStr: $env:SQLFlowConStr" -ForegroundColor Green
}

function Step3-CleanUpExistingContainers {
    # Purpose: Thoroughly clean up all SQLFlow-related Docker resources (containers, networks, volumes)
    Write-Host "Performing comprehensive cleanup of existing Docker resources..." -ForegroundColor Cyan
    
    if (-not (Test-Path $dockerComposePath)) {
        Write-Warning "docker-compose.yml not found at path: $dockerComposePath"
        Write-Host "Will proceed with direct Docker commands for cleanup." -ForegroundColor Yellow
    }

    Push-Location $ScriptRoot
    
    # STEP 1: Stop and remove containers using docker-compose first (if available)
    if (Test-Path $dockerComposePath) {
        Write-Host "Taking down Docker Compose environment with volumes..." -ForegroundColor Yellow
        try {
            $downOutput = Invoke-Expression "$ComposeCommand down -v --remove-orphans 2>&1" | Out-String
            # Filter and display relevant parts of the output
            $downOutput -split "`n" | Where-Object { $_ -match '\S' } | ForEach-Object {
                if ($_ -notmatch "^\s*At line:" -and $_ -notmatch "^\s*\+" -and $_ -notmatch "CategoryInfo") {
                    Write-Host $_.Trim() -ForegroundColor Gray
                }
            }
        }
        catch {
            Write-Warning "Error in docker-compose down: $_"
            Write-Host "Continuing with direct Docker commands..." -ForegroundColor Yellow
        }
    }
    
    # STEP 2: Find and stop any remaining SQLFlow containers
    Write-Host "Finding all SQLFlow-related containers..." -ForegroundColor Yellow
    
    # Specific filters for SQLFlow components
    $containerFilters = @(
        "name=sqlflow-ui", 
        "name=sqlflow-api", 
        "name=sqlflow-sql", 
        "name=sqlflow-mssql",
        "ancestor=businessiq"
    )
    
    $allContainerIds = @()
    foreach ($filter in $containerFilters) {
        $containerIds = docker ps -a --filter $filter --format "{{.ID}}" 2>$null
        if ($containerIds) {
            $allContainerIds += $containerIds
        }
    }
    
    # Remove duplicates and empty entries
    $uniqueContainerIds = $allContainerIds | Where-Object { $_ } | Select-Object -Unique
    
    if ($uniqueContainerIds.Count -gt 0) {
        Write-Host "Found $($uniqueContainerIds.Count) SQLFlow-related containers to remove." -ForegroundColor Yellow
        foreach ($id in $uniqueContainerIds) {
            Write-Host "Stopping container $id..." -ForegroundColor Yellow
            docker stop $id 2>$null | Out-Null
            Write-Host "Removing container $id..." -ForegroundColor Yellow
            docker rm $id 2>$null | Out-Null
        }
    }
    else {
        Write-Host "No existing SQLFlow containers found." -ForegroundColor Green
    }
    
    # STEP 3: Remove all SQLFlow networks
    Write-Host "Removing SQLFlow networks..." -ForegroundColor Yellow
    $networks = docker network ls --filter "name=sqlflow" --format "{{.ID}}" 2>$null
    if ($networks) {
        foreach ($networkId in $networks) {
            Write-Host "Removing network $networkId..." -ForegroundColor Yellow
            docker network rm $networkId 2>$null | Out-Null
        }
    }
    
    # STEP 4: Remove all SQLFlow volumes
    Write-Host "Removing SQLFlow volumes..." -ForegroundColor Yellow
    $volumeFilters = @(
        "name=sqlflow"
    )
    
    $allVolumes = @()
    foreach ($filter in $volumeFilters) {
        $volumes = docker volume ls --filter $filter --format "{{.Name}}" 2>$null
        if ($volumes) {
            $allVolumes += $volumes
        }
    }
    
    # Remove duplicates and empty entries
    $uniqueVolumes = $allVolumes | Where-Object { $_ } | Select-Object -Unique
    
    if ($uniqueVolumes.Count -gt 0) {
        Write-Host "Found $($uniqueVolumes.Count) SQLFlow-related volumes to remove:" -ForegroundColor Yellow
        foreach ($volume in $uniqueVolumes) {
            Write-Host "  - $volume" -ForegroundColor Gray
        }
        
        # First try to remove all volumes at once
        try {
            $volumesString = $uniqueVolumes -join " "
            Write-Host "Removing all volumes..." -ForegroundColor Yellow
            $removeOutput = Invoke-Expression "docker volume rm $volumesString 2>&1" | Out-String
            
            # Check which volumes were successfully removed
            $remainingVolumes = docker volume ls --format "{{.Name}}" | Where-Object { $uniqueVolumes -contains $_ }
            
            # If any volumes remain, try force-removing them individually
            if ($remainingVolumes.Count -gt 0) {
                Write-Host "$($remainingVolumes.Count) volumes could not be removed. Trying individual removal..." -ForegroundColor Yellow
                foreach ($volume in $remainingVolumes) {
                    Write-Host "Force removing volume $volume..." -ForegroundColor Yellow
                    # Find containers using this volume
                    $usingContainers = docker ps -a --filter "volume=$volume" --format "{{.ID}}" 2>$null
                    if ($usingContainers) {
                        foreach ($containerId in $usingContainers) {
                            Write-Host "  - First stopping container $containerId using this volume..." -ForegroundColor Gray
                            docker stop $containerId 2>$null | Out-Null
                            docker rm -f $containerId 2>$null | Out-Null
                        }
                    }
                    # Now try removing the volume again
                    docker volume rm $volume 2>$null | Out-Null
                }
            }
        }
        catch {
            Write-Warning "Error removing volumes: $_"
        }
    }
    else {
        Write-Host "No SQLFlow-related volumes found." -ForegroundColor Green
    }
    
    Pop-Location
    Write-Host "Cleanup complete." -ForegroundColor Green
    
    # Final check to verify cleanup was successful
    $remainingContainers = docker ps -a --filter "name=sqlflow" --format "{{.Names}}" 2>$null
    $remainingVolumes = docker volume ls --filter "name=sqlflow" --format "{{.Name}}" 2>$null
    
    if ($remainingContainers -or $remainingVolumes) {
        Write-Host "WARNING: Some SQLFlow resources could not be removed:" -ForegroundColor Red
        if ($remainingContainers) {
            Write-Host "  Containers: $remainingContainers" -ForegroundColor Red
        }
        if ($remainingVolumes) {
            Write-Host "  Volumes: $remainingVolumes" -ForegroundColor Red
        }
        Write-Host "You may need to manually remove these resources." -ForegroundColor Yellow
    }
    else {
        Write-Host "All SQLFlow Docker resources successfully removed!" -ForegroundColor Green
    }
}

function Step4-UpdateDockerComposePaths {
    Write-Host "Updating volume paths in docker-compose.yml..." -ForegroundColor Cyan
    
    # The docker-compose.yml is at the root level of the extraction
    $dockerComposePath = Join-Path -Path $DownloadPath -ChildPath "docker-compose.yml"
    
    # Set global variable to reference this path for later steps
    $global:dockerComposePath = $dockerComposePath 

    # Check if file exists
    if (-not (Test-Path $dockerComposePath)) {
        Write-Host "docker-compose.yml not found at: $dockerComposePath" -ForegroundColor Red
        return
    }
    
    # Create backup
    Copy-Item -Path $dockerComposePath -Destination "$dockerComposePath.backup" -Force
    
    # Normalize download path for Docker
    $normalizedPath = $DownloadPath.Replace("\", "/")
    Write-Host "Using normalized path: $normalizedPath" -ForegroundColor Cyan
    
    # Simple replacement
    (Get-Content -Path $dockerComposePath -Raw) -replace "C:/SQLFlow", $normalizedPath | Set-Content -Path $dockerComposePath
    
    Write-Host "docker-compose.yml updated successfully." -ForegroundColor Green
}

function Step5-PullDockerImages {
    Write-Host "Pulling Docker images using '$ComposeCommand pull'..." -ForegroundColor Cyan
    
    Push-Location $DownloadPath 
    try {
        # Capture and clean up the output
        $pullOutput = Invoke-Expression "$ComposeCommand -f $global:dockerComposePath pull 2>&1" | Out-String
        # Filter and display relevant parts
        $pullOutput -split "`n" | Where-Object { $_ -match '\S' } | ForEach-Object {
            if ($_ -notmatch "^\s*At line:" -and $_ -notmatch "^\s*\+" -and $_ -notmatch "CategoryInfo") {
                $line = $_.Trim()
                if ($line -match "Pulling|Pulled") {
                    Write-Host $line -ForegroundColor Gray
                }
            }
        }
        Write-Host "All Docker images pulled successfully." -ForegroundColor Green
    }
    catch {
        throw "Error pulling Docker images: $_"
    }
    Pop-Location
}

function Step6-StartSqlServerContainer {
    Write-Host "Starting only the SQL Server container (sqlflow-mssql)..." -ForegroundColor Cyan
    
    Push-Location $DownloadPath
    try {
        # Capture and clean up the output
        $startSqlOutput = Invoke-Expression "$ComposeCommand -f $global:dockerComposePath up -d sqlflow-mssql 2>&1" | Out-String
        # Filter and display relevant parts
        $startSqlOutput -split "`n" | Where-Object { $_ -match '\S' } | ForEach-Object {
            if ($_ -notmatch "^\s*At line:" -and $_ -notmatch "^\s*\+" -and $_ -notmatch "CategoryInfo") {
                $line = $_.Trim()
                if ($line -match "Creating|Created|Starting|Started") {
                    Write-Host $line -ForegroundColor Gray
                }
            }
        }
    }
    catch {
        throw "Error starting sqlflow-mssql container: $_"
    }

    # Verify container is up
    $sqlContainerCheck = Invoke-Expression "$ComposeCommand -f $global:dockerComposePath ps sqlflow-mssql 2>&1" | Out-String
    if ($sqlContainerCheck -notmatch "running|healthy") {
        Write-Host "Warning: sqlflow-mssql container not clearly marked as 'running' or 'healthy' yet." -ForegroundColor Yellow
    }
    
    Write-Host "Waiting for SQL Server to finish initializing..." -ForegroundColor Yellow
    Start-Sleep -Seconds 10  # Add a delay before verification
    $maxAttempts = 10
    $attempt = 0
    $sqlReady = $false
    
    while (-not $sqlReady -and $attempt -lt $maxAttempts) {
        $attempt++
        Write-Host "Checking readiness (attempt $attempt of $maxAttempts)..." -ForegroundColor Yellow
        $logs = Invoke-Expression "$ComposeCommand -f $global:dockerComposePath logs sqlflow-mssql 2>&1" | Out-String
        
        if ($logs -match "SQL Server is now ready for client connections") {
            # Add additional wait time even after seeing the ready message
            Write-Host "SQL Server reports ready, waiting 15 more seconds for full initialization..." -ForegroundColor Yellow
            Start-Sleep -Seconds 15
            $sqlReady = $true
            Write-Host "SQL Server should now be fully initialized!" -ForegroundColor Green
        }
        else {
            Start-Sleep -Seconds 10
        }
    }

    if (-not $sqlReady) {
        Write-Host "Warning: SQL Server may not be fully initialized, continuing anyway..." -ForegroundColor Yellow
    }
    Pop-Location
}

# Here's the section of the script that I've modified to remove the database name prompt:

function Step7-RestoreDatabases {
    Write-Host "Starting database restoration from downloaded backup..." -ForegroundColor Cyan
    
    if ($global:skipDbRestore) {
        Write-Host "Skipping database restoration (skipDbRestore = $skipDbRestore)." -ForegroundColor Yellow
        return
    }
    
    # Use Docker default paths inside container
    $dataPath = $defaultDataPath
    $logPath  = $defaultLogPath
    Write-Host "Data path in container: $dataPath" -ForegroundColor Cyan
    Write-Host "Log path in container: $logPath" -ForegroundColor Cyan
    
    # Test connection with local container credentials
    $connected = Test-SqlConnection -ServerInstance $localSqlServerInstance -Username $localSqlUsername -Password $localSqlPassword
    if (-not $connected) {
        throw "Cannot connect to local SQL Server container with either 'SQLFlow' user"
    }
    
    # At this point we are connected. Find the backup file
    $bakFiles = Get-ChildItem -Path $DownloadPath -Recurse -Include "*.bak", "*.BAK" -File -ErrorAction SilentlyContinue
    
    if ($bakFiles.Count -eq 0) {
        Write-Host "No .bak files found in $DownloadPath. Skipping database restoration." -ForegroundColor Yellow
        return
    }
    
    # Ensure the container's backup directory exists
    $containerName = "sqlflow-mssql"
    Write-Host "Ensuring backup directory exists in container..." -ForegroundColor Cyan
    docker exec $containerName mkdir -p /var/opt/mssql/bak 2>$null
    docker exec $containerName chown -R mssql:mssql /var/opt/mssql/bak 2>$null
    
    # Handling file selection - allow multiple selections
    $backupFilePaths = @()
    if ($bakFiles.Count -gt 1) {
        Write-Host "Multiple .bak files found. Select files to restore:" -ForegroundColor Cyan
        for ($i = 0; $i -lt $bakFiles.Count; $i++) {
            Write-Host "[$i] $($bakFiles[$i].Name)" -ForegroundColor White
        }
        
        Write-Host "Enter the indexes of files to restore (comma-separated, e.g., '0,2,3' or 'all' for all files):" -ForegroundColor Cyan
        $selection = Read-Host
        
        if ($selection.ToLower() -eq "all") {
            # Select all files
            $backupFilePaths = $bakFiles.FullName
        }
        else {
            # Process comma-separated selection
            $selectedIndexes = $selection -split ',' | ForEach-Object { $_.Trim() }
            
            foreach ($indexStr in $selectedIndexes) {
                $bakIndex = 0
                if ([int]::TryParse($indexStr, [ref] $bakIndex) -and $bakIndex -ge 0 -and $bakIndex -lt $bakFiles.Count) {
                    $backupFilePaths += $bakFiles[$bakIndex].FullName
                }
                else {
                    Write-Host "Invalid selection index: '$indexStr'. Skipping." -ForegroundColor Yellow
                }
            }
            
            if ($backupFilePaths.Count -eq 0) {
                Write-Host "No valid files selected. Using the first backup file." -ForegroundColor Yellow
                $backupFilePaths += $bakFiles[0].FullName
            }
        }
    }
    else {
        # Only one .bak file exists
        $backupFilePaths += $bakFiles[0].FullName
    }
    
    foreach ($backupFilePath in $backupFilePaths) {
        # Extract filename without extension
        $filenameNoExt = [System.IO.Path]::GetFileNameWithoutExtension($backupFilePath)
    
        # Remove date pattern (like _YYYYMMDD) from the filename
        $cleanDbName = $filenameNoExt -replace '_\d{8}$', ''
        
        # Use filename as database name (removed user prompt)
        $proposedDbName = $cleanDbName
        
        # Restore the selected database
        Write-Host "Restoring database from $backupFilePath as '$proposedDbName'..." -ForegroundColor Cyan
        $restored = Restore-SqlDatabase -BackupFile $backupFilePath -DatabaseName $proposedDbName `
                    -ServerInstance $localSqlServerInstance -Username $localSqlUsername -Password $localSqlPassword `
                    -DataPath $dataPath -LogPath $logPath
    
        if (-not $restored) {
            Write-Host "Database restoration for '$proposedDbName' failed or partially succeeded. Check logs for details." -ForegroundColor Yellow
        }
        else {
            Write-Host "Database '$proposedDbName' restored successfully." -ForegroundColor Green
        }
    }
}

function Step7b-UpdateConnectionStrings {
    [CmdletBinding()]
    param()
    
    Write-Host "`nRunning update statements against [flw].[SysDataSource]..." -ForegroundColor Cyan
    
    # Use the globally defined SQL connection variables
    $serverInstance = $global:localSqlServerInstance
    $userId = $global:localSqlUsername
    $password = $global:localSqlPassword
    
    # Construct connection string for the SQLFlow database
    $sqlFlowConnStr = "Server=$serverInstance;Database=dw-sqlflow-prod;User ID=$userId;Password=$password;TrustServerCertificate=True;Command Timeout=360;"
    
    # Define database mappings (alias to actual database name)
    $databases = @{
        "dw-ods-prod-db" = "dw-ods-prod";
        "dw-pre-prod-db" = "dw-pre-prod";
        "wwi-db" = "WideWorldImporters"
    }
    
    # Initialize an array to store all update statements
    $updateStatements = @()
    
    # Generate update statements for each database
    foreach ($alias in $databases.Keys) {
        $dbName = $databases[$alias]
        
        # Create connection string using host.docker.internal to allow containers to communicate
        # Note: Converting port 1477 to container's perspective (1433)
        $connString = "Server=host.docker.internal,1477;Initial Catalog=$dbName;User ID=$userId;Password=$password;Persist Security Info=False;"
        
        # Add TrustServerCertificate and Encrypt settings
        $connString += "TrustServerCertificate=True;Encrypt=False;"
        
        # Add Command Timeout
        $connString += "Command Timeout=360;"
        
        # Create the update statement
        $updateStatements += "UPDATE [flw].[SysDataSource] SET ConnectionString = '$connString' WHERE Alias = '$alias';"
        
        # Log the update (without showing the password)
        $logConnString = $connString -replace "Password=[^;]+", "Password=*****"
        Write-Host "Preparing update for $alias with connection string: $logConnString" -ForegroundColor Yellow
    }
    
    # Execute all update statements
    try {
        Write-Host "`nExecuting update statements..." -ForegroundColor Yellow
        
        # First check if the SQLFlow module is available
        $sqlCmdAvailable = Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue
        
        if ($sqlCmdAvailable) {
            # Use Invoke-Sqlcmd if available
            foreach ($statement in $updateStatements) {
                # Create safe version for logging (without showing the password)
                $logStatement = $statement -replace "Password=[^;]+", "Password=*****"
                Write-Host "Executing: $logStatement" -ForegroundColor Yellow
                
                Invoke-Sqlcmd -ConnectionString $sqlFlowConnStr -Query $statement
            }
        } else {
            # Fallback to direct SQL execution using SqlClient if Invoke-Sqlcmd is not available
            Write-Host "Invoke-Sqlcmd not available, using SqlClient directly..." -ForegroundColor Yellow
            
            # Load SqlClient assembly if not already loaded
            if (-not ("System.Data.SqlClient.SqlConnection" -as [type])) {
                Add-Type -AssemblyName System.Data.SqlClient
            }
            
            $connection = New-Object System.Data.SqlClient.SqlConnection($sqlFlowConnStr)
            $command = New-Object System.Data.SqlClient.SqlCommand("", $connection)
            
            try {
                $connection.Open()
                
                foreach ($statement in $updateStatements) {
                    # Create safe version for logging (without showing the password)
                    $logStatement = $statement -replace "Password=[^;]+", "Password=*****"
                    Write-Host "Executing: $logStatement" -ForegroundColor Yellow
                    
                    $command.CommandText = $statement
                    $command.ExecuteNonQuery() | Out-Null
                }
            } finally {
                $connection.Close()
            }
        }
        
        Write-Host "Update statements executed successfully against dw-sqlflow-prod database." -ForegroundColor Green
    } catch {
        Write-Host "Error running update statements: $($_.Exception.Message)" -ForegroundColor Red
        
        # Provide additional troubleshooting information
        Write-Host "`nTroubleshooting tips:" -ForegroundColor Yellow
        Write-Host "1. Ensure the dw-sqlflow-prod database was properly restored" -ForegroundColor Yellow
        Write-Host "2. Verify the [flw].[SysDataSource] table exists in the database" -ForegroundColor Yellow
        Write-Host "3. Confirm the SQL Server is accepting connections" -ForegroundColor Yellow
        Write-Host "4. Check if the specified database aliases exist in the table" -ForegroundColor Yellow
        
        $choice = Read-Host "Would you like to continue with the rest of the setup? (Y/N)"
        if ($choice.ToUpper() -ne "Y") {
            Write-Host "Exiting script at user request." -ForegroundColor Yellow
            exit 1
        }
    }
}

function Step8-StartRemainingContainers {
    Write-Host "Starting all remaining containers (docker-compose up -d)..." -ForegroundColor Cyan
    
    Push-Location $DownloadPath
    try {
        # Capture and clean up the output
        $startAllOutput = Invoke-Expression "$ComposeCommand -f $global:dockerComposePath up -d 2>&1" | Out-String
        # Filter and display relevant parts
        $startAllOutput -split "`n" | Where-Object { $_ -match '\S' } | ForEach-Object {
            if ($_ -notmatch "^\s*At line:" -and $_ -notmatch "^\s*\+" -and $_ -notmatch "CategoryInfo") {
                $line = $_.Trim()
                if ($line -match "Creating|Created|Starting|Started") {
                    Write-Host $line -ForegroundColor Gray
                }
            }
        }
    }
    catch {
        throw "Error starting remaining containers: $_"
    }

    Write-Host "Verifying containers' status..." -ForegroundColor Yellow
    Start-Sleep -Seconds 5
    $check = Invoke-Expression "$ComposeCommand -f $global:dockerComposePath ps 2>&1" | Out-String
    if ($check -match "running|healthy") {
        Write-Host "All containers appear to be running!" -ForegroundColor Green
        Write-Host "Access SQLFlow at http://localhost:8110 or https://localhost:8111" -ForegroundColor Cyan
        
        # Quick check if port 8110 is listening
        $portCheck = Get-NetTCPConnection -LocalPort 8110 -ErrorAction SilentlyContinue
        if (-not $portCheck) {
            Write-Host "Note: Port 8110 is not yet listening. The containers may still be starting." -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "Warning: Not all containers appear to be running. Check 'docker ps' manually." -ForegroundColor Yellow
    }
    Pop-Location
}

function Invoke-ExternalCommandWithCleanOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Command,
        [string]$ErrorMessage = "Command execution failed"
    )
    
    try {
        $output = Invoke-Expression "$Command 2>&1" | Out-String
        $exitCode = $LASTEXITCODE
        
        # Filter and display clean output
        $output -split "`n" | Where-Object { $_ -match '\S' } | ForEach-Object {
            if ($_ -notmatch "^\s*At line:" -and $_ -notmatch "^\s*\+" -and $_ -notmatch "CategoryInfo") {
                Write-Host $_.Trim() -ForegroundColor Gray
            }
        }
        
        if ($exitCode -ne 0) {
            throw "$ErrorMessage (exit code: $exitCode)"
        }
        
        return $exitCode -eq 0
    }
    catch {
        Write-Error "{$ErrorMessage}: $_"
        return $false
    }
}


function Step9-Summary {
    Write-Host "`nSQLFlow Setup and Database Restoration Complete!" -ForegroundColor Green
    Write-Host "------------------------------------------------" -ForegroundColor Green
    Write-Host "Environment variables are set." -ForegroundColor Green
    Write-Host "SQL Server container is running, backups restored (if selected), and the rest of the containers are up." -ForegroundColor Green
    Write-Host "You can now access SQLFlow via http://localhost:8110 or https://localhost:8111" -ForegroundColor Green
    Write-Host "Login credentials: demo@sqlflow.io/@Demo123" -ForegroundColor Green

    Write-Host "`nConnection String for your environment:" -ForegroundColor Cyan
    Write-Host "  $($env:SQLFlowConStr)" -ForegroundColor White

    Write-Host "`nTroubleshooting Tips:" -ForegroundColor Cyan
    Write-Host "  1. Make sure Docker Desktop is running." -ForegroundColor White
    Write-Host "  2. Check docker-compose syntax:  $ComposeCommand config" -ForegroundColor White
    Write-Host "  3. Ensure ports 8110, 8111, etc. are not in use by other apps." -ForegroundColor White
    Write-Host "  4. Check logs:  $ComposeCommand logs" -ForegroundColor White
    Write-Host "  5. Confirm your .bak files were restored: Connect to $($localSqlServerInstance) with user '$($localSqlUsername)'." -ForegroundColor White
   
}

# -----------------------------[ Main Script Flow ]----------------------------- #
Write-Host "Starting SQLFlow Setup and Database Restoration Wizard..." -ForegroundColor Green

Run-Step "Step 1: Download Backup Release Info"       { Step1-DownloadRelease }
Run-Step "Step 2: Set Environment Variables"          { Step2-SetEnvironmentVariables }
Run-Step "Step 3: Clean Up Existing Containers"       { Step3-CleanUpExistingContainers }
Run-Step "Step 4: Update Docker Compose Paths"        { Step4-UpdateDockerComposePaths }
Run-Step "Step 5: Pull Docker Images"                 { Step5-PullDockerImages }
Run-Step "Step 6: Start SQL Server Container"         { Step6-StartSqlServerContainer }
Run-Step "Step 7: Database Restoration"               { Step7-RestoreDatabases }
Run-Step "Step 7b: Update Connection Strings"         { Step7b-UpdateConnectionStrings }
Run-Step "Step 8: Start Remaining Containers"         { Step8-StartRemainingContainers }
Run-Step "Step 9: Summary"                            { Step9-Summary }

Write-Host "`nWizard complete! Review any warnings above for additional actions." -ForegroundColor Green
