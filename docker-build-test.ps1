#!/usr/bin/env pwsh
# Script to test only building the MSSQL DB image without pushing

# Repository configuration
$publicRepo = "businessiq/sqlflow"
$privateRepo = "businessiq/sqlflowprv"
$selectedRepo = ""

# Ask user which repository to use
function Select-Repository {
    Write-Host "`nSelect repository type to publish to:" -ForegroundColor Yellow
    Write-Host "1. Private repository ($privateRepo)" -ForegroundColor Cyan
    Write-Host "2. Public repository ($publicRepo)" -ForegroundColor Cyan
    $choice = Read-Host "Enter your choice (1 or 2)"
    switch ($choice) {
        "1" {
            Write-Host "Using private repository: $privateRepo" -ForegroundColor Green
            return $privateRepo
        }
        "2" {
            Write-Host "Using public repository: $publicRepo" -ForegroundColor Green
            return $publicRepo
        }
        default {
            Write-Host "Invalid choice. Defaulting to private repository." -ForegroundColor Yellow
            return $privateRepo
        }
    }
}

# Get the selected repository
$selectedRepo = Select-Repository
Write-Host "`nStarting Docker buildx build process for MSSQL image (build only, no push)..." -ForegroundColor Cyan

# Determine tag name
$dbTag = "$selectedRepo`:mssql"

# Build the DB image (MSSQL) without pushing
Write-Host "Building DB image (linux/amd64,linux/arm64)..." -ForegroundColor Green
docker buildx build `
    --builder mybuilder `
    --platform linux/amd64,linux/arm64 `
    --attest type=provenance,mode=max `
    --attest type=sbom `
    -t $dbTag `
    -f ./SQLFlowDb/Dockerfile `
    --build-arg BUILD_CONFIGURATION=Release `
    --load `
    .

# Check if the DB build was successful
if ($LASTEXITCODE -eq 0) {
    Write-Host "DB image built successfully as $dbTag!" -ForegroundColor Green
} else {
    Write-Host "DB image build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`nDB Docker image has been built successfully!" -ForegroundColor Cyan