# SQLFlow Database Import Tool

A PowerShell tool for automated importing of databases from BAK and BACPAC files into SQLFlow sandbox environments. Compatible with both SQL Server and Azure SQL.

## Prerequisites

- Windows with PowerShell 5.1 or later
- `SQLFlow` environment variable containing connection string to your sandbox environment
- Internet connection (if SqlPackage needs to be installed for BACPAC imports)

## Quick Start

```powershell
# Import all BAK files into SQLFlow sandbox from a directory
.\Import-SqlDatabases.ps1 -BackupDirectory "C:\Backups\20250304" -ImportType "bak" -UseFilenameAsDBName

# Import specific BACPAC files into SQLFlow sandbox using filenames as database names
.\Import-SqlDatabases.ps1 -BackupDirectory "C:\Backups\20250304" -ImportType "bacpac" -UseFilenameAsDBName -Databases "dwv-ods-prod_20250304.bacpac", "dwv-pre-prod_20250304.bacpac"
```

## Interactive Mode

Run the script without parameters for an interactive guided experience:

```powershell
.\Import-SqlDatabases.ps1
```

This will:
1. Prompt for backup directory
2. Allow selection of import type (BAK or BACPAC)
3. Show available files in the directory
4. Let you choose which databases to import
5. Guide you through the import process

## Command Line Options

| Parameter | Description | Default |
|-----------|-------------|---------|
| `-BackupDirectory` | Directory containing backup files | (Will prompt if not provided) |
| `-ImportType` | Type of import to perform: `bak` or `bacpac` | `bak` |
| `-UseFilenameAsDBName` | Use filename as database name | `$false` |
| `-Databases` | Specific databases to import | (All files in directory) |

## Manual Import Methods for SQLFlow Sandbox

### Importing BAK Files Manually

1. Open SQL Server Management Studio (SSMS) or Azure Data Studio
2. Connect to your SQLFlow sandbox environment
3. Right-click on "Databases" in Object Explorer
4. Select "Restore Database..."
5. Choose "Device" as source and add your BAK file
6. Verify file locations and options
7. Click "OK" to start the restore process

### Importing BACPAC Files Manually

1. Open SQL Server Management Studio (SSMS) or Azure Data Studio
2. Connect to your SQLFlow sandbox environment
3. Right-click on "Databases" in Object Explorer
4. Select "Import Data-tier Application..."
5. Browse to your BACPAC file location
6. Set your target database name
7. Review settings and click "Next"
8. Click "Finish" to start import

## Troubleshooting

- Ensure the SQLFlow environment variable is properly set with correct credentials for your sandbox environment
- For BACPAC imports, make sure SqlPackage is installed (the script will attempt to install it if missing)
- Verify your account has sufficient permissions to read from the backup directory
- Check database logs in your SQLFlow sandbox for detailed error messages

## Security Note

This script requires authentication credentials stored in the `SQLFlow` environment variable. Ensure this variable is secured appropriately and not exposed in shared environments. After importing the databases create a SQL user with dbo access to these databaes. 
