#!/bin/bash
set -e

# Function for logging
log() {
    echo "$(date +"%Y-%m-%d %H:%M:%S") - $1"
}

# Directory for certificates
CERT_DIR="/https"
PFX_FILE="$CERT_DIR/aspnetapp.pfx"
CERT_FILE="$CERT_DIR/cert.pem"
KEY_FILE="$CERT_DIR/key.pem"

# If HTTPS is disabled, exit
if [ "$USE_HTTPS" != "true" ]; then
    log "HTTPS is disabled, skipping certificate setup"
    exit 0
fi

# If a password is not provided, generate a secure random one
if [ -z "$CERT_PASSWORD" ]; then
    CERT_PASSWORD=$(openssl rand -base64 32)
    log "Generated random certificate password"
fi

# Setup certificates based on the CERT_SOURCE value
case "$CERT_SOURCE" in
    "generate")
        log "Generating self-signed certificate"
        
        # Ensure directories exist
        mkdir -p "$CERT_DIR"
        
        # Generate a self-signed certificate
        openssl req -x509 -newkey rsa:4096 \
            -keyout "$KEY_FILE" \
            -out "$CERT_FILE" \
            -days 365 -nodes \
            -subj "/CN=localhost" \
            -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
            
        # Convert to PFX format for ASP.NET Core
        openssl pkcs12 -export \
            -out "$PFX_FILE" \
            -inkey "$KEY_FILE" \
            -in "$CERT_FILE" \
            -passout "pass:$CERT_PASSWORD"
            
        # Set permissions
        chmod 600 "$KEY_FILE"
        chmod 644 "$CERT_FILE" "$PFX_FILE"
        
        log "Self-signed certificate generated successfully"
        ;;
        
    "volume")
        log "Using certificates from mounted volume"
        
        # Check if certificate files exist
        if [ ! -f "$PFX_FILE" ]; then
            log "ERROR: Certificate file $PFX_FILE not found in mounted volume"
            log "Falling back to generating a self-signed certificate"
            
            # Generate a self-signed certificate as fallback
            openssl req -x509 -newkey rsa:4096 \
                -keyout "$KEY_FILE" \
                -out "$CERT_FILE" \
                -days 365 -nodes \
                -subj "/CN=localhost" \
                -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
                
            # Convert to PFX format for ASP.NET Core
            openssl pkcs12 -export \
                -out "$PFX_FILE" \
                -inkey "$KEY_FILE" \
                -in "$CERT_FILE" \
                -passout "pass:$CERT_PASSWORD"
                
            # Set permissions
            chmod 600 "$KEY_FILE"
            chmod 644 "$CERT_FILE" "$PFX_FILE"
        else
            log "Using existing certificate from volume"
        fi
        ;;
        
    "env")
        log "Using certificate data from environment variables"
        
        # Check if certificate data is provided in environment variables
        if [ -z "$CERT_DATA" ] || [ -z "$KEY_DATA" ]; then
            log "ERROR: CERT_DATA or KEY_DATA environment variables not provided"
            log "Falling back to generating a self-signed certificate"
            
            # Generate a self-signed certificate as fallback
            openssl req -x509 -newkey rsa:4096 \
                -keyout "$KEY_FILE" \
                -out "$CERT_FILE" \
                -days 365 -nodes \
                -subj "/CN=localhost" \
                -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
        else
            # Decode base64-encoded certificate and key data
            echo "$CERT_DATA" | base64 -d > "$CERT_FILE"
            echo "$KEY_DATA" | base64 -d > "$KEY_FILE"
            
            # Convert to PFX format for ASP.NET Core
            openssl pkcs12 -export \
                -out "$PFX_FILE" \
                -inkey "$KEY_FILE" \
                -in "$CERT_FILE" \
                -passout "pass:$CERT_PASSWORD"
        fi
        
        # Set permissions
        chmod 600 "$KEY_FILE"
        chmod 644 "$CERT_FILE" "$PFX_FILE"
        ;;
        
    *)
        log "ERROR: Unknown CERT_SOURCE value: $CERT_SOURCE"
        log "Falling back to generating a self-signed certificate"
        
        # Generate a self-signed certificate as fallback
        openssl req -x509 -newkey rsa:4096 \
            -keyout "$KEY_FILE" \
            -out "$CERT_FILE" \
            -days 365 -nodes \
            -subj "/CN=localhost" \
            -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
            
        # Convert to PFX format for ASP.NET Core
        openssl pkcs12 -export \
            -out "$PFX_FILE" \
            -inkey "$KEY_FILE" \
            -in "$CERT_FILE" \
            -passout "pass:$CERT_PASSWORD"
            
        # Set permissions
        chmod 600 "$KEY_FILE"
        chmod 644 "$CERT_FILE" "$PFX_FILE"
        ;;
esac

# Set environment variables for ASP.NET Core
export ASPNETCORE_Kestrel__Certificates__Default__Path="$PFX_FILE"
export ASPNETCORE_Kestrel__Certificates__Default__Password="$CERT_PASSWORD"

log "Certificate setup completed successfully"