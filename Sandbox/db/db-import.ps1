# PowerShell script to import databases from .bak or .bacpac files

# Parameter declaration with defaults
param (
    [Parameter(Mandatory=$false)]
    [string]$BackupDirectory = "",
    
    [Parameter(Mandatory=$false)]
    [ValidateSet("bak", "bacpac")]
    [string]$ImportType = "bak",
    
    [Parameter(Mandatory=$false)]
    [switch]$UseFilenameAsDBName = $false,
    
    [Parameter(Mandatory=$false)]
    [string[]]$Databases = @()
)

# Function to display usage information
function Show-Usage {
    Write-Host "Usage: .\Import-SqlDatabases.ps1 [options]" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  -BackupDirectory <path>  : Directory containing backup files (default: will prompt user)"
    Write-Host "  -ImportType <bak|bacpac> : Type of import to perform (default: bak)"
    Write-Host "  -UseFilenameAsDBName    : Use filename as database name (default: false)"
    Write-Host "  -Databases <db1,db2,...> : Specific databases to import (default: all in directory)"
    Write-Host ""
    Write-Host "Example: .\Import-SqlDatabases.ps1 -BackupDirectory 'C:\Backups\20250304' -ImportType 'bacpac' -UseFilenameAsDBName"
    exit
}

# Show usage if requested
if ($args -contains "-h" -or $args -contains "--help") {
    Show-Usage
}

# Check if running in interactive mode (no parameters passed)
$interactiveMode = $BackupDirectory -eq "" -or ($ImportType -eq "" -and $PSBoundParameters.ContainsKey('ImportType') -eq $false)

