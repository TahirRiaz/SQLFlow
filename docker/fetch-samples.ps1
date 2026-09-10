#requires -Version 5.1
<#
.SYNOPSIS
    Downloads the Sakila sample database SQL (schema + data) for PostgreSQL, MySQL, and Oracle into the
    per-engine initdb directories, where docker-compose mounts them as first-boot initialization scripts.

.DESCRIPTION
    The scripts come from the jOOQ/sakila mirror, which publishes the same Sakila schema for every engine, so
    the three source databases end up with identical tables (actor, film, customer, ...). Existing files are
    left in place unless -Force is given. The Oracle scripts are prefixed with a CURRENT_SCHEMA directive so
    they load into the dedicated SAKILA schema created by 00_create_sakila_schema.sql rather than SYSTEM.

.PARAMETER Force
    Re-download and overwrite files that already exist.

.PARAMETER Ref
    The git ref (branch, tag, or commit SHA) of jOOQ/sakila to pull from. Defaults to 'main'.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Ref = 'main'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$base = "https://raw.githubusercontent.com/jOOQ/sakila/$Ref"

# MySQL and PostgreSQL run their scripts directly, in filename order. The Oracle scripts are pulled in by the
# committed wrapper 10_load_sakila.sql (which handles SET DEFINE OFF and schema targeting), so they land in a
# sql/ subfolder the image does not auto-execute and are fetched verbatim.
$downloads = @(
    @{ Url = "$base/mysql-sakila-db/mysql-sakila-schema.sql";        Path = "mysql/initdb/01_sakila_schema.sql" },
    @{ Url = "$base/mysql-sakila-db/mysql-sakila-insert-data.sql";   Path = "mysql/initdb/02_sakila_data.sql" },
    @{ Url = "$base/postgres-sakila-db/postgres-sakila-schema.sql";  Path = "postgres/initdb/01_sakila_schema.sql" },
    @{ Url = "$base/postgres-sakila-db/postgres-sakila-insert-data.sql"; Path = "postgres/initdb/02_sakila_data.sql" },
    @{ Url = "$base/oracle-sakila-db/oracle-sakila-schema.sql";      Path = "oracle/sakila-sql/oracle-sakila-schema.sql" },
    @{ Url = "$base/oracle-sakila-db/oracle-sakila-insert-data.sql"; Path = "oracle/sakila-sql/oracle-sakila-data.sql" }
)

foreach ($item in $downloads) {
    $target = Join-Path $root $item.Path
    if ((Test-Path $target) -and -not $Force) {
        Write-Host "skip   $($item.Path) (already present; use -Force to refresh)"
        continue
    }

    $dir = Split-Path $target -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    Write-Host "fetch  $($item.Path)  <-  $($item.Url)"
    $content = (Invoke-WebRequest -Uri $item.Url -UseBasicParsing).Content
    [System.IO.File]::WriteAllText($target, $content)
}

Write-Host ''
Write-Host 'Sample SQL is ready under docker/*/initdb. Start the databases with docker/up.ps1.'
