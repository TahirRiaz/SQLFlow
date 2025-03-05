#!/bin/bash
set -e

# Function for logging
log() {
    echo "$(date +"%Y-%m-%d %H:%M:%S") - HEALTHCHECK: $1"
}

# Determine if the app is using SignalR by checking for a common SignalR assembly
check_signalr_installed() {
    if [ -f "/app/Microsoft.AspNetCore.SignalR.dll" ] || [ -f "/app/Microsoft.AspNetCore.SignalR.Core.dll" ]; then
        return 0
    else
        return 1
    fi
}

# Extract host and port from ASPNETCORE_URLS
# Parse URLs properly to support both HTTP and HTTPS
parse_urls() {
    # Default values
    PROTOCOL="http"
    HOST="localhost"
    PORT="8080"
    
    # Try to extract HTTPS URL first (preferred)
    HTTPS_URL=$(echo "$ASPNETCORE_URLS" | grep -o "https://[^;,:]*" | head -1)
    if [ -n "$HTTPS_URL" ]; then
        PROTOCOL="https"
        HOST=$(echo "$HTTPS_URL" | sed -n 's|https://\([^:]*\).*|\1|p' | tr -d '+')
        PORT=$(echo "$HTTPS_URL" | sed -n 's|https://[^:]*:\([0-9]*\).*|\1|p')
        # Default HTTPS port if not specified
        if [ -z "$PORT" ]; then PORT="443"; fi
    else
        # Fall back to HTTP URL if HTTPS not configured
        HTTP_URL=$(echo "$ASPNETCORE_URLS" | grep -o "http://[^;,:]*" | head -1)
        if [ -n "$HTTP_URL" ]; then
            HOST=$(echo "$HTTP_URL" | sed -n 's|http://\([^:]*\).*|\1|p' | tr -d '+')
            PORT=$(echo "$HTTP_URL" | sed -n 's|http://[^:]*:\([0-9]*\).*|\1|p')
            # Default HTTP port if not specified
            if [ -z "$PORT" ]; then PORT="80"; fi
        fi
    fi
    
    # If host is + or *, change to localhost for healthcheck
    if [ "$HOST" = "+" ] || [ "$HOST" = "*" ]; then
        HOST="localhost"
    fi
    
    log "Using $PROTOCOL://$HOST:$PORT for health checks"
}

# Perform basic health check
check_application_health() {
    local health_endpoint=${HEALTH_ENDPOINT:-"/health"}
    local url="${PROTOCOL}://${HOST}:${PORT}${health_endpoint}"
    local health_timeout=${HEALTH_TIMEOUT:-5}
    
    log "Checking application health at $url"
    
    # Set curl options
    local curl_opts="--max-time $health_timeout -s -o /dev/null -w %{http_code}"
    
    # Add insecure flag for HTTPS
    if [ "$PROTOCOL" = "https" ]; then
        curl_opts="$curl_opts --insecure"
    fi
    
    # Execute the curl command
    local response
    response=$(curl $curl_opts "$url")
    
    # Check if response is acceptable
    if [ "$response" = "200" ] || [ "$response" = "204" ]; then
        log "Application health check passed"
        return 0
    else
        log "Application health check failed with HTTP status: $response"
        return 1
    fi
}

