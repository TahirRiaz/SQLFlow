# SQL Flow UI Dockerfile
# Multi-stage build for optimized container size and security

# Base stage - runtime environment
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
WORKDIR /app

# Install required packages for runtime, diagnostics, and networking
RUN apk add --no-cache icu-libs curl tzdata iputils bind-tools \
    openssl bash jq procps net-tools htop ca-certificates \
    mtr tcpdump tini fontconfig libstdc++

# Environment Variables with defaults (can be overridden at runtime)
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8 \
    LANG=en_US.UTF-8 \
    # Connection settings
    CONNECTION_TIMEOUT=30 \
    CONNECTION_RETRIES=3 \
    DNS_CACHE_TTL=60 \
    # SignalR configuration (for UI)
    SIGNALR_ENABLED="true" \
    SIGNALR_TRANSPORT_TYPE="WebSockets,ServerSentEvents,LongPolling" \
    SIGNALR_MAX_MESSAGE_SIZE=20971520 \
    # Networking configuration with defaults
    HTTP_PROXY="" \
    HTTPS_PROXY="" \
    NO_PROXY="localhost,127.0.0.1" \
    # TLS/SSL Configuration
    CERT_PASSWORD="" \
    CERT_SOURCE="generate" \
    USE_HTTPS="true" \
    # Application settings
    ASPNETCORE_ENVIRONMENT="Production" \
    # Logging
    LOG_LEVEL="Information" \
    # API Configuration - connect to API service
    API_BASE_URL="https://sqlflow.api:9111" \
    # Cloud provider detection - can be overridden
    CLOUD_PROVIDER="auto-detect"

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Move dotnet version check to build stage where SDK is available
RUN dotnet --version && \
    dotnet --info

# Install build dependencies
RUN apk add --no-cache git

# Copy and restore project dependencies
COPY ["SQLFlowUi/SQLFlowUi.csproj", "SQLFlowUi/"]
COPY ["SQLFlowCore/SQLFlowCore.csproj", "SQLFlowCore/"]
RUN dotnet restore "./SQLFlowUi/SQLFlowUi.csproj"

# Copy the rest of the source code
COPY . .

# Build the application
WORKDIR "/src/SQLFlowUi"
ARG BUILD_CONFIGURATION=Release
RUN dotnet build "./SQLFlowUi.csproj" -c ${BUILD_CONFIGURATION} -o /app/build

# Publish stage with SBOM generation
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# Publish without SBOM generation from .NET
RUN dotnet publish "./SQLFlowUi.csproj" -c ${BUILD_CONFIGURATION} -o /app/publish /p:UseAppHost=false /p:Version=1.0.0

# Final stage
FROM base AS final
WORKDIR /app

# Create a non-root user to run the application
RUN addgroup -g 1000 appgroup && \
    adduser -u 1000 -G appgroup -s /bin/sh -D appuser && \
    mkdir -p /app/wwwroot /app/data /https /tmp/diagnostics /app/scripts && \
    chown -R appuser:appgroup /app /https /tmp/diagnostics

# Copy the application from publish stage
COPY --from=publish /app/publish .

# Copy scripts - using wildcard to copy all scripts and setting ownership
COPY --chown=appuser:appgroup ./Scripts/ui/entrypoint.sh /app/scripts/
COPY --chown=appuser:appgroup ./Scripts/ui/healthcheck.sh /app/scripts/
COPY --chown=appuser:appgroup ./Scripts/ui/network-diagnostics.sh /app/scripts/
COPY --chown=appuser:appgroup ./Scripts/ui/setup-certificates.sh /app/scripts/

# Make scripts executable with error handling
RUN chmod +x /app/scripts/*.sh || echo "Warning: No scripts found in /app/scripts/"

# Copy entrypoint and healthcheck scripts separately to ensure they exist
COPY --chown=appuser:appgroup ./Scripts/ui/entrypoint.sh /app/
COPY --chown=appuser:appgroup ./Scripts/ui/healthcheck.sh /app/
RUN chmod +x /app/entrypoint.sh /app/healthcheck.sh

# Default port configuration (override with -e or docker compose)
ENV ASPNETCORE_URLS=http://+:8110;https://+:8111

# Set up a default healthcheck (can be overridden in docker-compose)
HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 \
    CMD ["/app/healthcheck.sh"]

# Switch to non-root user
USER appuser

# Use tini as init to handle signals properly
ENTRYPOINT ["/sbin/tini", "--", "/app/entrypoint.sh"]