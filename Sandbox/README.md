# SQLFlow Installation Guide

This guide explains how to quickly set up SQLFlow using Docker containers.

## Prerequisites

- [Docker Desktop](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/install-docker.md) installed and running
- Sufficient disk space for Docker images and volumes (~650MB)

## Automated Installation
Our new fully automated PowerShell script handles the entire setup process in just 5 minutes:

```powershell
.\setup.ps1
```

### What the script does:
- Downloads required backup files from GitHub
- Sets up environment variables and cleans previous installations
- Pulls Docker images and configures paths
- Restores databases and updates connection strings
- Starts all containers with proper configuration

The script only requires Docker Desktop to be running - everything else is handled automatically.

[Download Setup Script](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/setup.ps1)

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