# If in interactive mode, prompt the user for information
if ($interactiveMode) {
    Write-Host "SQL Server Database Import Tool" -ForegroundColor Cyan
    Write-Host "===============================" -ForegroundColor Cyan

    # Prompt for backup directory if not provided
    if ($BackupDirectory -eq "") {
        $BackupDirectory = Read-Host "Enter the backup directory path"
        if (-not (Test-Path $BackupDirectory)) {
            Write-Host "Error: Directory does not exist: $BackupDirectory" -ForegroundColor Red
            exit 1
        }
    }
    
    # Prompt for import type if not provided
    if (-not $PSBoundParameters.ContainsKey('ImportType')) {
        $importOptions = @{
            "1" = "bak (SQL Server backup file)";
            "2" = "bacpac (Data-tier application package)"
        }
        
        Write-Host "`nSelect import type:" -ForegroundColor Yellow
        $importOptions.GetEnumerator() | ForEach-Object {
            Write-Host "  $($_.Key): $($_.Value)"
        }
        
        $importChoice = Read-Host "`nEnter choice (1-2)"
        switch ($importChoice) {
            "1" { $ImportType = "bak" }
            "2" { $ImportType = "bacpac" }
            default {
                Write-Host "Invalid choice. Using default: bak" -ForegroundColor Yellow
                $ImportType = "bak"
            }
        }
    }
    
    # Prompt for whether to use filename as database name
    if (-not $PSBoundParameters.ContainsKey('UseFilenameAsDBName')) {
        $useFilenameChoice = Read-Host "`nUse filename as database name? (y/n)"
        $UseFilenameAsDBName = $useFilenameChoice.ToLower() -eq "y"
    }
    
    # Show databases available in the directory
    $filePattern = "*.$ImportType"
    $availableFiles = Get-ChildItem -Path $BackupDirectory -Filter $filePattern
    
    if ($availableFiles.Count -eq 0) {
        Write-Host "Error: No $ImportType files found in $BackupDirectory" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "`nAvailable $ImportType files:" -ForegroundColor Yellow
    $index = 1
    $fileMap = @{}
    
    $availableFiles | ForEach-Object {
        $fileMap["$index"] = $_.Name
        Write-Host "  $index. $($_.Name)"
        $index++
    }
    
    # Prompt for specific databases
    $importAllChoice = Read-Host "`nImport all files? (y/n)"
    if ($importAllChoice.ToLower() -ne "y") {
        $selectedIndices = Read-Host "Enter file numbers to import (comma-separated)"
        $selectedIndices = $selectedIndices.Split(",").Trim()
        
        $Databases = @()
        foreach ($idx in $selectedIndices) {
            if ($fileMap.ContainsKey($idx)) {
                $Databases += $fileMap[$idx]
            }
        }
        
        if ($Databases.Count -eq 0) {
            Write-Host "No valid files selected. Exiting." -ForegroundColor Red
            exit 1
        }
    } else {
        $Databases = $availableFiles.Name
    }
}


# Function to verify connection string parameters
function Verify-ConnectionParameters {
    param(
        [hashtable]$ConnectionParams
    )
    
    Write-Host "Verifying connection string parameters..." -ForegroundColor Yellow
    
    # Check required parameters
    $requiredParams = @('Server', 'User ID', 'Password')
    $missingParams = @()
    
    foreach ($param in $requiredParams) {
        if (-not $ConnectionParams.ContainsKey($param) -or [string]::IsNullOrWhiteSpace($ConnectionParams[$param])) {
            $missingParams += $param
        }
    }
    
    if ($missingParams.Count -gt 0) {
        Write-Host "Error: Missing required parameters in connection string: $($missingParams -join ', ')" -ForegroundColor Red
        return $false
    }
    
    # Verify server format
    $server = $ConnectionParams['Server']
    if (-not ($server -match '[\w\-\.]+(\,\d+)?')) {
        Write-Host "Warning: Server parameter format looks unusual: '$server'" -ForegroundColor Yellow
        Write-Host "Expected format: 'servername' or 'servername,port'" -ForegroundColor Yellow
    }
    
    # Verify encryption parameters
    $encryptKey = $null
    if ($ConnectionParams.ContainsKey('Encrypt')) {
        $encryptKey = 'Encrypt'
    } elseif ($ConnectionParams.ContainsKey('Encryption')) {
        $encryptKey = 'Encryption'
    }
    
    if ($encryptKey -ne $null) {
        $encryptValue = $ConnectionParams[$encryptKey]
        if ($encryptValue -notin @('True', 'False', 'yes', 'no', '1', '0')) {
            Write-Host "Warning: $encryptKey value '$encryptValue' is not a standard boolean value" -ForegroundColor Yellow
        }
    } else {
        Write-Host "Note: No encryption setting found in connection string. SQL Server may use encryption by default." -ForegroundColor Yellow
    }
    
    # Verify TrustServerCertificate setting
    if ($ConnectionParams.ContainsKey('TrustServerCertificate')) {
        $trustValue = $ConnectionParams['TrustServerCertificate']
        if ($trustValue -notin @('True', 'False', 'yes', 'no', '1', '0')) {
            Write-Host "Warning: TrustServerCertificate value '$trustValue' is not a standard boolean value" -ForegroundColor Yellow
        }
    }
    
    # Display parsed connection parameters for verification
    Write-Host "`nParsed Connection Parameters:" -ForegroundColor Cyan
    Write-Host "  Server: $($ConnectionParams['Server'])" -ForegroundColor White
    Write-Host "  User ID: $($ConnectionParams['User ID'])" -ForegroundColor White
    
    # Show encryption settings if present
    if ($encryptKey -ne $null) {
        Write-Host "  $($encryptKey): $($ConnectionParams[$encryptKey])" -ForegroundColor White
    }
    if ($ConnectionParams.ContainsKey('TrustServerCertificate')) {
        Write-Host "  TrustServerCertificate: $($ConnectionParams['TrustServerCertificate'])" -ForegroundColor White
    }
    
    # Prompt user to confirm
    $confirmChoice = Read-Host "`nDo these connection parameters look correct? (y/n)"
    if ($confirmChoice.ToLower() -ne "y") {
        Write-Host "Operation cancelled by user due to connection parameter concerns." -ForegroundColor Yellow
        return $false
    }
    
    # Try to test the connection
    try {
        Write-Host "`nTesting database connection..." -ForegroundColor Yellow
        $testQuery = "SELECT @@VERSION AS ServerVersion"
        $serverInfo = Invoke-Sqlcmd -ServerInstance $ConnectionParams['Server'] -Username $ConnectionParams['User ID'] -Password $ConnectionParams['Password'] -Query $testQuery -ErrorAction Stop -ConnectionTimeout 10
        Write-Host "Connection successful!" -ForegroundColor Green
        Write-Host "SQL Server Version: $($serverInfo.ServerVersion)" -ForegroundColor Cyan
    } catch {
        Write-Host "Warning: Could not connect to the database server: $_" -ForegroundColor Yellow
        Write-Host "Please verify your connection parameters." -ForegroundColor Yellow
        
        $proceedChoice = Read-Host "Proceed anyway? (y/n)"
        if ($proceedChoice.ToLower() -ne "y") {
            return $false
        }
    }
    
    return $true
}


# Get connection string from environment variable
$connectionString = $env:SQLFlow

# Parse connection string more robustly
$connectionParams = @{}
$connectionString.Split(';') | ForEach-Object {
    if ($_ -match '(.+?)=(.+)') {
        $key = $matches[1].Trim()
        $value = $matches[2].Trim()
        $connectionParams[$key] = $value
    }
}

# Extract needed parameters
$serverName = $connectionParams['Server']
$userId = $connectionParams['User ID']
$password = $connectionParams['Password']
$trustServerCertificate = $connectionParams['TrustServerCertificate']
$encrypt = $connectionParams['Encrypt']

# Verify connection parameters
$connectionValid = Verify-ConnectionParameters -ConnectionParams $connectionParams
if (-not $connectionValid) {
    $exitChoice = Read-Host "Do you want to exit the script? (y/n)"
    if ($exitChoice.ToLower() -eq "y") {
        Write-Host "Exiting script due to connection parameter concerns." -ForegroundColor Yellow
        exit 1
    }
    Write-Host "Continuing with the provided connection parameters." -ForegroundColor Yellow
}

# Validate the required parameters
if (-not $serverName -or -not $userId -or -not $password) {
    Write-Host "Error: Missing required parameters in connection string." -ForegroundColor Red
    exit 1
}

# Check for required modules
if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    Write-Host "SqlServer module not found. Installing..." -ForegroundColor Yellow
    Install-Module -Name SqlServer -Force -AllowClobber -Scope CurrentUser
}

