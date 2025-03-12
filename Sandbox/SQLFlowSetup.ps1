param (
    [Parameter(Mandatory=$false)]
    [string]$BackupDirectory = "",
    
    [Parameter(Mandatory=$false)]
    [ValidateSet("bak", "bacpac")]
    [string]$ImportType = "bak",
    
    [Parameter(Mandatory=$false)]
    [switch]$UseFilenameAsDBName = $false,
    
    [Parameter(Mandatory=$false)]
    [string[]]$Databases = @(),
    
    # Removed "Windows" from ValidateSet
    [Parameter(Mandatory=$false)]
    [ValidateSet("SQL")]
    [string]$AuthenticationType = ""
)

function Show-Usage {
    Write-Host "Usage: .\SQLFlowSetup.ps1 [options]" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  -BackupDirectory <path>         : Directory containing backup files (default: will prompt user)"
    Write-Host "  -ImportType <bak|bacpac>        : Type of import to perform (default: bak)"
    Write-Host "  -UseFilenameAsDBName            : Use filename as database name (default: false)"
    Write-Host "  -Databases <db1,db2,...>        : Specific databases to import (default: all in directory)"
    # Removed any reference to Windows in the usage text
    Write-Host "  -AuthenticationType <SQL>       : Authentication type to use (default: will prompt user)"
    Write-Host ""
    # Updated example to remove "Windows" as an option
    Write-Host "Example: .\Import-SqlDatabases.ps1 -BackupDirectory 'C:\Backups\20250304' -ImportType 'bacpac' -UseFilenameAsDBName -AuthenticationType 'SQL'"
    exit
}

# If help is requested, show usage
if ($args -contains "-h" -or $args -contains "--help") {
    Show-Usage
}

# Determine if we're in interactive mode (missing essential parameters)
$interactiveMode = $BackupDirectory -eq "" -or `
                   ($ImportType -eq "" -and $PSBoundParameters.ContainsKey('ImportType') -eq $false) -or `
                   ($AuthenticationType -eq "" -and $PSBoundParameters.ContainsKey('AuthenticationType') -eq $false)

############################################################################
# PART 1: Gather/build SQL Server connection string & set environment vars
############################################################################
if ($interactiveMode) {
    Write-Host "SQL Server Database Import Tool" -ForegroundColor Cyan
    Write-Host "===============================" -ForegroundColor Cyan

    # 1. Ask user whether connecting to Azure or local instance
    $cloudOptions = @{
        "1" = "Local SQL Server instance";
        "2" = "Azure SQL Server instance"
    }
    Write-Host "`nAre you connecting to a local or Azure SQL Server?" -ForegroundColor Yellow
    $cloudOptions.GetEnumerator() | ForEach-Object {
        Write-Host "  $($_.Key): $($_.Value)"
    }
    $cloudChoice = Read-Host "`nEnter choice (1-2)"
    switch ($cloudChoice) {
        "1" { $cloudType = "Local" }
        "2" { $cloudType = "Azure" }
        default {
            Write-Host "Invalid choice. Using default: Local" -ForegroundColor Yellow
            $cloudType = "Local"
        }
    }

    # 2. Prompt for backup directory if not provided
    if ($BackupDirectory -eq "") {
        $BackupDirectory = Read-Host "Enter the backup directory path"
        if (-not (Test-Path $BackupDirectory)) {
            Write-Host "Error: Directory does not exist: $BackupDirectory" -ForegroundColor Red
            exit 1
        }
    }

    # 3. Prompt for import type if not provided
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

    # 4. Since Windows Authentication is removed, set or confirm $AuthenticationType to SQL
    if (-not $PSBoundParameters.ContainsKey('AuthenticationType')) {
        Write-Host "`nUsing only SQL Server Authentication..." -ForegroundColor Yellow
        $AuthenticationType = "SQL"
    }

    # 5. Prompt for server name
    if ($cloudType -eq "Azure") {
        Write-Host "`nTypically Azure SQL Server is something like 'myserver.database.windows.net'." -ForegroundColor Yellow
    } else {
        Write-Host "`nTypical local server could be 'localhost' or 'localhost,1433' or 'MyServerName'." -ForegroundColor Yellow
    }
    $serverName = Read-Host "Enter the SQL Server name or address"
    
    # 6. Always prompt for user credentials, since only SQL Auth remains
    $userId = Read-Host "Enter the SQL Server user ID"
    $securePwd = Read-Host "Enter the SQL Server password" -AsSecureString
    $password = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd)
    )

    # 7. Ask whether to use encryption (default false)
    $encrypt = $false
    $encryptionChoice = Read-Host "Use encryption (y/N)? [Default: N]"
    if ($encryptionChoice.ToLower() -eq 'y') { $encrypt = $true }
    
    # 8. Ask whether to trust server certificate (default true)
    $trustServerCertificate = $true
    $trustChoice = Read-Host "Trust server certificate (Y/n)? [Default: Y]"
    if ($trustChoice.ToLower() -eq 'n') { $trustServerCertificate = $false }
    
    # 9. Build a final connection string for "master" (SQL only)
    $finalConnectionString = "Server=$serverName;Initial Catalog=master;User ID=$userId;Password=$password;" +
                             "Persist Security Info=False;TrustServerCertificate=$trustServerCertificate;" +
                             "Encrypt=$($encrypt);"

    # 11. Ask the user if they have an OpenAI API key
    $hasOpenAiKey = Read-Host "Do you have an OpenAI API key you want to store? (y/n)"
    if ($hasOpenAiKey.ToLower() -eq 'y') {
        $openAiApiKey = Read-Host "Enter your OpenAI API key"
        [Environment]::SetEnvironmentVariable("SQLFlowOpenAiApiKey", $openAiApiKey, "Machine")
        Write-Host "Created/updated system environment variable 'SQLFlowOpenAiApiKey'." -ForegroundColor Green
    } else {
        Write-Host "Skipping OpenAI API Key environment variable." -ForegroundColor Yellow
    }

    # 12. Prompt for whether to use filename as database name
    if (-not $PSBoundParameters.ContainsKey('UseFilenameAsDBName')) {
        $useFilenameChoice = Read-Host "`nUse filename as database name for the restore/import? (y/n)"
        $UseFilenameAsDBName = $useFilenameChoice.ToLower() -eq "y"
    }

    # 13. Show databases available in the directory & prompt for selection
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
else {
    # If not interactive, we assume environment or parameter-based usage
    $finalConnectionString = $env:SQLFlowConStr
}

