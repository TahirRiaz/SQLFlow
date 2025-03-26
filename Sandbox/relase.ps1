# PowerShell script to create both .bak and .bacpac files for specified databases and package the entire Sandbox environment

# Set variables
$backupBaseDirectory = "B:\Github\SQLFlow\Sandbox\mssql\bak"
$releaseDirectory = "B:\Github\SQLFlow\bin\Release"
$sandboxDirectory = "B:\Github\SQLFlow\Sandbox"
$currentDate = Get-Date -Format "yyyyMMdd"
$databases = @("dw-ods-prod", "dw-sqlflow-prod", "dw-pre-prod", "WideWorldImporters")
$zipFileName = "SQLFlow_Sandbox_Release_$currentDate.zip"
$zipFilePath = Join-Path -Path $releaseDirectory -ChildPath $zipFileName

# Get connection string from environment variable
$connectionString = $env:SQLFlowConStr

# Parse connection string to get server, user, and password
if ($connectionString -match "Server=(.*?);.*?User ID=(.*?);Password=(.*?);") {
    $serverName = $matches[1]
    $userId = $matches[2]
    $password = $matches[3]
} else {
    Write-Host "Error: Unable to parse SQLFlow environment variable." -ForegroundColor Red
    exit 1
}

# Use the base directory directly instead of creating a date-based subfolder
$backupDirectory = $backupBaseDirectory

# Create backup directory if it doesn't exist
if (-not (Test-Path $backupDirectory)) {
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    Write-Host "Created backup directory: $backupDirectory" -ForegroundColor Green
}

# Delete existing backup files to clean up before creating new ones
Write-Host "Deleting existing backup files from $backupDirectory..." -ForegroundColor Yellow
Get-ChildItem -Path $backupDirectory -Filter "*.bak" | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $backupDirectory -Filter "*.bacpac" | Remove-Item -Force -ErrorAction SilentlyContinue
Write-Host "Finished deleting existing backup files" -ForegroundColor Green

# Create release directory if it doesn't exist
if (-not (Test-Path $releaseDirectory)) {
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    Write-Host "Created release directory: $releaseDirectory" -ForegroundColor Green
}

# Function to shrink database files
function Shrink-Database {
    param (
        [string]$DatabaseName
    )
    
    Write-Host "Shrinking database $DatabaseName to remove unused space..." -ForegroundColor Yellow
    
    try {
        # First run DBCC SHRINKDATABASE with reasonable target percent
        $shrinkQuery = @"
DBCC SHRINKDATABASE (N'$DatabaseName', 10)
"@
        Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $shrinkQuery -ErrorAction Stop
        
        # Get logical file names for data files
        $fileQuery = @"
SELECT name FROM [$DatabaseName].sys.database_files WHERE type = 0
"@
        $dataFiles = Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $fileQuery -ErrorAction Stop
        
        # Shrink each data file specifically
        foreach ($file in $dataFiles) {
            $fileName = $file.name
            $fileQuery = @"
DBCC SHRINKFILE (N'$fileName', 10)
"@
            Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $fileQuery -ErrorAction SilentlyContinue
        }
        
        Write-Host "Successfully shrunk database $DatabaseName" -ForegroundColor Green
        return $true
    } catch {
        Write-Host ("Error shrinking database {0}: {1}" -f $DatabaseName, $_) -ForegroundColor Red
        return $false
    }
}

# Function to create a single zip archive of the entire Sandbox environment
function Create-SandboxReleaseArchive {
    Write-Host "Creating SQLFlow Sandbox release archive..." -ForegroundColor Yellow
    
    try {
        # Ensure the release directory exists
        if (-not (Test-Path $releaseDirectory)) {
            New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
            Write-Host "Created release directory: $releaseDirectory" -ForegroundColor Green
        }
        
        # Verify the sandbox directory exists
        if (-not (Test-Path $sandboxDirectory)) {
            Write-Host "Error: Sandbox directory not found at $sandboxDirectory" -ForegroundColor Red
            return $false
        }
        
        # Count files to get a sense of the package size
        $fileCount = (Get-ChildItem -Path $sandboxDirectory -File -Recurse -ErrorAction SilentlyContinue).Count
        Write-Host "Found $fileCount files in the Sandbox environment to package" -ForegroundColor White
        
        # Create the zip file directly from the Sandbox directory
        Write-Host "Creating SQLFlow Sandbox release package: $zipFilePath" -ForegroundColor Yellow
        Write-Host "This may take some time depending on the size of the Sandbox environment..." -ForegroundColor Yellow
        
        # Use -Force to overwrite any existing zip file
        Compress-Archive -Path "$sandboxDirectory\*" -DestinationPath $zipFilePath -Force
        
        if (Test-Path $zipFilePath) {
            $zipSize = (Get-Item $zipFilePath).Length / 1MB
            Write-Host "Successfully created SQLFlow Sandbox release package: $zipFilePath (Size: $($zipSize.ToString('0.00')) MB)" -ForegroundColor Green
            return $true
        } else {
            Write-Host "Failed to create the release package" -ForegroundColor Red
            return $false
        }
    } catch {
        Write-Host "Error creating SQLFlow Sandbox release package: $_" -ForegroundColor Red
        return $false
    }
}