# Import SqlServer module
Import-Module SqlServer

# Function to check and install SqlPackage if needed (for BACPAC import)
function Ensure-SqlPackageInstalled {
    # List of possible SqlPackage locations
    $possiblePaths = @(
        "C:\Program Files\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe",
        "C:\Program Files\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe",
        "C:\Program Files\Microsoft SQL Server\140\DAC\bin\SqlPackage.exe",
        "C:\Program Files\Microsoft SQL Server\130\DAC\bin\SqlPackage.exe",
        "C:\Program Files (x86)\Microsoft SQL Server\160\DAC\bin\SqlPackage.exe",
        "C:\Program Files (x86)\Microsoft SQL Server\150\DAC\bin\SqlPackage.exe",
        "C:\Program Files (x86)\Microsoft SQL Server\140\DAC\bin\SqlPackage.exe",
        "C:\Program Files (x86)\Microsoft SQL Server\130\DAC\bin\SqlPackage.exe"
    )
    
    # Try to find SqlPackage in common locations
    foreach ($path in $possiblePaths) {
        if (Test-Path $path) {
            Write-Host "Found SqlPackage at: $path" -ForegroundColor Green
            return $path
        }
    }
    
    # Try to find SqlPackage in PATH
    $sqlPackageInPath = Get-Command "SqlPackage.exe" -ErrorAction SilentlyContinue
    if ($sqlPackageInPath) {
        Write-Host "Found SqlPackage in PATH: $($sqlPackageInPath.Source)" -ForegroundColor Green
        return $sqlPackageInPath.Source
    }
    
    # Search for SqlPackage recursively
    Write-Host "Searching for SqlPackage.exe in Program Files directories..." -ForegroundColor Yellow
    $foundFiles = Get-ChildItem -Path "C:\Program Files", "C:\Program Files (x86)" -Recurse -Filter "SqlPackage.exe" -ErrorAction SilentlyContinue
    if ($foundFiles.Count -gt 0) {
        Write-Host "Found SqlPackage at: $($foundFiles[0].FullName)" -ForegroundColor Green
        return $foundFiles[0].FullName
    }
    
    # If not found, download and install SqlPackage
    Write-Host "SqlPackage not found. Downloading and installing..." -ForegroundColor Yellow
    
    # Create temp directory
    $tempDir = "$env:TEMP\SqlPackageInstall"
    if (-not (Test-Path $tempDir)) {
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    }
    
    # Download SqlPackage
    $downloadUrl = "https://aka.ms/sqlpackage-windows"
    $zipFile = "$tempDir\sqlpackage.zip"
    
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipFile
        
        # Extract to a specific folder
        $extractPath = "$env:ProgramFiles\SqlPackage"
        if (-not (Test-Path $extractPath)) {
            New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
        }
        
        Write-Host "Extracting SqlPackage..." -ForegroundColor Yellow
        Expand-Archive -Path $zipFile -DestinationPath $extractPath -Force
        
        # Verify and return path
        $sqlPackagePath = "$extractPath\SqlPackage.exe"
        if (Test-Path $sqlPackagePath) {
            Write-Host "SqlPackage installed successfully at: $sqlPackagePath" -ForegroundColor Green
            
            # Add to PATH for future use
            $currentPath = [Environment]::GetEnvironmentVariable("PATH", "Machine")
            if (-not $currentPath.Contains($extractPath)) {
                [Environment]::SetEnvironmentVariable("PATH", "$currentPath;$extractPath", "Machine")
                Write-Host "Added SqlPackage directory to system PATH" -ForegroundColor Green
            }
            
            return $sqlPackagePath
        } else {
            Write-Host "Error: SqlPackage.exe not found after extraction" -ForegroundColor Red
            return $null
        }
    } catch {
        Write-Host "Error installing SqlPackage: $_" -ForegroundColor Red
        return $null
    } finally {
        # Clean up
        if (Test-Path $zipFile) {
            Remove-Item $zipFile -Force
        }
    }
}

