# SQL Flow API Dockerfile

# Base stage for runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
WORKDIR /app

# Install essential packages and clean cache in single layer
RUN apk add --no-cache icu-libs curl tzdata iputils bind-tools \
    openssl bash jq ca-certificates tini && \
    mkdir -p /app/wwwroot /app/data /https /tmp/diagnostics

# Environment Variables - grouped by function
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8 \
    LANG=en_US.UTF-8 \
    # App settings
    ASPNETCORE_ENVIRONMENT="Production" \
    ASPNETCORE_URLS=http://+:9110;https://+:9111 \
    LOG_LEVEL="Information" \
    # Connection settings
    CONNECTION_TIMEOUT=30 \
    CONNECTION_RETRIES=3 \
    DNS_CACHE_TTL=60 \
    # Network configuration
    HTTP_PROXY="" \
    HTTPS_PROXY="" \
    NO_PROXY="localhost,127.0.0.1" \
    # TLS/SSL Config
    CERT_PASSWORD="" \
    CERT_SOURCE="generate" \
    USE_HTTPS="true" \
    # Cloud settings
    CLOUD_PROVIDER="auto-detect"

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Copy project files, restore dependencies, and build in a single layer
COPY ["SQLFlowApi/SQLFlowApi.csproj", "SQLFlowApi/"]
COPY ["SQLFlowCore/SQLFlowCore.csproj", "SQLFlowCore/"]
RUN dotnet restore "./SQLFlowApi/SQLFlowApi.csproj"

# Copy source and build
COPY . .
WORKDIR "/src/SQLFlowApi"
ARG BUILD_CONFIGURATION=Release
RUN dotnet build "./SQLFlowApi.csproj" -c ${BUILD_CONFIGURATION} -o /app/build \
    && dotnet publish "./SQLFlowApi.csproj" -c ${BUILD_CONFIGURATION} -o /app/publish /p:UseAppHost=false /p:Version=1.0.0

# Final stage
FROM base AS final
WORKDIR /app

# Create non-root user and set permissions in one layer
RUN addgroup -g 1000 appgroup && \
    adduser -u 1000 -G appgroup -s /bin/sh -D appuser && \
    chown -R appuser:appgroup /app /https /tmp/diagnostics

# Copy application
COPY --from=build /app/publish .

# Copy scripts - using wildcard to copy all scripts and setting ownership
COPY --chown=appuser:appgroup ./Scripts/api/entrypoint.sh /app/scripts/
COPY --chown=appuser:appgroup ./Scripts/api/healthcheck.sh /app/scripts/
COPY --chown=appuser:appgroup ./Scripts/api/network-diagnostics.sh /app/scripts/
COPY --chown=appuser:appgroup ./Scripts/api/setup-certificates.sh /app/scripts/

# Make scripts executable with error handling
RUN chmod +x /app/scripts/*.sh || echo "Warning: No scripts found in /app/scripts/"

# Copy entrypoint and healthcheck scripts separately to ensure they exist
COPY --chown=appuser:appgroup ./Scripts/api/entrypoint.sh /app/
COPY --chown=appuser:appgroup ./Scripts/api/healthcheck.sh /app/
RUN chmod +x /app/entrypoint.sh /app/healthcheck.sh

# Default port configuration (override with -e or docker compose)
ENV ASPNETCORE_URLS=http://+:9110;https://+:9111

# Configure healthcheck
HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 \
    CMD ["/app/healthcheck.sh"]

# Set non-root user and entrypoint
USER appuser
ENTRYPOINT ["/sbin/tini", "--", "/app/entrypoint.sh"]