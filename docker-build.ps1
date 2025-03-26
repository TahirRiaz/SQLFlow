#!/usr/bin/env pwsh

# Script to build and push Docker images for SQLFlow API, UI, and DB
# PowerShell script for Docker buildx multi-platform builds

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

Write-Host "`nStarting Docker buildx build process..." -ForegroundColor Cyan

# Determine tag names
$apiTag = "$selectedRepo`:api"
$uiTag = "$selectedRepo`:ui"
$dbTag = "$selectedRepo`:mssql"

# Build and push the API image (multi-platform: linux/amd64, linux/arm64)
Write-Host "Building API image (linux/amd64)..." -ForegroundColor Green
docker buildx build `
    --builder mybuilder `
    --platform linux/amd64,linux/arm64 `
    --attest type=provenance,mode=max `
    --attest type=sbom `
    -t $apiTag `
    -f ./SQLFlowApi/Dockerfile `
    --build-arg BUILD_CONFIGURATION=Release `
    --push `
    .

# Check if the API build was successful
if ($LASTEXITCODE -eq 0) {
    Write-Host "API image built and pushed successfully to $apiTag!" -ForegroundColor Green
} else {
    Write-Host "API image build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Build and push the UI image (single platform: linux/amd64)
Write-Host "Building UI image (linux/amd64)..." -ForegroundColor Green
docker buildx build `
    --builder mybuilder `
    --platform linux/amd64,linux/arm64 `
    --attest type=provenance,mode=max `
    --attest type=sbom `
    -t $uiTag `
    -f ./SQLFlowUi/Dockerfile `
    --build-arg BUILD_CONFIGURATION=Release `
    --push `
    .

# Check if the UI build was successful
if ($LASTEXITCODE -eq 0) {
    Write-Host "UI image built and pushed successfully to $uiTag!" -ForegroundColor Green
} else {
    Write-Host "UI image build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Build and push the DB image (MSSQL)
Write-Host "Building DB image (linux/amd64)..." -ForegroundColor Green
docker buildx build `
    --builder mybuilder `
    --platform linux/amd64,linux/arm64 `
    --attest type=provenance,mode=max `
    --attest type=sbom `
    -t $dbTag `
    -f ./SQLFlowDb/Dockerfile `
    --build-arg BUILD_CONFIGURATION=Release `
    --push `
    .

# Check if the DB build was successful
if ($LASTEXITCODE -eq 0) {
    Write-Host "DB image built and pushed successfully to $dbTag!" -ForegroundColor Green
} else {
    Write-Host "DB image build failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`nAll Docker images have been built and pushed successfully to $selectedRepo!" -ForegroundColor Cyan