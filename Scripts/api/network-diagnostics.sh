#!/bin/bash

# Function for logging
log() {
    echo "$(date +"%Y-%m-%d %H:%M:%S") - NETWORK: $1" >> /tmp/diagnostics/network.log
}

# Create diagnostics directory
mkdir -p /tmp/diagnostics

log "Starting network diagnostics"

# Log network configuration
log "Network configuration:"
ip addr show >> /tmp/diagnostics/network.log 2>&1
log "Routing table:"
ip route show >> /tmp/diagnostics/network.log 2>&1
log "DNS configuration:"
cat /etc/resolv.conf >> /tmp/diagnostics/network.log 2>&1

# Log environment variables related to networking
log "Proxy environment variables:"
echo "HTTP_PROXY=$HTTP_PROXY" >> /tmp/diagnostics/network.log
echo "HTTPS_PROXY=$HTTPS_PROXY" >> /tmp/diagnostics/network.log
echo "NO_PROXY=$NO_PROXY" >> /tmp/diagnostics/network.log

# Test basic connectivity
log "Testing basic connectivity:"
ping -c 3 8.8.8.8 >> /tmp/diagnostics/network.log 2>&1 || log "WARNING: Cannot ping 8.8.8.8"

# Test DNS resolution
log "Testing DNS resolution:"
nslookup google.com >> /tmp/diagnostics/network.log 2>&1 || log "WARNING: Cannot resolve google.com"

# Check internet connectivity
log "Testing internet connectivity:"
curl -s -m 5 https://www.google.com > /dev/null 2>&1
if [ $? -eq 0 ]; then
    log "Internet connectivity: OK"
else
    log "WARNING: Internet connectivity check failed"
    
    # Attempt to diagnose the issue
    log "Attempting to diagnose connectivity issues:"
    
    # Check if it's a DNS issue
    curl -s -m 5 https://8.8.8.8 > /dev/null 2>&1
    if [ $? -eq 0 ]; then
        log "Can connect to IP directly, likely a DNS issue"
    else
        log "Cannot connect to IP directly, likely a network/firewall issue"
    fi
    
    # Check if it's a proxy issue
    if [ -n "$HTTP_PROXY" ] || [ -n "$HTTPS_PROXY" ]; then
        log "Proxy is configured, may need to check proxy settings"
    fi
    
    # Try to trace the route
    traceroute -m 10 8.8.8.8 >> /tmp/diagnostics/network.log 2>&1 || log "Cannot run traceroute"
fi

# Collect TCP connection information
log "Current TCP connections:"
netstat -tuln >> /tmp/diagnostics/network.log 2>&1

# Monitor for network changes periodically
log "Setting up periodic network monitoring"

while true; do
    # Sleep for a while (check every 5 minutes)
    sleep 300
    
    log "Periodic network check - $(date)"
    
    # Check if the network configuration has changed
    ip addr show >> /tmp/diagnostics/network.log 2>&1
    
    # Test basic connectivity again
    ping -c 1 8.8.8.8 > /dev/null 2>&1
    if [ $? -ne 0 ]; then
        log "WARNING: Network connectivity issue detected"
        
        # Detailed diagnostics when issue detected
        log "Current network state:"
        ip addr show >> /tmp/diagnostics/network.log 2>&1
        ip route show >> /tmp/diagnostics/network.log 2>&1
        
        # Check DNS
        log "Current DNS configuration:"
        cat /etc/resolv.conf >> /tmp/diagnostics/network.log 2>&1
        nslookup google.com >> /tmp/diagnostics/network.log 2>&1 || log "DNS resolution failing"
        
        # Check if application is still running
        pgrep -f "dotnet SQLFlowApi.dll" > /dev/null 2>&1
        if [ $? -ne 0 ]; then
            log "APPLICATION IS NOT RUNNING!"
        fi
    fi
    
    # Rotate log if it gets too large (>10MB)
    if [ -f /tmp/diagnostics/network.log ] && [ $(stat -c%s /tmp/diagnostics/network.log) -gt 10485760 ]; then
        mv /tmp/diagnostics/network.log /tmp/diagnostics/network.log.old
        log "Network log rotated at $(date)"
    fi
done