# Function to import database from .bak file
function Import-DatabaseFromBak {
    param (
        [string]$BackupFile,
        [string]$TargetDatabaseName
    )
    
    Write-Host "Importing $TargetDatabaseName from $BackupFile..." -ForegroundColor Yellow
    
    try {
        # Check if database exists
        $dbExists = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query "SELECT name FROM sys.databases WHERE name = '$TargetDatabaseName'" -ErrorAction Stop
        
        if ($dbExists) {
            Write-Host "Database $TargetDatabaseName already exists. Do you want to replace it?" -ForegroundColor Yellow
            $replaceChoice = Read-Host "Replace existing database? (y/n)"
            
            if ($replaceChoice.ToLower() -eq "y") {
                # Set database to single user mode and drop
                $dropQuery = @"
ALTER DATABASE [$TargetDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$TargetDatabaseName];
"@
                Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $dropQuery -ErrorAction Stop
                Write-Host "Existing database $TargetDatabaseName dropped." -ForegroundColor Yellow
            } else {
                Write-Host "Skipping import of $TargetDatabaseName." -ForegroundColor Yellow
                return $false
            }
        }
        
        # Get logical file names from backup
        Write-Host "Retrieving logical file names from backup..." -ForegroundColor Yellow
        $fileListQuery = "RESTORE FILELISTONLY FROM DISK = N'$BackupFile'"
        $fileList = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $fileListQuery -ErrorAction Stop
        
        # Prepare restore command
        $moveFiles = ""
        $defaultDataPath = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query "EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'DefaultData'" -ErrorAction SilentlyContinue
        $defaultLogPath = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query "EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'DefaultLog'" -ErrorAction SilentlyContinue
        
        if ($defaultDataPath -eq $null -or $defaultLogPath -eq $null) {
            $defaultInstanceQuery = @"
SELECT SERVERPROPERTY('InstanceDefaultDataPath') AS DefaultData, 
       SERVERPROPERTY('InstanceDefaultLogPath') AS DefaultLog
"@
            $defaultPaths = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $defaultInstanceQuery -ErrorAction SilentlyContinue
            
            if ($defaultPaths) {
                $defaultDataPath = $defaultPaths.DefaultData
                $defaultLogPath = $defaultPaths.DefaultLog
            } else {
                # Fallback to system drive
                $defaultDataPath = "C:\Program Files\Microsoft SQL Server\MSSQL15.MSSQLSERVER\MSSQL\DATA"
                $defaultLogPath = "C:\Program Files\Microsoft SQL Server\MSSQL15.MSSQLSERVER\MSSQL\DATA"
            }
        }
        
        # Build the MOVE statements for each file
        foreach ($file in $fileList) {
            $logicalName = $file.LogicalName
            $physicalName = [System.IO.Path]::GetFileName($file.PhysicalName)
            $fileType = $file.Type
            
            # Determine target path based on file type (0 = data, 1 = log)
            if ($fileType -eq 1) {
                $targetPath = Join-Path -Path $defaultLogPath -ChildPath "$TargetDatabaseName`_$physicalName"
            } else {
                $targetPath = Join-Path -Path $defaultDataPath -ChildPath "$TargetDatabaseName`_$physicalName"
            }
            
            $moveFiles += "MOVE N'$logicalName' TO N'$targetPath', "
        }
        
        # Remove trailing comma and space
        $moveFiles = $moveFiles.TrimEnd(", ")
        
        # Restore database
        $restoreQuery = @"
RESTORE DATABASE [$TargetDatabaseName] 
FROM DISK = N'$BackupFile' 
WITH $moveFiles, 
     REPLACE, 
     STATS = 10
"@
        
        Write-Host "Restoring database $TargetDatabaseName..." -ForegroundColor Yellow
        Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $restoreQuery -ErrorAction Stop
        
        Write-Host "Database $TargetDatabaseName restored successfully." -ForegroundColor Green
        return $true
    } catch {
        Write-Host "Error restoring database ${TargetDatabaseName} from ${BackupFile}: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Function to import database from .bacpac file
function Import-DatabaseFromBacpac {
    param (
        [string]$BacpacFile,
        [string]$TargetDatabaseName,
        [string]$SqlPackagePath
    )
    
    Write-Host "Importing $TargetDatabaseName from $BacpacFile..." -ForegroundColor Yellow
    
    try {
        # Check if database exists
        $dbExists = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query "SELECT name FROM sys.databases WHERE name = '$TargetDatabaseName'" -ErrorAction Stop
        
        if ($dbExists) {
            Write-Host "Database $TargetDatabaseName already exists. Do you want to replace it?" -ForegroundColor Yellow
            $replaceChoice = Read-Host "Replace existing database? (y/n)"
            
            if ($replaceChoice.ToLower() -eq "y") {
                # Set database to single user mode and drop
                $dropQuery = @"
ALTER DATABASE [$TargetDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$TargetDatabaseName];
"@
                Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $dropQuery -ErrorAction Stop
                Write-Host "Existing database $TargetDatabaseName dropped." -ForegroundColor Yellow
            } else {
                Write-Host "Skipping import of $TargetDatabaseName." -ForegroundColor Yellow
                return $false
            }
        }
        
        # Build properties string based on connection string parameters
        $properties = ""
        if ($trustServerCertificate -eq "True") { $properties += "TrustServerCertificate=True;" }
        if ($encrypt -eq "False") { $properties += "Encrypt=False;" }
        $properties = $properties.TrimEnd(';')
        
        # Import BACPAC using SqlPackage with proper SSL settings
        if ($properties) {
            & $SqlPackagePath /Action:Import /SourceFile:$BacpacFile /TargetServerName:$serverName /TargetDatabaseName:$TargetDatabaseName /TargetUser:$userId /TargetPassword:$password /Properties:$properties
        } else {
            & $SqlPackagePath /Action:Import /SourceFile:$BacpacFile /TargetServerName:$serverName /TargetDatabaseName:$TargetDatabaseName /TargetUser:$userId /TargetPassword:$password
        }
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Database $TargetDatabaseName imported successfully from $BacpacFile." -ForegroundColor Green
            return $true
        } else {
            Write-Host "Error importing database $TargetDatabaseName from $BacpacFile. Exit code: $LASTEXITCODE" -ForegroundColor Red
            return $false
        }
    } catch {
        Write-Host "Error importing database $TargetDatabaseName from ${BacpacFile}: $_" -ForegroundColor Red
        return $false
    }
}

# Function to extract base name from filename (remove date part)
function Get-BaseDatabaseName {
    param (
        [string]$FileName
    )
    
    # Remove extension
    $nameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($FileName)
    
    # Check for common date patterns and remove them
    # Pattern 1: name_YYYYMMDD or name-YYYYMMDD
    if ($nameWithoutExtension -match '^(.+?)[-_](20\d{6})$') {
        return $matches[1]
    }
    # Pattern 2: name_YYYY-MM-DD or name-YYYY-MM-DD
    elseif ($nameWithoutExtension -match '^(.+?)[-_](20\d{2}-\d{2}-\d{2})$') {
        return $matches[1]
    }
    # Pattern 3: name_YYYYMMDD_HHMMSS or name-YYYYMMDD-HHMMSS
    elseif ($nameWithoutExtension -match '^(.+?)[-_](20\d{6})[-_](\d{6})$') {
        return $matches[1]
    }
    
    # Return original name if no date pattern is found
    return $nameWithoutExtension
}

# Ensure we have a backup directory
if (-not (Test-Path $BackupDirectory)) {
    Write-Host "Error: Backup directory does not exist: $BackupDirectory" -ForegroundColor Red
    exit 1
}

# Initialize success/failure counters
$successCount = 0
$failureCount = 0

# If no databases specified, get all matching files in the directory
if ($Databases.Count -eq 0) {
    $filePattern = "*.$ImportType"
    $Databases = (Get-ChildItem -Path $BackupDirectory -Filter $filePattern).Name
    
    if ($Databases.Count -eq 0) {
        Write-Host "Error: No $ImportType files found in $BackupDirectory" -ForegroundColor Red
        exit 1
    }
}

# Process based on import type
if ($ImportType -eq "bacpac") {
    # Ensure SqlPackage is installed
    $sqlPackagePath = Ensure-SqlPackageInstalled
    if (-not $sqlPackagePath) {
        Write-Host "Error: SqlPackage not found and could not be installed. Cannot import BACPAC files." -ForegroundColor Red
        exit 1
    }
    
    # Process each BACPAC file
    foreach ($database in $Databases) {
        $bacpacFile = Join-Path -Path $BackupDirectory -ChildPath $database
        
        # Skip if file doesn't exist
        if (-not (Test-Path $bacpacFile)) {
            Write-Host "Error: File not found: $bacpacFile" -ForegroundColor Red
            $failureCount++
            continue
        }
        
        # Determine target database name
        if ($UseFilenameAsDBName) {
            # Extract database name from filename (remove date portion and extension)
            $targetDbName = Get-BaseDatabaseName -FileName $database
        } else {
            # Prompt for database name, suggesting the base name as default
            $defaultName = Get-BaseDatabaseName -FileName $database
            $targetDbName = Read-Host "Enter target database name for $database [default: $defaultName]"
            if ([string]::IsNullOrWhiteSpace($targetDbName)) {
                $targetDbName = $defaultName
            }
        }
        
        # Import the database
        $result = Import-DatabaseFromBacpac -BacpacFile $bacpacFile -TargetDatabaseName $targetDbName -SqlPackagePath $sqlPackagePath
        
        if ($result) {
            $successCount++
        } else {
            $failureCount++
        }
        
        Write-Host "-------------------------------------------" -ForegroundColor Gray
    }
} else {
    # Process each BAK file
    foreach ($database in $Databases) {
        $bakFile = Join-Path -Path $BackupDirectory -ChildPath $database
        
        # Skip if file doesn't exist
        if (-not (Test-Path $bakFile)) {
            Write-Host "Error: File not found: $bakFile" -ForegroundColor Red
            $failureCount++
            continue
        }
        
        # Determine target database name
        if ($UseFilenameAsDBName) {
            # Extract database name from filename (remove date portion and extension)
            $targetDbName = Get-BaseDatabaseName -FileName $database
        } else {
            # Prompt for database name, suggesting the base name as default
            $defaultName = Get-BaseDatabaseName -FileName $database
            $targetDbName = Read-Host "Enter target database name for $database [default: $defaultName]"
            if ([string]::IsNullOrWhiteSpace($targetDbName)) {
                $targetDbName = $defaultName
            }
        }
        
        # Import the database
        $result = Import-DatabaseFromBak -BackupFile $bakFile -TargetDatabaseName $targetDbName
        
        if ($result) {
            $successCount++
        } else {
            $failureCount++
        }
        
        Write-Host "-------------------------------------------" -ForegroundColor Gray
    }
}

# Display summary
Write-Host "`nImport Summary:" -ForegroundColor Cyan
Write-Host "Files processed: $($Databases.Count)" -ForegroundColor White
Write-Host "Successfully imported: $successCount" -ForegroundColor $(if ($successCount -eq $Databases.Count) { "Green" } else { "Yellow" })
Write-Host "Failed imports: $failureCount" -ForegroundColor $(if ($failureCount -gt 0) { "Red" } else { "Green" })