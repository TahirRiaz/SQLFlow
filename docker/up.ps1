#requires -Version 5.1
<#
.SYNOPSIS
    Fetches the Sakila sample SQL (if missing) and starts the DeltaForge source databases in the background.

.PARAMETER Force
    Passed through to fetch-samples.ps1 to re-download the sample SQL.
#>
[CmdletBinding()]
param([switch]$Force)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& (Join-Path $root 'fetch-samples.ps1') -Force:$Force

Write-Host ''
Write-Host 'Starting containers (docker compose up -d)...'
docker compose --project-directory $root -f (Join-Path $root 'docker-compose.yml') up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed with exit code $LASTEXITCODE" }

Write-Host ''
Write-Host 'Waiting for health checks. Track progress with: docker compose -f docker/docker-compose.yml ps'
Write-Host 'Oracle takes the longest to become healthy on a first run (it builds FREEPDB1 and loads Sakila).'
Write-Host 'Connection strings are in docker/README.md.'
