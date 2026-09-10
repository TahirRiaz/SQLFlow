#requires -Version 5.1
<#
.SYNOPSIS
    Stops the DeltaForge source databases.

.PARAMETER Volumes
    Also remove the data volumes, so the next start re-seeds Sakila from scratch.
#>
[CmdletBinding()]
param([switch]$Volumes)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compose = Join-Path $root 'docker-compose.yml'

if ($Volumes) {
    docker compose --project-directory $root -f $compose down --volumes
} else {
    docker compose --project-directory $root -f $compose down
}
if ($LASTEXITCODE -ne 0) { throw "docker compose down failed with exit code $LASTEXITCODE" }
