# SQLFlow Installation and Configuration Guide

This guide will walk you through setting up SQLFlow using Docker containers.

## Prerequisites

- Docker Desktop installed and running
- SQL Server instance (on-premises) or Azure SQL Database
- Sufficient disk space for Docker images and volumes
- Basic knowledge of Docker and SQL database management

## Step 1: Prepare the Databases

### 1.1 Download Database Backups

1. Go to the GitHub release page for SQLFlow
2. Download the database backup package (~350 MB)
3. Extract the files to your local folder

### 1.2 Restore Database Backups

Restore databases using the provided PowerShell script from [SQLFlow\Sandbox\db](https://github.com/TahirRiaz/SQLFlow/tree/master/Sandbox/db):

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

### 1.4 Create and Configure Database User

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
2. Execute the SQL statements to configure your database connections

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

## Step 3: Download and Run Docker Images

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

### Persistent Volumes
The Docker setup creates these volumes:
- `sqlflow-keys`: Encryption keys
- `sqlflow-data`: Application data
- `sqlflow-sample-data`: Sample data (mounted from B:/SQLFlow)

## Step 4: Access SQLFlow

Once running successfully:

| Service | URL |
|---------|-----|
| SQLFlow UI (HTTPS) | https://localhost:8111 |
| SQLFlow UI (HTTP) | http://localhost:8110 |
| API (HTTPS) | https://localhost:9111 |
| API (HTTP) | http://localhost:9110 |

**Login credentials:** demo@sqlflow.io/demo

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