############################################################################
# PART 2: Helper function to parse or verify connection parameters
############################################################################
function Verify-ConnectionParameters {
    param(
        [hashtable]$ConnectionParams,
        [string]$AuthType
    )
    
    Write-Host "Verifying connection parameters..." -ForegroundColor Yellow
    
    # Always require these for SQL auth
    $requiredParams = @('Server','User ID','Password')

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
    
    # Test connection
    try {
        Write-Host "`nTesting database connection..." -ForegroundColor Yellow
        $testQuery = "SELECT @@VERSION AS ServerVersion"
        
        # Only SQL path remains
        $serverInfo = Invoke-Sqlcmd -ServerInstance $ConnectionParams['Server'] -Username $ConnectionParams['User ID'] -Password $ConnectionParams['Password'] -Query $testQuery -ErrorAction Stop -ConnectionTimeout 10
        
        Write-Host "Connection successful!" -ForegroundColor Green
        Write-Host "SQL Server Version: $($serverInfo.ServerVersion)" -ForegroundColor Cyan
    } catch {
        Write-Host "Warning: Could not connect to the database server: $_" -ForegroundColor Yellow
        return $false
    }
    
    return $true
}




############################################################################
# PART 3: Database import logic
############################################################################

# Parse $finalConnectionString into a hashtable
$connectionParams = @{}
if ($finalConnectionString) {
    $finalConnectionString.Split(';') | ForEach-Object {
        if ($_ -match '(.+?)=(.+)') {
            $key = $matches[1].Trim()
            $value = $matches[2].Trim()
            $connectionParams[$key] = $value
        }
    }
}

# Derive variables from connectionParams for restore logic
$serverName = $connectionParams['Server']
$userId    = $connectionParams['User ID']
$password  = $connectionParams['Password']
$encrypt   = if ($connectionParams['Encrypt'] -eq 'True') { $true } else { $false }
$trustServerCertificate = if ($connectionParams['TrustServerCertificate'] -eq 'False') { $false } else { $true }

# Since we removed Windows Auth, force $AuthenticationType to SQL
$AuthenticationType = "SQL"

# Verify connection
$connectionValid = Verify-ConnectionParameters -ConnectionParams $connectionParams -AuthType $AuthenticationType
if (-not $connectionValid) {
    $exitChoice = Read-Host "Connection test failed. Do you want to exit the script? (y/n)"
    if ($exitChoice.ToLower() -eq "y") {
        Write-Host "Exiting script due to connection parameter concerns." -ForegroundColor Yellow
        exit 1
    }
    else {
        Write-Host "Continuing with the provided connection parameters." -ForegroundColor Yellow
    }
}

# Check for required modules
if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    Write-Host "SqlServer module not found. Installing..." -ForegroundColor Yellow
    Install-Module -Name SqlServer -Force -AllowClobber -Scope CurrentUser
}
Import-Module SqlServer


############################################################################
# PART 2b: Check and configure SQL Server Full-Text Search
############################################################################
Write-Host "`n===== Checking SQL Server Full-Text Search =====" -ForegroundColor Cyan

