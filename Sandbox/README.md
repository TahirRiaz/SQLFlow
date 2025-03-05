# SQLFlow Installation and Configuration Guide

This comprehensive guide will walk you through setting up SQLFlow using Docker containers. Follow these steps carefully to ensure proper installation and configuration of your sandbox environment.

## Prerequisites

- Docker Desktop installed and running
- SQL Server instance (on-premises) or Azure SQL Database accessible from your Docker host
- Sufficient disk space for Docker images and volumes
- Basic knowledge of Docker and SQL database management

## Step 1: Prepare the Databases

Before starting the Docker containers, you must set up the required databases:

### 1.1 Restore Database Backups

Restore the following database backup files to your SQL Server or Azure SQL Database:

- `dw-sqlflow-prod` (Primary SQLFlow database)
- `dw-pre-prod` (Sandbox environment database)
- `dw-ods-prod` (Sandbox environment database)
- `WideWorldImporters` (Sample database)

### 1.2 Enable Full-Text Indexing

**Important**: Ensure full-text indexing is enabled for all databases. SQLFlow relies on full-text search capabilities for optimal performance.

### 1.3 Create and Configure Database User

Create a dedicated database user for SQLFlow with appropriate permissions:

```sql
-- Create SQLFlow user (SQL Server syntax - adjust for Azure SQL DB if needed)
CREATE LOGIN SQLFlowUser WITH PASSWORD = 'YourStrongPassword!';

-- Configure access for the main SQLFlow database
USE [dw-sqlflow-prod];
CREATE USER SQLFlowUser FOR LOGIN SQLFlowUser;
EXEC sp_addrolemember 'db_owner', 'SQLFlowUser';

-- Configure access for sandbox environment databases
USE [dw-pre-prod];
CREATE USER SQLFlowUser FOR LOGIN SQLFlowUser;
EXEC sp_addrolemember 'db_owner', 'SQLFlowUser';

USE [dw-ods-prod];
CREATE USER SQLFlowUser FOR LOGIN SQLFlowUser;
EXEC sp_addrolemember 'db_owner', 'SQLFlowUser';

USE [WideWorldImporters];
CREATE USER SQLFlowUser FOR LOGIN SQLFlowUser;
EXEC sp_addrolemember 'db_owner', 'SQLFlowUser';
```

### 1.4 Run Authentication Script

```sql
-- Execute the set-authentication.sql script in your database
-- This script updates connection strings for the sandbox environment
```

## Step 2: Configure Environment Variables

Create a `.env` file in the same directory as your `docker-compose.yml` file:

```
# Required environment variables - Adjust server connection string as needed
# For local SQL Server:
SQLFlowConStr=Server=host.docker.internal;Database=dw-sqlflow-prod;User Id=SQLFlowUser;Password=YourStrongPassword!;TrustServerCertificate=True;

# For Azure SQL Database:
# SQLFlowConStr=Server=your-server.database.windows.net;Database=dw-sqlflow-prod;User Id=SQLFlowUser;Password=YourStrongPassword!;

SQLFlowOpenAiApiKey=your-openai-api-key
```

**Notes:**
- Replace connection string parameters with your actual database details
- Use `host.docker.internal` to reference your host machine's SQL Server from Docker
- Provide a valid OpenAI API key for the AI functionality

## Step 3: Download and Run Docker Images

1. Save the Docker Compose configuration to a file named `docker-compose.yml`

2. Open a terminal/command prompt and navigate to the directory containing your `docker-compose.yml` file

3. Pull the required Docker images:
   ```
   docker-compose pull
   ```

4. Start the services:
   ```
   docker-compose up -d
   ```

5. Verify the containers are running:
   ```
   docker-compose ps
   ```

## Step 4: Access SQLFlow

Once the containers are running successfully:

- Access the SQLFlow UI at:
  - HTTPS: https://localhost:8111
  - HTTP: http://localhost:8110
- The API endpoints are available at:
  - HTTPS: https://localhost:9111
  - HTTP: http://localhost:9110

## Volume Information

The Docker setup creates the following persistent volumes:
- `sqlflow-keys`: Stores encryption keys
- `sqlflow-data`: Stores application data
- `sqlflow-sample-data`: Maps to local sample data (mounted from B:/SQLFlow)

## Troubleshooting

| Issue | Resolution |
|-------|------------|
| **Database Connection Issues** | Ensure your database allows remote connections and any firewalls permit access from Docker |
| **Full-Text Search Issues** | Verify full-text indexing is properly configured in all databases |
| **Certificate Errors** | For development, the system generates self-signed certificates. Accept browser warnings or provide your own certificates |
| **Memory Issues** | Both containers are configured with 2GB memory limits. Adjust as needed in the compose file |
| **Container Startup Failures** | Check logs with `docker-compose logs sqlflowui` or `docker-compose logs sqlflowapi` |

## Stopping the Services

To stop the services:
```
docker-compose down
```

To completely remove containers, networks, and volumes:
```
docker-compose down -v
```

## Security Best Practices

- Change the default certificate password (`ChangeThisInProduction!`) in production environments
- For production, set `ASPNETCORE_ENVIRONMENT=Production` instead of Development
- Rotate database passwords periodically
- Use a dedicated API key with appropriate permissions for the OpenAI integration
- Consider implementing network segmentation for production deployments

## Maintenance

Regular maintenance tasks include:
- Backing up SQLFlow databases
- Monitoring Docker container health
- Updating Docker images when new versions are released
- Rotating security credentials

For additional support, please contact your SQLFlow administrator or refer to the official documentation.