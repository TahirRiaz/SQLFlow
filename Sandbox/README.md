# SQLFlow Installation and Configuration Guide
This guide will walk you through setting up SQLFlow using Docker containers.

## Prerequisites

- [Docker Desktop](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/install-docker.md) installed and running
- [SQL Server](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/install-mssql.md) instance (on-premises) or Azure SQL Database
  - **Full-text Search** must be enabled on your SQL Server instance
- Sufficient disk space for Docker images and volumes (~550MB)
- Basic knowledge of Docker and SQL database management
- SSL Configuration:
  - Before starting SQLFlow setup, ensure your SQL Server or Azure SQL Database has SSL properly configured
  - Detailed instructions and an automation script are available in our [SQL Server SSL Configuration Wizard](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/db/mssql-ssl-wizard.md)
## Setup Options
### Option 1: Automated Installation
Use the PowerShell auto-installer script:
```powershell
.\SQLFlowSetup.ps1
```
[View Script Source](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/SQLFlowSetup.ps1)

### Option 2: Manual Installation
Follow the step-by-step instructions in our [Manual Setup Guide](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/ManualSetup.md)