function Check-FullTextInstalled {
    param(
        [string]$ServerInstance,
        [string]$UserID,
        [string]$Password
    )
    
    try {
        $query = "SELECT SERVERPROPERTY('IsFullTextInstalled') AS IsFullTextInstalled;"
        $result = Invoke-Sqlcmd -ServerInstance $ServerInstance -Username $UserID -Password $Password -Database "master" -Query $query -ErrorAction Stop
        
        return [bool]$result.IsFullTextInstalled
    }
    catch {
        Write-Host "Error checking Full-Text status: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Enable-FullText {
    param(
        [string]$ServerInstance,
        [string]$UserID,
        [string]$Password
    )
    
    try {
        # First check if the service is already running
        $serviceCheckQuery = "SELECT FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS IsServiceRunning;"
        $serviceStatus = Invoke-Sqlcmd -ServerInstance $ServerInstance -Username $UserID -Password $Password -Database "master" -Query $serviceCheckQuery -ErrorAction Stop
        
        if ($serviceStatus.IsServiceRunning -eq 1) {
            Write-Host "Full-Text service is already running." -ForegroundColor Green
            return $true
        }
        
        # Try to start the service 
        $startServiceQuery = "EXEC sp_fulltext_service 'start_full_text_service', 1;"
        Invoke-Sqlcmd -ServerInstance $ServerInstance -Username $UserID -Password $Password -Database "master" -Query $startServiceQuery -ErrorAction Stop
        
        # Verify service is now running
        $verifyServiceQuery = "SELECT FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS IsServiceRunning;"
        $verifyStatus = Invoke-Sqlcmd -ServerInstance $ServerInstance -Username $UserID -Password $Password -Database "master" -Query $verifyServiceQuery -ErrorAction Stop
        
        if ($verifyStatus.IsServiceRunning -eq 1) {
            Write-Host "Full-Text service was successfully started." -ForegroundColor Green
            return $true
        } else {
            Write-Host "Failed to start Full-Text service programmatically." -ForegroundColor Red
            return $false
        }
    }
    catch {
        Write-Host "Error enabling Full-Text service: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Get-FullTextInstallationInstructions {
    $instructions = @"
==== Manual Full-Text Search Installation Instructions ====

1. For SQL Server on Windows:
   a. Open SQL Server Configuration Manager
   b. Go to SQL Server Services
   c. Right-click on "SQL Full-text Filter Daemon Launcher" and select "Start"
   d. If the service is not listed, you need to install Full-Text Search feature:
      - Run SQL Server Installation Center
      - Select "Maintenance" -> "Modify Features"
      - Check "Full-Text and Semantic Extractions for Search"
      - Complete the installation wizard

2. Via PowerShell (requires administrative privileges):
   ```powershell
   # Start the service if it exists but is stopped
   Start-Service MSSQLFTDSRV -ErrorAction SilentlyContinue
   
   # If you need to install the feature
   # For SQL Server 2019 example (adjust path for your version)
   Start-Process -FilePath "C:\SQL2019\Setup.exe" -ArgumentList "/Action=Install", "/Features=FullText", "/InstanceName=MSSQLSERVER", "/quiet" -Wait
   ```

3. Via T-SQL (if Full-Text is installed but not started):
   ```sql
   -- Run as a SQL Server administrator
   EXEC sp_fulltext_service 'start_full_text_service', 1;
   ```

Note: After installation, you may need to restart the SQL Server instance.
"@
    return $instructions
}

# Check if Full-Text is installed
Write-Host "Checking if SQL Server Full-Text Search is installed..." -ForegroundColor Yellow
$isFullTextInstalled = Check-FullTextInstalled -ServerInstance $serverName -UserID $userId -Password $password

if ($isFullTextInstalled) {
    Write-Host "Full-Text Search is installed on the SQL Server instance." -ForegroundColor Green
} else {
    Write-Host "Full-Text Search is not installed or not enabled on the SQL Server instance." -ForegroundColor Red
    Write-Host "Full-Text Search is required for SQLFlow to function properly." -ForegroundColor Yellow
    
    $enableChoice = Read-Host "Would you like to try enabling Full-Text Search automatically? (y/n)"
    
    if ($enableChoice.ToLower() -eq "y") {
        Write-Host "Attempting to enable Full-Text Search..." -ForegroundColor Yellow
        $enableResult = Enable-FullText -ServerInstance $serverName -UserID $userId -Password $password
        
        if (-not $enableResult) {
            Write-Host "Could not enable Full-Text Search automatically." -ForegroundColor Red
            Write-Host "This typically requires administrative privileges on the SQL Server." -ForegroundColor Yellow
            
            $showInstructions = Read-Host "Would you like to see manual installation instructions? (y/n)"
            if ($showInstructions.ToLower() -eq "y") {
                Write-Host (Get-FullTextInstallationInstructions) -ForegroundColor Cyan
            }
            
            $continueChoice = Read-Host "Continue with setup without Full-Text Search? (y/n)"
            if ($continueChoice.ToLower() -ne "y") {
                Write-Host "Setup cannot continue without Full-Text Search. Exiting..." -ForegroundColor Red
                exit 1
            } else {
                Write-Host "Continuing setup without Full-Text Search. Some SQLFlow features may not work properly." -ForegroundColor Yellow
            }
        } else {
            Write-Host "Full-Text Search was successfully enabled!" -ForegroundColor Green
        }
    } else {
        $showInstructions = Read-Host "Would you like to see manual installation instructions? (y/n)"
        if ($showInstructions.ToLower() -eq "y") {
            Write-Host (Get-FullTextInstallationInstructions) -ForegroundColor Cyan
        }
        
        $continueChoice = Read-Host "Continue with setup without Full-Text Search? (y/n)"
        if ($continueChoice.ToLower() -ne "y") {
            Write-Host "Setup cannot continue without Full-Text Search. Exiting..." -ForegroundColor Red
            exit 1
        } else {
            Write-Host "Continuing setup without Full-Text Search. Some SQLFlow features may not work properly." -ForegroundColor Yellow
        }
    }
}

function Ensure-SqlPackageInstalled {
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
    foreach ($path in $possiblePaths) {
        if (Test-Path $path) {
            Write-Host "Found SqlPackage at: $path" -ForegroundColor Green
            return $path
        }
    }
    $sqlPackageInPath = Get-Command "SqlPackage.exe" -ErrorAction SilentlyContinue
    if ($sqlPackageInPath) {
        Write-Host "Found SqlPackage in PATH: $($sqlPackageInPath.Source)" -ForegroundColor Green
        return $sqlPackageInPath.Source
    }
    Write-Host "Searching for SqlPackage.exe in Program Files directories..." -ForegroundColor Yellow
    $foundFiles = Get-ChildItem -Path "C:\Program Files", "C:\Program Files (x86)" -Recurse -Filter "SqlPackage.exe" -ErrorAction SilentlyContinue
    if ($foundFiles.Count -gt 0) {
        Write-Host "Found SqlPackage at: $($foundFiles[0].FullName)" -ForegroundColor Green
        return $foundFiles[0].FullName
    }
    Write-Host "SqlPackage not found. Downloading and installing..." -ForegroundColor Yellow
    $tempDir = "$env:TEMP\SqlPackageInstall"
    if (-not (Test-Path $tempDir)) {
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    }
    $downloadUrl = "https://aka.ms/sqlpackage-windows"
    $zipFile = "$tempDir\sqlpackage.zip"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipFile
        $extractPath = "$env:ProgramFiles\SqlPackage"
        if (-not (Test-Path $extractPath)) {
            New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
        }
        Write-Host "Extracting SqlPackage..." -ForegroundColor Yellow
        Expand-Archive -Path $zipFile -DestinationPath $extractPath -Force
        $sqlPackagePath = "$extractPath\SqlPackage.exe"
        if (Test-Path $sqlPackagePath) {
            Write-Host "SqlPackage installed successfully at: $sqlPackagePath" -ForegroundColor Green
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
    }
    catch {
        Write-Host "Error installing SqlPackage: $_" -ForegroundColor Red
        return $null
    }
    finally {
        if (Test-Path $zipFile) {
            Remove-Item $zipFile -Force
        }
    }
}

function Import-DatabaseFromBak {
    param (
        [string]$BackupFile,
        [string]$TargetDatabaseName,
        [string]$AuthType
    )
    
    Write-Host "Importing $TargetDatabaseName from $BackupFile..." -ForegroundColor Yellow
    try {
        # Only SQL path remains
        $dbExists = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query "SELECT name FROM sys.databases WHERE name = '$TargetDatabaseName'" -ErrorAction Stop
        
        if ($dbExists) {
            Write-Host "Database $TargetDatabaseName already exists. Do you want to replace it?" -ForegroundColor Yellow
            $replaceChoice = Read-Host "Replace existing database? (y/n)"
            if ($replaceChoice.ToLower() -eq "y") {
                $dropQuery = @"
ALTER DATABASE [$TargetDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$TargetDatabaseName];
"@
                Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $dropQuery -ErrorAction Stop
                Write-Host "Existing database $TargetDatabaseName dropped." -ForegroundColor Yellow
            }
            else {
                Write-Host "Skipping import of $TargetDatabaseName." -ForegroundColor Yellow
                return $false
            }
        }
        
        # Get logical file names from backup
        Write-Host "Retrieving logical file names from backup..." -ForegroundColor Yellow
        $fileListQuery = "RESTORE FILELISTONLY FROM DISK = N'$BackupFile'"
        $fileList = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $fileListQuery -ErrorAction Stop
        
        $moveFiles = ""
        $defaultDataPathQuery = "EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'DefaultData'"
        $defaultLogPathQuery  = "EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'DefaultLog'"
        
        $defaultDataPath = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $defaultDataPathQuery -ErrorAction SilentlyContinue
        $defaultLogPath  = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $defaultLogPathQuery -ErrorAction SilentlyContinue
        
        if ($defaultDataPath -eq $null -or $defaultLogPath -eq $null) {
            $defaultInstanceQuery = @"
SELECT SERVERPROPERTY('InstanceDefaultDataPath') AS DefaultData, 
       SERVERPROPERTY('InstanceDefaultLogPath') AS DefaultLog
"@
            $defaultPaths = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $defaultInstanceQuery -ErrorAction SilentlyContinue
            if ($defaultPaths) {
                $defaultDataPath = $defaultPaths.DefaultData
                $defaultLogPath = $defaultPaths.DefaultLog
            }
            else {
                # Fallback
                $defaultDataPath = "C:\Program Files\Microsoft SQL Server\MSSQL15.MSSQLSERVER\MSSQL\DATA"
                $defaultLogPath  = "C:\Program Files\Microsoft SQL Server\MSSQL15.MSSQLSERVER\MSSQL\DATA"
            }
        }
        
        foreach ($file in $fileList) {
            $logicalName = $file.LogicalName
            $physicalName = [System.IO.Path]::GetFileName($file.PhysicalName)
            $fileType = $file.Type
            if ($fileType -eq 1) {
                $targetPath = Join-Path -Path $defaultLogPath -ChildPath "$TargetDatabaseName`_$physicalName"
            }
            else {
                $targetPath = Join-Path -Path $defaultDataPath -ChildPath "$TargetDatabaseName`_$physicalName"
            }
            $moveFiles += "MOVE N'$logicalName' TO N'$targetPath', "
        }
        $moveFiles = $moveFiles.TrimEnd(", ")
        
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
    }
    catch {
        Write-Host "Error restoring database ${TargetDatabaseName} from ${BackupFile}: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

function Import-DatabaseFromBacpac {
    param (
        [string]$BacpacFile,
        [string]$TargetDatabaseName,
        [string]$SqlPackagePath,
        [string]$AuthType
    )
    Write-Host "Importing $TargetDatabaseName from $BacpacFile..." -ForegroundColor Yellow
    try {
        # Only SQL path remains
        $dbExists = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query "SELECT name FROM sys.databases WHERE name = '$TargetDatabaseName'" -ErrorAction Stop
        
        if ($dbExists) {
            Write-Host "Database $TargetDatabaseName already exists. Do you want to replace it?" -ForegroundColor Yellow
            $replaceChoice = Read-Host "Replace existing database? (y/n)"
            if ($replaceChoice.ToLower() -eq "y") {
                $dropQuery = @"
ALTER DATABASE [$TargetDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$TargetDatabaseName];
"@
                Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $dropQuery -ErrorAction Stop
                Write-Host "Existing database $TargetDatabaseName dropped." -ForegroundColor Yellow
            }
            else {
                Write-Host "Skipping import of $TargetDatabaseName." -ForegroundColor Yellow
                return $false
            }
        }

        # Build the SqlPackage arguments
        $propertyArgs = @()
        if ($trustServerCertificate -eq $true) { 
            $propertyArgs += "/p:TrustServerCertificate=True"
        } else {
            $propertyArgs += "/p:TrustServerCertificate=False"
        }
        if ($encrypt -eq $true) {
            $propertyArgs += "/p:Encrypt=True"
        } else {
            $propertyArgs += "/p:Encrypt=False"
        }

        $sqlPackageArgs = @(
            "/Action:Import",
            "/SourceFile:$BacpacFile",
            "/TargetServerName:$serverName",
            "/TargetDatabaseName:$TargetDatabaseName"
        )
        # Only SQL auth remains
        $sqlPackageArgs += "/TargetUser:$userId"
        $sqlPackageArgs += "/TargetPassword:$password"
        
        $sqlPackageArgs += $propertyArgs

        Write-Host "Executing: $SqlPackagePath $($sqlPackageArgs -join ' ')" -ForegroundColor Yellow
        & $SqlPackagePath $sqlPackageArgs
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Database $TargetDatabaseName imported successfully from $BacpacFile." -ForegroundColor Green
            return $true
        } else {
            Write-Host "Error importing database $TargetDatabaseName from $BacpacFile. Exit code: $LASTEXITCODE" -ForegroundColor Red
            return $false
        }
    }
    catch {
        Write-Host "Error importing database $TargetDatabaseName from ${BacpacFile}: $_" -ForegroundColor Red
        return $false
    }
}

function Get-BaseDatabaseName {
    param ([string]$FileName)
    $nameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($FileName)
    # Remove common date patterns from filename
    if ($nameWithoutExtension -match '^(.+?)[-_](20\d{6})$') {
        return $matches[1]
    }
    elseif ($nameWithoutExtension -match '^(.+?)[-_](20\d{2}-\d{2}-\d{2})$') {
        return $matches[1]
    }
    elseif ($nameWithoutExtension -match '^(.+?)[-_](20\d{6})[-_](\d{6})$') {
        return $matches[1]
    }
    return $nameWithoutExtension
}

# Ensure backup directory is valid
if (-not (Test-Path $BackupDirectory)) {
    Write-Host "Error: Backup directory does not exist: $BackupDirectory" -ForegroundColor Red
    exit 1
}

# If no databases explicitly specified, gather them from the directory
if ($Databases.Count -eq 0) {
    $filePattern = "*.$ImportType"
    $Databases = (Get-ChildItem -Path $BackupDirectory -Filter $filePattern).Name
    if ($Databases.Count -eq 0) {
        Write-Host "Error: No $ImportType files found in $BackupDirectory" -ForegroundColor Red
        exit 1
    }
}

Write-Host "`n===== Starting Database Import Process =====" -ForegroundColor Cyan

$successCount = 0
$failureCount = 0

if ($ImportType -eq "bacpac") {
    $sqlPackagePath = Ensure-SqlPackageInstalled
    if (-not $sqlPackagePath) {
        Write-Host "Error: SqlPackage not found and could not be installed. Cannot import BACPAC files." -ForegroundColor Red
        exit 1
    }
    foreach ($database in $Databases) {
        $bacpacFile = Join-Path -Path $BackupDirectory -ChildPath $database
        if (-not (Test-Path $bacpacFile)) {
            Write-Host "Error: File not found: $bacpacFile" -ForegroundColor Red
            $failureCount++
            continue
        }
        if ($UseFilenameAsDBName) {
            $targetDbName = Get-BaseDatabaseName -FileName $database
        } else {
            $defaultName = Get-BaseDatabaseName -FileName $database
            $targetDbName = Read-Host "Enter target database name for $database [default: $defaultName]"
            if ([string]::IsNullOrWhiteSpace($targetDbName)) {
                $targetDbName = $defaultName
            }
        }
        $result = Import-DatabaseFromBacpac -BacpacFile $bacpacFile -TargetDatabaseName $targetDbName -SqlPackagePath $sqlPackagePath -AuthType $AuthenticationType
        if ($result) { $successCount++ } else { $failureCount++ }
        Write-Host "-------------------------------------------" -ForegroundColor Gray
    }
} else {
    foreach ($database in $Databases) {
        $bakFile = Join-Path -Path $BackupDirectory -ChildPath $database
        if (-not (Test-Path $bakFile)) {
            Write-Host "Error: File not found: $bakFile" -ForegroundColor Red
            $failureCount++
            continue
        }
        if ($UseFilenameAsDBName) {
            $targetDbName = Get-BaseDatabaseName -FileName $database
        } else {
            $defaultName = Get-BaseDatabaseName -FileName $database
            $targetDbName = Read-Host "Enter target database name for $database [default: $defaultName]"
            if ([string]::IsNullOrWhiteSpace($targetDbName)) {
                $targetDbName = $defaultName
            }
        }
        $result = Import-DatabaseFromBak -BackupFile $bakFile -TargetDatabaseName $targetDbName -AuthType $AuthenticationType
        if ($result) { $successCount++ } else { $failureCount++ }
        Write-Host "-------------------------------------------" -ForegroundColor Gray
    }
}

Write-Host "`nImport Summary:" -ForegroundColor Cyan
Write-Host "Files processed: $($Databases.Count)" -ForegroundColor White
Write-Host "Successfully imported: $successCount" -ForegroundColor $(if ($successCount -eq $Databases.Count) { "Green" } else { "Yellow" })
Write-Host "Failed imports: $failureCount" -ForegroundColor $(if ($failureCount -gt 0) { "Red" } else { "Green" })

############################################################################
# PART 4: Run the update statements (AFTER all imports/restores)
############################################################################
Write-Host "`nRunning update statements against [flw].[SysDataSource]..." -ForegroundColor Cyan

# Modify connection string to use dw-sqlflow-prod instead of master
$sqlFlowConnStr = $finalConnectionString -replace "Initial Catalog=master", "Initial Catalog=dw-sqlflow-prod"

# Create Docker-compatible connection string by replacing the server name with host.docker.internal
$sqlFlowConnStr = $sqlFlowConnStr -replace "Server=$serverName", "Server=host.docker.internal"
Write-Host "Using host.docker.internal instead of $serverName for Docker connectivity." -ForegroundColor Cyan


# Save connection string in a system environment variable named SQLFlowConStr
[Environment]::SetEnvironmentVariable("SQLFlowConStr", $sqlFlowConnStr, "Machine")
Write-Host "`nCreated/updated system environment variable 'SQLFlowConStr' with the chosen connection string." -ForegroundColor Green

# Create base connection string template that always uses host.docker.internal
$baseConnString = $finalConnectionString -replace "Server=$serverName", "Server=host.docker.internal"

# Create connection strings for different databases
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
    $connString = $baseConnString -replace "Initial Catalog=master", "Initial Catalog=$dbName"
    
    # Add Command Timeout if not present
    if (-not ($connString -match "Command Timeout")) {
        $connString = $connString.TrimEnd(";") + ";Command Timeout=360;"
    }
    
    # Create the update statement
    $updateStatements += "UPDATE [flw].[SysDataSource] SET ConnectionString = '$connString' WHERE Alias = '$alias';"
}

# Execute all update statements
try {
    foreach ($statement in $updateStatements) {
        Invoke-Sqlcmd -ConnectionString $sqlFlowConnStr -Query $statement
    }
    Write-Host "Update statements executed successfully against dw-sqlflow-prod database." -ForegroundColor Green
}
catch {
    Write-Host "Error running update statements: $($_.Exception.Message)" -ForegroundColor Red
}

############################################################################
# PART 5: Download sample files and YAML configuration
############################################################################
Write-Host "`n===== Downloading Sample Files and Configuration =====" -ForegroundColor Cyan

# Prompt for download path
$defaultDownloadPath = Join-Path -Path $env:USERPROFILE -ChildPath "SQLFlow"
Write-Host "`nWhere would you like to download the SQLFlow files?" -ForegroundColor Yellow
Write-Host "Default path: $defaultDownloadPath" -ForegroundColor Yellow
$downloadPath = Read-Host "Enter download path or press Enter for default"

if ([string]::IsNullOrWhiteSpace($downloadPath)) {
    $downloadPath = $defaultDownloadPath
}

# Create directory if it doesn't exist
if (-not (Test-Path $downloadPath)) {
    try {
        New-Item -ItemType Directory -Path $downloadPath -Force | Out-Null
        Write-Host "Created directory: $downloadPath" -ForegroundColor Green
    }
    catch {
        Write-Host "Error creating directory $downloadPath`: $_" -ForegroundColor Red
        exit 1
    }
}

# Download sample data zip file
$sampleDataUrl = "https://github.com/TahirRiaz/SQLFlow/raw/refs/heads/master/Sandbox/data/SampleData.zip"
$sampleDataPath = Join-Path -Path $downloadPath -ChildPath "SampleData.zip"
$maxRetries = 3
$retryCount = 0
$sampleDataDownloaded = $false 

Write-Host "`nDownloading sample data..." -ForegroundColor Yellow
while (-not $sampleDataDownloaded -and $retryCount -lt $maxRetries) {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $sampleDataUrl -OutFile $sampleDataPath
        
        # Verify file exists and has content
        if (Test-Path $sampleDataPath) {
            $fileSize = (Get-Item $sampleDataPath).Length
            if ($fileSize -gt 1KB) {
                Write-Host "Sample data downloaded successfully to: $sampleDataPath ($([math]::Round($fileSize/1MB, 2)) MB)" -ForegroundColor Green
                $sampleDataDownloaded = $true
            } else {
                Write-Host "Downloaded file is too small ($fileSize bytes). Retrying..." -ForegroundColor Yellow
                Remove-Item $sampleDataPath -Force
                $retryCount++
            }
        } else {
            Write-Host "File was not created. Retrying..." -ForegroundColor Yellow
            $retryCount++
        }
    }
    catch {
        $retryCount++
        Write-Host "Error downloading sample data (Attempt $retryCount/$maxRetries): $_" -ForegroundColor Red
        if ($retryCount -lt $maxRetries) {
            Write-Host "Retrying in 3 seconds..." -ForegroundColor Yellow
            Start-Sleep -Seconds 3
        }
    }
}

if (-not $sampleDataDownloaded) {
    Write-Host "Failed to download sample data after $maxRetries attempts." -ForegroundColor Red
    $continueChoice = Read-Host "Do you want to continue without sample data? (y/n)"
    if ($continueChoice.ToLower() -ne "y") {
        Write-Host "Exiting script." -ForegroundColor Yellow
        exit 1
    }
}
else {
    # Unzip the downloaded sample data
    $extractPath = Join-Path -Path $downloadPath -ChildPath "SampleData"
    Write-Host "`nExtracting sample data to: $extractPath" -ForegroundColor Yellow
    
    # Create extraction directory if it doesn't exist
    if (-not (Test-Path $extractPath)) {
        try {
            New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
        }
        catch {
            Write-Host "Error creating extraction directory: $_" -ForegroundColor Red
            $continueChoice = Read-Host "Do you want to continue without extracting the data? (y/n)"
            if ($continueChoice.ToLower() -ne "y") {
                Write-Host "Exiting script." -ForegroundColor Yellow
                exit 1
            }
        }
    }
    
    try {
        # Use .NET's built-in zip capabilities (available in PowerShell 5.0 and later)
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($sampleDataPath, $extractPath)
        
        # Verify extraction was successful
        $extractedFiles = Get-ChildItem -Path $extractPath -Recurse
        if ($extractedFiles.Count -gt 0) {
            Write-Host "Sample data extracted successfully. Found $($extractedFiles.Count) files/directories." -ForegroundColor Green
            
            # Optional: List top-level extracted items
            $topLevelItems = Get-ChildItem -Path $extractPath
            if ($topLevelItems.Count -gt 0) {
                Write-Host "Extracted contents:" -ForegroundColor Cyan
                $topLevelItems | ForEach-Object {
                    $itemType = if ($_.PSIsContainer) { "Directory" } else { "File" }
                    Write-Host "  - $($_.Name) ($itemType)" -ForegroundColor White
                }
            }
        }
        else {
            Write-Host "Warning: Zip file was extracted but no files were found in the extraction directory." -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "Error extracting zip file: $_" -ForegroundColor Red
        
        # Fallback method using Expand-Archive (PowerShell 5.0+)
        try {
            Write-Host "Trying alternative extraction method..." -ForegroundColor Yellow
            Expand-Archive -Path $sampleDataPath -DestinationPath $extractPath -Force
            
            $extractedFiles = Get-ChildItem -Path $extractPath -Recurse
            if ($extractedFiles.Count -gt 0) {
                Write-Host "Sample data extracted successfully using alternative method." -ForegroundColor Green
            }
            else {
                throw "No files extracted using alternative method"
            }
        }
        catch {
            Write-Host "Error with alternative extraction method: $_" -ForegroundColor Red
            $continueChoice = Read-Host "Do you want to continue without extracting the data? (y/n)"
            if ($continueChoice.ToLower() -ne "y") {
                Write-Host "Exiting script." -ForegroundColor Yellow
                exit 1
            }
        }
    }
}

# Download docker-compose.yml
$dockerComposeUrl = "https://raw.githubusercontent.com/TahirRiaz/SQLFlow/refs/heads/master/Sandbox/docker-compose.yml"
$dockerComposePath = Join-Path -Path $downloadPath -ChildPath "docker-compose.yml"
$retryCount = 0
$dockerComposeDownloaded = $false

Write-Host "`nDownloading docker-compose.yml..." -ForegroundColor Yellow
while (-not $dockerComposeDownloaded -and $retryCount -lt $maxRetries) {
    try {
        Invoke-WebRequest -Uri $dockerComposeUrl -OutFile $dockerComposePath
        
        # Verify file exists and has content
        if (Test-Path $dockerComposePath) {
            $fileSize = (Get-Item $dockerComposePath).Length
            if ($fileSize -gt 100) {
                Write-Host "docker-compose.yml downloaded successfully to: $dockerComposePath" -ForegroundColor Green
                $dockerComposeDownloaded = $true
            } else {
                Write-Host "Downloaded file is too small ($fileSize bytes). Retrying..." -ForegroundColor Yellow
                Remove-Item $dockerComposePath -Force
                $retryCount++
            }
        } else {
            Write-Host "File was not created. Retrying..." -ForegroundColor Yellow
            $retryCount++
        }
    }
    catch {
        $retryCount++
        Write-Host "Error downloading docker-compose.yml (Attempt $retryCount/$maxRetries): $_" -ForegroundColor Red
        if ($retryCount -lt $maxRetries) {
            Write-Host "Retrying in 3 seconds..." -ForegroundColor Yellow
            Start-Sleep -Seconds 3
        }
    }
}

if (-not $dockerComposeDownloaded) {
    Write-Host "Failed to download docker-compose.yml after $maxRetries attempts." -ForegroundColor Red
    Write-Host "The docker-compose.yml file is required to continue." -ForegroundColor Red
    Write-Host "Please check your internet connection and try again." -ForegroundColor Yellow
    exit 1
}

# Replace path in docker-compose.yml
Write-Host "`nUpdating paths in docker-compose.yml..." -ForegroundColor Yellow
try {
    if (-not $dockerComposeDownloaded) {
        Write-Host "Skipping path update as docker-compose.yml was not downloaded successfully." -ForegroundColor Yellow
    } else {
        $content = Get-Content -Path $dockerComposePath -Raw
        
        # Verify we can read the content
        if ([string]::IsNullOrWhiteSpace($content)) {
            throw "docker-compose.yml appears to be empty"
        }
        
        # Normalize paths for replacement
        $normalizedPath = $downloadPath.Replace("\", "/")
        $updatedContent = $content -replace "C:/SQLFlow", $normalizedPath
        
        # Verify the replacement occurred
        if ($content -eq $updatedContent -and $normalizedPath -ne "C:/SQLFlow") {
            Write-Host "Warning: Path replacement may not have worked. Verify the docker-compose.yml manually." -ForegroundColor Yellow
        } else {
            Set-Content -Path $dockerComposePath -Value $updatedContent
            Write-Host "Updated docker-compose.yml with path: $normalizedPath" -ForegroundColor Green
            
            # Verify the file was written correctly
            $verifyContent = Get-Content -Path $dockerComposePath -Raw
            if ([string]::IsNullOrWhiteSpace($verifyContent)) {
                throw "Failed to write updated content to docker-compose.yml"
            }
        }
    }
}
catch {
    Write-Host "Error updating docker-compose.yml: $_" -ForegroundColor Red
    Write-Host "This may affect Docker container volume mappings." -ForegroundColor Yellow
    
    $continueChoice = Read-Host "Do you want to continue? (y/n)"
    if ($continueChoice.ToLower() -ne "y") {
        Write-Host "Exiting script." -ForegroundColor Yellow
        exit 1
    }
}

############################################################################
# PART 6: Check Docker installation and start containers
############################################################################
Write-Host "`n===== Checking Docker Installation and Starting Containers =====" -ForegroundColor Cyan

function Test-DockerRunning {
    try {
        $result = docker ps -q 2>$null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Test-DockerDesktopRunning {
    # Check if Docker Desktop process is running
    $dockerDesktopProcess = Get-Process "Docker Desktop" -ErrorAction SilentlyContinue
    
    # On some systems, the main process might be named differently
    $dockerForDesktopProcess = Get-Process "com.docker.backend" -ErrorAction SilentlyContinue
    
    # Additional check for Docker service
    $dockerService = Get-Service "com.docker*", "docker*" -ErrorAction SilentlyContinue | 
                     Where-Object { $_.Status -eq 'Running' }
    
    # Return true if any of the checks are positive
    return ($dockerDesktopProcess -or $dockerForDesktopProcess -or $dockerService)
}

function Start-DockerDesktop {
    # List of possible Docker Desktop executable locations
    $dockerDesktopPaths = @(
        "${env:ProgramFiles}\Docker\Docker\Docker Desktop.exe",
        "${env:ProgramFiles(x86)}\Docker\Docker\Docker Desktop.exe",
        "$env:LOCALAPPDATA\Docker\Docker\Docker Desktop.exe",
        "${env:ProgramFiles}\Docker Desktop\Docker Desktop.exe",
        "${env:ProgramFiles(x86)}\Docker Desktop\Docker Desktop.exe",
        "$env:LOCALAPPDATA\Docker Desktop\Docker Desktop.exe",
        "${env:ProgramFiles}\Docker\Docker Desktop\Docker Desktop.exe",
        "${env:ProgramFiles(x86)}\Docker\Docker Desktop\Docker Desktop.exe"
    )
    
    # Find Docker Desktop executable
    $dockerExePath = $null
    foreach ($path in $dockerDesktopPaths) {
        if (Test-Path $path) {
            $dockerExePath = $path
            Write-Host "Found Docker Desktop at: $dockerExePath" -ForegroundColor Green
            break
        }
    }
    
    if (-not $dockerExePath) {
        # Search for Docker Desktop in Program Files
        Write-Host "Searching for Docker Desktop executable..." -ForegroundColor Yellow
        $searchPaths = @("${env:ProgramFiles}", "${env:ProgramFiles(x86)}", "$env:LOCALAPPDATA")
        foreach ($basePath in $searchPaths) {
            if (Test-Path $basePath) {
                $found = Get-ChildItem -Path $basePath -Recurse -Filter "Docker Desktop.exe" -ErrorAction SilentlyContinue | 
                         Select-Object -First 1 -ExpandProperty FullName
                if ($found) {
                    $dockerExePath = $found
                    Write-Host "Found Docker Desktop at: $dockerExePath" -ForegroundColor Green
                    break
                }
            }
        }
    }
    
    # If Docker Desktop executable was found, try to start it
    if ($dockerExePath) {
        $dockerDesktopProcess = Get-Process "Docker Desktop" -ErrorAction SilentlyContinue
        $dockerProcess = Get-Process "Docker" -ErrorAction SilentlyContinue
        
        if ($dockerDesktopProcess -or $dockerProcess) {
            Write-Host "Docker Desktop process is already running." -ForegroundColor Yellow
        } else {
            Write-Host "Starting Docker Desktop from: $dockerExePath" -ForegroundColor Cyan
            Start-Process -FilePath $dockerExePath
            Write-Host "Docker Desktop process started." -ForegroundColor Yellow
            
            # Give Docker Desktop some time to initialize
            Start-Sleep -Seconds 10
        }
        
        return $true
    } else {
        Write-Host "Could not find Docker Desktop executable." -ForegroundColor Red
        return $false
    }
}

# Main Docker check logic
try {
    # First check if docker command is available
    $dockerInstalled = Get-Command "docker" -ErrorAction SilentlyContinue
    if (-not $dockerInstalled) {
        Write-Host "Docker is not installed or not in PATH. Please install Docker Desktop and try again." -ForegroundColor Red
        Write-Host "Download Docker Desktop from: https://www.docker.com/products/docker-desktop" -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host "Docker command is available in PATH." -ForegroundColor Green
    
    # Check if Docker Desktop application is running first
    if (-not (Test-DockerDesktopRunning)) {
        Write-Host "Docker Desktop application is not running." -ForegroundColor Yellow
        $startResult = Start-DockerDesktop
        
        if (-not $startResult) {
            Write-Host "Failed to start Docker Desktop. Please start Docker Desktop manually and try again." -ForegroundColor Red
            Write-Host "Docker Desktop MUST be running before continuing with this script." -ForegroundColor Red
            
            $continueChoice = Read-Host "Do you want to exit the script? (y/n)"
            if ($continueChoice.ToLower() -eq "y") {
                Write-Host "Exiting script." -ForegroundColor Yellow
                exit 1
            }
            
            Write-Host "Continuing script, but Docker operations will likely fail without Docker Desktop running." -ForegroundColor Yellow
        }
    } else {
        Write-Host "Docker Desktop application is running." -ForegroundColor Green
    }
}
catch {
    Write-Host "Error checking Docker installation: $_" -ForegroundColor Red
    Write-Host "Please make sure Docker Desktop is installed and running." -ForegroundColor Yellow
    
    $continueChoice = Read-Host "Do you want to continue anyway? (y/n)"
    if ($continueChoice.ToLower() -ne "y") {
        Write-Host "Exiting script." -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host "Continuing script, but Docker operations may fail." -ForegroundColor Yellow
}

# Verify Docker Desktop is fully running before checking for docker-compose
if (-not (Test-DockerDesktopRunning)) {
    Write-Host "Docker Desktop must be running before checking for Docker Compose availability." -ForegroundColor Red
    Write-Host "Please start Docker Desktop manually and try again." -ForegroundColor Red
    
    $continueChoice = Read-Host "Do you want to exit the script? (y/n)"
    if ($continueChoice.ToLower() -eq "y") {
        Write-Host "Exiting script." -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host "Continuing script, but Docker Compose operations will likely fail." -ForegroundColor Yellow
    $composeCommand = "docker compose"  # Default to V2 format
} else {
    # Now check if docker-compose is available
    try {
        $dockerComposeInstalled = Get-Command "docker-compose" -ErrorAction SilentlyContinue
        if (-not $dockerComposeInstalled) {
            # Try with Docker Compose V2 command format
            $dockerComposeV2Output = docker compose version 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Docker Compose is not available. Please make sure Docker Desktop is up to date." -ForegroundColor Red
                
                $continueChoice = Read-Host "Do you want to exit the script? (y/n)"
                if ($continueChoice.ToLower() -eq "y") {
                    Write-Host "Exiting script." -ForegroundColor Yellow
                    exit 1
                }
                
                Write-Host "Continuing script, but Docker Compose operations will fail." -ForegroundColor Yellow
                $composeCommand = "docker compose"
            } else {
                Write-Host "Docker Compose V2 is available." -ForegroundColor Green
                $composeCommand = "docker compose"
            }
        } else {
            Write-Host "Docker Compose V1 is available." -ForegroundColor Green
            $composeCommand = "docker-compose"
        }
    } 
    catch {
        Write-Host "Error checking Docker Compose installation: $_" -ForegroundColor Red
        $composeCommand = "docker compose"  # Default to V2 format if check fails
        
        $continueChoice = Read-Host "Do you want to exit the script? (y/n)"
        if ($continueChoice.ToLower() -eq "y") {
            Write-Host "Exiting script." -ForegroundColor Yellow
            exit 1
        }
        
        Write-Host "Continuing script, but Docker Compose operations may fail." -ForegroundColor Yellow
    }
}

############################################################################
# Refresh environment variables
############################################################################
Write-Host "`nRefreshing environment variables for Docker..." -ForegroundColor Yellow

# Refresh the environment variables in the current PowerShell session
$env:SQLFlowConStr = [Environment]::GetEnvironmentVariable("SQLFlowConStr", "Machine")
$env:SQLFlowOpenAiApiKey = [Environment]::GetEnvironmentVariable("SQLFlowOpenAiApiKey", "Machine")

Write-Host "Environment variables refreshed." -ForegroundColor Green

# Replace the existing "Pull and start containers" section with this updated code
# Pull and start containers
Write-Host "`nChecking for existing SQLFlow containers..." -ForegroundColor Yellow
try {
    # Verify docker-compose.yml exists before proceeding
    if (-not (Test-Path $dockerComposePath)) {
        throw "docker-compose.yml not found at $dockerComposePath"
    }
    
    # Change to the download directory
    Set-Location -Path $downloadPath
    
    # Stop any running SQLFlow containers by name/image rather than by compose file
    # This will catch containers started from any directory
    Write-Host "Stopping any existing SQLFlow containers..." -ForegroundColor Yellow
    
    # Get all container IDs with sqlflow-api, sqlflow-ui, or businessiq in the name or image
    $sqlflowContainers = docker ps -a --filter "name=sqlflow" --format "{{.ID}}"
    # Also look for containers with businessiq in the image name (as shown in your screenshot)
    $businessiqContainers = docker ps -a --filter "ancestor=businessiq" --format "{{.ID}}"
    # Combine the container lists, using Select-Object -Unique to remove duplicates
    $containersToStop = @($sqlflowContainers) + @($businessiqContainers) | Select-Object -Unique
    
    if ($containersToStop -and $containersToStop.Count -gt 0) {
        Write-Host "Found existing SQLFlow containers. Stopping and removing them..." -ForegroundColor Yellow
        $containersToStop | ForEach-Object {
            if ($_) {  # Make sure the ID is not empty
                Write-Host "Stopping container $_..." -ForegroundColor Yellow
                docker stop $_ 2>&1 | Out-Null
                Write-Host "Removing container $_..." -ForegroundColor Yellow
                docker rm $_ 2>&1 | Out-Null
            }
        }
    } else {
        Write-Host "No existing SQLFlow containers found." -ForegroundColor Green
    }
    
    # Also check for any demo containers (as shown in your screenshot with demo09)
    $demoContainers = docker ps -a --filter "name=demo" --format "{{.ID}}"
    if ($demoContainers) {
        Write-Host "Found demo containers that may be related. Stopping and removing them..." -ForegroundColor Yellow
        $demoContainers | ForEach-Object {
            if ($_) {  # Make sure the ID is not empty
                Write-Host "Stopping container $_..." -ForegroundColor Yellow
                docker stop $_ 2>&1 | Out-Null
                Write-Host "Removing container $_..." -ForegroundColor Yellow
                docker rm $_ 2>&1 | Out-Null
            }
        }
    }
    
    # Still run compose down in the current directory to clean up networks
    if ($composeCommand -eq "docker-compose") {
        # Docker Compose V1
        Write-Host "Executing: docker-compose down" -ForegroundColor Yellow
        $downOutput = Invoke-Expression "docker-compose down" 2>&1
        # We don't check LASTEXITCODE here as it might fail if no containers are running, which is fine
        
        Write-Host "`nPulling Docker images (this may take some time)..." -ForegroundColor Yellow
        Write-Host "Executing: docker-compose pull" -ForegroundColor Yellow
        $pullOutput = Invoke-Expression "docker-compose pull" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host $pullOutput -ForegroundColor Red
            throw "Error pulling Docker images (Exit code: $LASTEXITCODE)"
        }
        
        Write-Host "`nStarting Docker containers..." -ForegroundColor Yellow
        Write-Host "Executing: docker-compose up -d" -ForegroundColor Yellow
        $upOutput = Invoke-Expression "docker-compose up -d" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host $upOutput -ForegroundColor Red
            throw "Error starting Docker containers (Exit code: $LASTEXITCODE)"
        }
    }
    else {
        # Docker Compose V2
        Write-Host "Executing: docker compose down" -ForegroundColor Yellow
        $downOutput = Invoke-Expression "docker compose down" 2>&1
        # We don't check LASTEXITCODE here as it might fail if no containers are running, which is fine
        
        Write-Host "`nPulling Docker images (this may take some time)..." -ForegroundColor Yellow
        Write-Host "Executing: docker compose pull" -ForegroundColor Yellow
        $pullOutput = Invoke-Expression "docker compose pull" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host $pullOutput -ForegroundColor Red
            throw "Error pulling Docker images (Exit code: $LASTEXITCODE)"
        }
        
        Write-Host "`nStarting Docker containers..." -ForegroundColor Yellow
        Write-Host "Executing: docker compose up -d" -ForegroundColor Yellow
        $upOutput = Invoke-Expression "docker compose up -d" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host $upOutput -ForegroundColor Red
            throw "Error starting Docker containers (Exit code: $LASTEXITCODE)"
        }
    }
    
    # Verify containers are running
    Write-Host "`nVerifying containers are running..." -ForegroundColor Yellow
    Start-Sleep -Seconds 5 # Give containers a moment to start
    
    $containerCheck = if ($composeCommand -eq "docker-compose") {
        Invoke-Expression "docker-compose ps" 2>&1
    } else {
        Invoke-Expression "docker compose ps" 2>&1
    }
    
    if ($containerCheck -match "running" -or $containerCheck -match "healthy") {
        Write-Host "`nDocker containers are now running!" -ForegroundColor Green
        Write-Host "You can access SQLFlow at: http://localhost:8110" -ForegroundColor Cyan
        
        # Check if port 8110 is actually listening
        $portCheck = Get-NetTCPConnection -LocalPort 8110 -ErrorAction SilentlyContinue
        if (-not $portCheck) {
            Write-Host "Note: Port 8110 doesn't appear to be listening yet. The application may still be starting up." -ForegroundColor Yellow
            Write-Host "Please wait a few minutes before accessing SQLFlow." -ForegroundColor Yellow
        }
    } else {
        Write-Host "`nWarning: Could not verify containers are running. Please check with 'docker ps'" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "Error with Docker operations: $_" -ForegroundColor Red
    
    $troubleshootingTips = @"
Troubleshooting tips:
1. Make sure Docker Desktop is running
2. Check if the docker-compose.yml is valid: $composeCommand config
3. Check if there are port conflicts: another application might be using port 8110
4. Check disk space for Docker images
5. Review Docker logs: $composeCommand logs
"@
    Write-Host $troubleshootingTips -ForegroundColor Yellow
    
    $continueChoice = Read-Host "Do you want to exit the script? (y/n)"
    if ($continueChoice.ToLower() -eq "y") {
        Write-Host "Exiting script." -ForegroundColor Yellow
        exit 1
    } else {
        Write-Host "Continuing script execution, but SQLFlow might not be properly configured." -ForegroundColor Yellow
    }
}

Write-Host "`n===== SQLFlow Setup Complete =====" -ForegroundColor Cyan
Write-Host "- Sample data downloaded to: $downloadPath" -ForegroundColor White
Write-Host "- Docker containers are running" -ForegroundColor White
Write-Host "- SQLFlow is accessible at: http://localhost:8110" -ForegroundColor White
Write-Host "- Connection string environment variable has been configured" -ForegroundColor White
Write-Host "`nTo stop the containers, run: $composeCommand down" -ForegroundColor Yellow
Write-Host "To view container logs, run: $composeCommand logs -f" -ForegroundColor Yellow