# Function to create .bak backup
function Create-DatabaseBackup {
    param (
        [string]$DatabaseName
    )
    
    $backupFile = "$backupDirectory\$DatabaseName`_$currentDate.bak"
    
    Write-Host "Creating compressed .bak backup for $DatabaseName..." -ForegroundColor Yellow
    
    $query = @"
BACKUP DATABASE [$DatabaseName] 
TO DISK = N'$backupFile' 
WITH COMPRESSION, 
     INIT, 
     NAME = N'$DatabaseName-Full Database Backup', 
     DESCRIPTION = N'Full backup of $DatabaseName database'
"@
    
    try {
        Invoke-Sqlcmd -ServerInstance $serverName -Database "master" -Username $userId -Password $password -Query $query -ErrorAction Stop
        Write-Host "Backup of $DatabaseName to $backupFile completed successfully." -ForegroundColor Green
        return $true
    } catch {
        Write-Host ("Error creating backup for {0}: {1}" -f $DatabaseName, $_) -ForegroundColor Red
        return $false
    }
}

# Function to check and install SqlPackage if needed
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

# Function to export as BACPAC
function Export-DatabaseAsBacpac {
    param (
        [string]$DatabaseName,
        [string]$SqlPackagePath
    )
    
    $bacpacFile = "$backupDirectory\$DatabaseName`_$currentDate.bacpac"
    
    Write-Host "Exporting $DatabaseName as .bacpac..." -ForegroundColor Yellow
    
    try {
        # Use only the most basic parameters
        & $SqlPackagePath /Action:Export /SourceServerName:$serverName /SourceDatabaseName:$DatabaseName /TargetFile:$bacpacFile /SourceUser:$userId /SourcePassword:$password /p:VerifyExtraction=False
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Export of $DatabaseName to $bacpacFile completed successfully." -ForegroundColor Green
            return $true
        } else {
            Write-Host "Export of $DatabaseName failed with exit code $LASTEXITCODE." -ForegroundColor Red
            return $false
        }
    } catch {
        Write-Host "Error exporting $DatabaseName as BACPAC: $_" -ForegroundColor Red
        return $false
    }
}

# Check for required modules
if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    Write-Host "SqlServer module not found. Installing..." -ForegroundColor Yellow
    Install-Module -Name SqlServer -Force -AllowClobber -Scope CurrentUser
}

# Import SqlServer module
Import-Module SqlServer

# Ensure SqlPackage is installed
$sqlPackagePath = Ensure-SqlPackageInstalled
if (-not $sqlPackagePath) {
    Write-Host "Warning: Could not install or find SqlPackage. BACPAC exports will be skipped." -ForegroundColor Red
    $skipBacpac = $true
} else {
    $skipBacpac = $false
}

# Track success and failure counts
$shrinkSuccess = 0
$shrinkFailure = 0
$bakSuccess = 0
$bakFailure = 0
$bacpacSuccess = 0
$bacpacFailure = 0

# Process each database
foreach ($database in $databases) {
    # Shrink database first
    $shrinkResult = Shrink-Database -DatabaseName $database
    if ($shrinkResult) { $shrinkSuccess++ } else { $shrinkFailure++ }

    # Create .bak backup
    $bakResult = Create-DatabaseBackup -DatabaseName $database
    if ($bakResult) { $bakSuccess++ } else { $bakFailure++ }
    
    # Export as .bacpac if SqlPackage is available
    if (-not $skipBacpac) {
        $bacpacResult = Export-DatabaseAsBacpac -DatabaseName $database -SqlPackagePath $sqlPackagePath
        if ($bacpacResult) { $bacpacSuccess++ } else { $bacpacFailure++ }
    }
    
    Write-Host "-------------------------------------------" -ForegroundColor Gray
}

# Create the SQLFlow Sandbox release package
$zipResult = Create-SandboxReleaseArchive
if (-not $zipResult) {
    Write-Host "Warning: Failed to create SQLFlow Sandbox release package." -ForegroundColor Yellow
}

# Display summary
Write-Host "`nBackup Summary:" -ForegroundColor Cyan
Write-Host "Databases processed: $($databases.Count)" -ForegroundColor White
Write-Host "Database shrink successful: $shrinkSuccess" -ForegroundColor $(if ($shrinkSuccess -eq $databases.Count) { "Green" } else { "Yellow" })
Write-Host "Database shrink failed: $shrinkFailure" -ForegroundColor $(if ($shrinkFailure -gt 0) { "Red" } else { "Green" })
Write-Host "BAK files created successfully: $bakSuccess" -ForegroundColor $(if ($bakSuccess -eq $databases.Count) { "Green" } else { "Yellow" })
Write-Host "BAK files failed: $bakFailure" -ForegroundColor $(if ($bakFailure -gt 0) { "Red" } else { "Green" })

if (-not $skipBacpac) {
    Write-Host "BACPAC files created successfully: $bacpacSuccess" -ForegroundColor $(if ($bacpacSuccess -eq $databases.Count) { "Green" } else { "Yellow" })
    Write-Host "BACPAC files failed: $bacpacFailure" -ForegroundColor $(if ($bacpacFailure -gt 0) { "Red" } else { "Green" })
} else {
    Write-Host "BACPAC export skipped: SqlPackage utility not available" -ForegroundColor Yellow
}

Write-Host "Backup location: $backupDirectory" -ForegroundColor White
Write-Host "SQLFlow Sandbox release package: $zipFilePath" -ForegroundColor White
Write-Host "`nThe release package contains the complete SQLFlow Sandbox environment ready for deployment." -ForegroundColor Cyan