# Check SignalR health if enabled and installed
check_signalr_health() {
    # Skip if SignalR checks are disabled
    if [ "${CHECK_SIGNALR:-true}" != "true" ]; then
        log "SignalR health check disabled, skipping"
        return 0
    fi
    
    # Skip if SignalR isn't installed
    if ! check_signalr_installed; then
        log "SignalR not detected, skipping SignalR health check"
        return 0
    fi
    
    # Determine SignalR endpoint to check
    local signalr_endpoint=${SIGNALR_ENDPOINT:-"/blazor-server-components-hub/negotiate"}
    local url="${PROTOCOL}://${HOST}:${PORT}${signalr_endpoint}"
    local health_timeout=${HEALTH_TIMEOUT:-5}
    
    log "Checking SignalR health at $url"
    
    # Set curl options with JSON content type header
    local curl_opts="--max-time $health_timeout -H \"Content-Type: application/json\" -s -o /dev/null -w %{http_code}"
    
    # Add insecure flag for HTTPS
    if [ "$PROTOCOL" = "https" ]; then
        curl_opts="$curl_opts --insecure"
    fi
    
    # Execute the curl command
    local response
    response=$(curl $curl_opts "$url")
    
    # Check if response is acceptable
    if [ "$response" = "200" ] || [ "$response" = "204" ]; then
        log "SignalR health check passed"
        return 0
    else
        log "SignalR health check failed with HTTP status: $response"
        # Only fail health check if REQUIRE_SIGNALR is set to true
        if [ "${REQUIRE_SIGNALR:-false}" = "true" ]; then
            return 1
        else
            log "REQUIRE_SIGNALR not set to true, continuing despite SignalR failure"
            return 0
        fi
    fi
}

# Check if the application process is running
check_process() {
    # Get the application name from environment or use a default
    local app_name=${APP_NAME:-"SQLFlowUi.dll"}
    
    if pgrep -f "dotnet $app_name" > /dev/null; then
        log "Process check passed: dotnet $app_name is running"
        return 0
    else
        log "Process check failed: dotnet $app_name is not running"
        return 1
    fi
}

# Check for available disk space
check_disk_space() {
    # Skip if disk space check is disabled
    if [ "${CHECK_DISK_SPACE:-true}" != "true" ]; then
        log "Disk space check disabled, skipping"
        return 0
    fi
    
    local min_space_mb=${MIN_DISK_SPACE_MB:-100}
    local disk_path=${DISK_PATH:-"/app"}
    local available_space=$(df -m $disk_path | awk 'NR==2 {print $4}')
    
    if [ "$available_space" -lt "$min_space_mb" ]; then
        log "Low disk space: $available_space MB available (minimum: $min_space_mb MB)"
        return 1
    fi
    
    log "Disk space check passed: $available_space MB available"
    return 0
}

# Check memory usage
check_memory() {
    # Skip if memory check is disabled
    if [ "${CHECK_MEMORY:-true}" != "true" ]; then
        log "Memory check disabled, skipping"
        return 0
    fi
    
    local max_percent=${MAX_MEMORY_PERCENT:-90}
    
    # Some systems might not have 'free' command
    if command -v free >/dev/null 2>&1; then
        local used_percent=$(free | grep Mem | awk '{print int($3/$2 * 100)}')
        
        if [ "$used_percent" -gt "$max_percent" ]; then
            log "High memory usage: $used_percent% (threshold: $max_percent%)"
            return 1
        fi
        
        log "Memory check passed: $used_percent% used"
    else
        log "Memory check skipped: 'free' command not available"
    fi
    
    return 0
}

# Main health check function
main() {
    log "Starting health check"
    
    # Parse application URLs
    parse_urls
    
    # List of checks to run
    CHECKS=()
    
    # Always check if process is running first, unless explicitly disabled
    if [ "${CHECK_PROCESS:-true}" = "true" ]; then
        CHECKS+=("check_process")
    fi
    
    # Add HTTP health check if enabled
    if [ "${CHECK_HTTP_HEALTH:-true}" = "true" ]; then
        CHECKS+=("check_application_health")
    fi
    
    # Add SignalR health check if not explicitly disabled
    if [ "${CHECK_SIGNALR:-true}" = "true" ]; then
        CHECKS+=("check_signalr_health")
    fi
    
    # Add disk space check if enabled
    if [ "${CHECK_DISK_SPACE:-true}" = "true" ]; then
        CHECKS+=("check_disk_space")
    fi
    
    # Add memory check if enabled
    if [ "${CHECK_MEMORY:-true}" = "true" ]; then
        CHECKS+=("check_memory")
    fi
    
    # Run all enabled checks
    for check in "${CHECKS[@]}"; do
        if ! $check; then
            log "Health check failed at: $check"
            return 1
        fi
    done
    
    # All checks passed
    log "All health checks passed"
    return 0
}

# Run the main health check
main
exit $?