# SQLFlow Installation Guide

This guide explains how to quickly set up SQLFlow using Docker containers.

## Prerequisites

- [Docker Desktop](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/install-docker.md) installed and running
- Sufficient disk space for Docker images and volumes (~650MB)

## Automated Installation

### For Windows Users
[Download Setup Script](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/setup.ps1) 
PowerShell script to handle the entire setup process in just 5 minutes:

```powershell
.\setup.ps1
```

### For Mac Users
[Download Setup Script](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/setup.sh)
Shell script to handle the entire setup process in just 5 minutes:

```bash
chmod +x setup.sh
./setup.sh
```

### What the scripts do:
- Download required backup files from GitHub
- Set up environment variables and clean previous installations
- Pull Docker images and configure paths
- Restore databases and update connection strings
- Start all containers with proper configuration

The scripts only require Docker Desktop to be running - everything else is handled automatically.
[Manual setup guide with mssql](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/setup-guide.md)

## Access SQLFlow

Once installation completes, access SQLFlow via:

| Service | URL |
|---------|-----|
| SQLFlow UI | http://localhost:8110 |
| SQLFlow UI (HTTPS) | https://localhost:8111 |
| API | http://localhost:9110 |

**Login credentials:** demo@sqlflow.io/@Demo123

## Manual Installation

If you prefer a manual approach, follow our [Manual Setup Guide](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/ManualSetup.md)

## Support

If you encounter any issues during installation, please [open an issue](https://github.com/TahirRiaz/SQLFlow/issues) on our GitHub repository.