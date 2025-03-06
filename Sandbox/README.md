# SQLFlow Installation and Configuration Guide

This guide will walk you through setting up SQLFlow using Docker containers.

## Prerequisites

- Docker Desktop installed and running
- SQL Server instance (on-premises) or Azure SQL Database
- Sufficient disk space for Docker images and volumes
- Basic knowledge of Docker and SQL database management
- Before starting SQLFlow setup, ensure your SQL Server or Azure SQL Database has SSL properly configured. Detailed instructions and an automation script are available at:
[SQL Server SSL Configuration Wizard](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/db/mssql-ssl-wizard.md)

## Step 1: Prepare the Databases

### 1.1 Download Database Backups

1. Download the database backup package (~350 MB) in your preferred format:
   - [SandboxDb BACPAC format](https://github.com/TahirRiaz/SQLFlow/releases/download/SQLFlow_Sandbox_V1/SandboxDb_BACPAC_Files_20250305.zip)
   - [SandboxDb BAK format](https://github.com/TahirRiaz/SQLFlow/releases/download/SQLFlow_Sandbox_V1/SandboxDb_BAK_Files_20250305.zip)

2. Extract the files to a folder of your choice on your local system.

3. Create an environment variable named "SQLFlow" containing the connection string to your target server. This variable will be used for the automated database restoration process. This is an optional step if you want to restore the databases manually
Example:
```
Server=localhost;Initial Catalog=master;User ID=;Password=;Persist Security Info=False;TrustServerCertificate=True;Encrypt=False;Command Timeout=660;
```

### 1.2 Restore Database Backups
Restore the databases using the provided PowerShell script located in the [SQLFlow\Sandbox\db](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/db/db-import.ps1) repository. Alternatively, you may perform a manual restoration if preferred.


```powershell
# Set environment variable before running
# $env:SQLFlow = "Connection string to target server"

# Run the database restoration script
.\db-import.ps1
```

This script will restore these databases:
- `dw-sqlflow-prod` (Primary SQLFlow database)
- `dw-pre-prod` (Sandbox environment)
- `dw-ods-prod` (Sandbox environment)
- `WideWorldImporters` (Sample database)

### 1.3 Enable Full-Text Indexing

**Important**: SQLFlow requires full-text search capabilities.

Verify full-text is enabled:

```sql
SELECT SERVERPROPERTY('IsFullTextInstalled') AS [IsFullTextInstalled];
```

### 1.4 Create and Configure Database User For Pipelines

```sql
-- Create SQLFlow user
CREATE LOGIN SQLFlowUser WITH PASSWORD = 'YourStrongPassword!';

-- Configure access for all required databases
USE [dw-sqlflow-prod];
CREATE USER SQLFlowUser FOR LOGIN SQLFlowUser;
EXEC sp_addrolemember 'db_owner', 'SQLFlowUser';

-- Repeat for other databases: dw-pre-prod, dw-ods-prod, WideWorldImporters
```

### 1.5 Database Connectivity for Data Pipelines
The following script updates connection strings required for data pipeline execution in the sandbox environment. In production environments, these connection strings are securely managed through Azure KeyVault.

**Instructions:**
1. Replace the empty User ID and Password fields with your credentials
2. Execute the SQL statements agasting [dw-sqlflow-prod] these connection strings will be used by pipeline executions.

```sql
UPDATE [flw].[SysDataSource]
SET ConnectionString = 'Server=host.docker.internal;Initial Catalog=dw-ods-prod;User ID=;Password=;Persist Security Info=False;TrustServerCertificate=True;Encrypt=False;Command Timeout=360;'
WHERE Alias = 'dw-ods-prod-db';

UPDATE [flw].[SysDataSource]
SET ConnectionString = 'Server=host.docker.internal;Initial Catalog=dw-pre-prod;User ID=;Password=;Persist Security Info=False;TrustServerCertificate=True;Encrypt=False;Command Timeout=360;'
WHERE Alias = 'dw-pre-prod-db';

UPDATE [flw].[SysDataSource]
SET ConnectionString = 'Server=host.docker.internal;Initial Catalog=WideWorldImporters;User ID=;Password=;Persist Security Info=False;TrustServerCertificate=True;Encrypt=False;Command Timeout=360;'
WHERE Alias = 'wwi-db';
```

## Step 2: Configure Environment Variables

### Required Variables

**For local SQL Server:**
```
SQLFlowConStr=Server=host.docker.internal;Database=dw-sqlflow-prod;User Id=SQLFlowUser;Password=YourStrongPassword!;TrustServerCertificate=True;
```

**For Azure SQL Database:**
```
SQLFlowConStr=Server=your-server.database.windows.net;Database=dw-sqlflow-prod;User Id=SQLFlowUser;Password=YourStrongPassword!;
```

**OpenAI integration:**
```
SQLFlowOpenAiApiKey=your-openai-api-key
```

> **Note:** Use `host.docker.internal` to reference your host machine's SQL DB from Docker

## Step 3: Download and Configure Sample Data

Before continuing with the Docker setup, you need to download and place the sample data in the correct location:

1. Download the sample data package from:
   [SQLFlow Sample Data](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/data/SampleData.zip)

2. Create a directory at `C:\SQLFlow` if it doesn't already exist.

3. Extract the contents of `SampleData.zip` directly into the `C:\SQLFlow` directory.
   - The extraction should result in various folders and files directly under `C:\SQLFlow`
   - Do not create a nested folder structure (avoid having `C:\SQLFlow\SampleData\`)

4. Verify the sample data is correctly placed by checking that the directory structure matches what the Docker volume expects to mount.

> **Note:** This sample data is required for the SQLFlow Docker container to function properly, as it will be mounted as a volume during container startup. The Docker Compose configuration expects to find this data at the specified location.

## Step 4: Download and Run Docker Images

1. Save the Docker Compose configuration to `docker-compose.yml`

2. Pull the required Docker images:
   ```bash
   docker-compose pull
   ```

3. Start the services:
   ```bash
   docker-compose up -d
   ```

4. Verify the containers are running:
   ```bash
   docker-compose ps
   ```

4. Docker images are updated regularly. Always use the latest database and image version. Check the repository for recent changes.
To force an update, run:
   ```bash
   docker-compose pull
   docker-compose down
   docker-compose up -d
   ```

### Persistent Volumes
The Docker setup creates these volumes:
- `sqlflow-keys`: Encryption keys
- `sqlflow-data`: Application data
- `sqlflow-sample-data`: Sample data (mounted from c:/SQLFlow)

## Step 5: Access SQLFlow

Once running successfully:

| Service | URL |
|---------|-----|
| SQLFlow UI (HTTPS) | https://localhost:8111 |
| SQLFlow UI (HTTP) | http://localhost:8110 |
| API (HTTPS) | https://localhost:9111 |
| API (HTTP) | http://localhost:9110 |

**Login credentials:** demo@sqlflow.io/@Demo123

## Troubleshooting

| Issue | Resolution |
|-------|------------|
| **Database Connection** | Ensure your database allows remote connections and check firewall settings |
| **Full-Text Search** | Verify full-text indexing is properly configured |
| **Certificate Errors** | Accept browser warnings for self-signed certificates or provide your own |
| **Memory Issues** | Both containers have 2GB memory limits; adjust as needed |
| **Container Failures** | Check logs with `docker-compose logs sqlflowui` or `docker-compose logs sqlflowapi` |

## Stopping the Services

```bash
# Stop services
docker-compose down

# Remove containers, networks, and volumes
docker-compose down -v
```

## Security Best Practices

- Change default certificate password (`ChangeThisInProduction!`)
- Set `ASPNETCORE_ENVIRONMENT=Production` in production
- Rotate database passwords periodically
- Use a dedicated API key for OpenAI integration
- Implement network segmentation for production

## Maintenance

Regular tasks:
- Back up SQLFlow databases
- Monitor Docker container health
- Update Docker images when new versions are released
- Rotate security credentials

For additional support, contact your SQLFlow administrator or refer to the official documentation.