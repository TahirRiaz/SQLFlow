#!/bin/bash
set -e

# Function for logging
log() {
    echo "$(date +"%Y-%m-%d %H:%M:%S") - $1"
}

# Detect environment and cloud provider
detect_environment() {
    if [ "$CLOUD_PROVIDER" = "auto-detect" ]; then
        # Check for AWS
        if curl -s -m 1 http://169.254.169.254/latest/meta-data/ > /dev/null 2>&1; then
            log "Detected AWS environment"
            export CLOUD_PROVIDER="aws"
        # Check for Azure
        elif curl -s -m 1 -H "Metadata:true" "http://169.254.169.254/metadata/instance?api-version=2021-02-01" > /dev/null 2>&1; then
            log "Detected Azure environment"
            export CLOUD_PROVIDER="azure"
        # Check for GCP
        elif curl -s -m 1 "http://metadata.google.internal/computeMetadata/v1/instance/" -H "Metadata-Flavor: Google" > /dev/null 2>&1; then
            log "Detected GCP environment"
            export CLOUD_PROVIDER="gcp"
        else
            log "No specific cloud provider detected, assuming generic environment"
            export CLOUD_PROVIDER="generic"
        fi
    else
        log "Using predefined cloud provider: $CLOUD_PROVIDER"
    fi
    
    # Export additional environment-specific variables
    case "$CLOUD_PROVIDER" in
        aws)
            # AWS-specific settings
            export AWS_CONTAINER_CREDENTIALS_RELATIVE_URI=$(curl -s 169.254.170.2$AWS_CONTAINER_CREDENTIALS_RELATIVE_URI)
            ;;
        azure)
            # Azure-specific settings
            ;;
        gcp)
            # GCP-specific settings
            ;;
        *)
            # Default settings
            ;;
    esac
}

# Configure network settings
configure_network() {
    # Apply proxy settings if provided
    if [ -n "$HTTP_PROXY" ]; then
        log "Configuring HTTP proxy: $HTTP_PROXY"
        export http_proxy="$HTTP_PROXY"
    fi
    
    if [ -n "$HTTPS_PROXY" ]; then
        log "Configuring HTTPS proxy: $HTTPS_PROXY" 
        export https_proxy="$HTTPS_PROXY"
    fi
    
    if [ -n "$NO_PROXY" ]; then
        log "Configuring no_proxy: $NO_PROXY"
        export no_proxy="$NO_PROXY"
    fi

    # Run network diagnostics in background
    /app/scripts/network-diagnostics.sh &
}

# Setup HTTPS certificates
setup_certificates() {
    # Call the certificate setup script
    /app/scripts/setup-certificates.sh
}

# Configure app settings based on environment
configure_app_settings() {
    # Set log level
    export Logging__LogLevel__Default="$LOG_LEVEL"
    
    # Set connection settings
    export Connection__Timeout="$CONNECTION_TIMEOUT"
    export Connection__Retries="$CONNECTION_RETRIES"
    
    log "Application configured with environment: $ASPNETCORE_ENVIRONMENT"
}

# Run startup checks
run_startup_checks() {
    # Check if the application can resolve external domains
    if ! ping -c 1 -W 5 8.8.8.8 > /dev/null 2>&1; then
        log "WARNING: Cannot ping external IP, network connectivity may be limited"
    fi
    
    if ! nslookup google.com > /dev/null 2>&1; then
        log "WARNING: DNS resolution not working, check DNS configuration"
    fi
    
    # Check for custom app startup checks
    if [ -f /app/scripts/app-startup-checks.sh ]; then
        log "Running application-specific startup checks"
        /app/scripts/app-startup-checks.sh
    fi
}

# Main execution flow
log "Starting SQL Flow API container"

# Run all setup steps
detect_environment
configure_network
setup_certificates
configure_app_settings
run_startup_checks

# Display important configuration
log "SQL Flow API is starting with the following configuration:"
log "- Environment: $ASPNETCORE_ENVIRONMENT"
log "- Cloud Provider: $CLOUD_PROVIDER" 
log "- URLs: $ASPNETCORE_URLS"

# Execute the application
exec dotnet SQLFlowApi.dll