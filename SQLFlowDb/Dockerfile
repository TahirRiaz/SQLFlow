# Use official Microsoft SQL Server image as the base
FROM mcr.microsoft.com/mssql/server:2022-latest

# Set environment variables for non-interactive installation
ENV ACCEPT_EULA=Y \
    MSSQL_SA_PASSWORD=Passw0rd123456 \
    MSSQL_PID=Developer \
    PATH="$PATH:/opt/mssql-tools18/bin"

# Create necessary directories
USER root
RUN mkdir -p /var/opt/mssql/data /var/opt/mssql/log /var/opt/mssql/bak /var/opt/mssql/scripts /var/opt/mssql/security

# Install necessary tools
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        wget \
        unzip \
        curl \
        openssl && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Generate self-signed certificate with correct format for SQL Server
RUN openssl req -x509 -nodes -newkey rsa:2048 \
    -keyout /var/opt/mssql/security/mssql.key \
    -out /var/opt/mssql/security/mssql.pem \
    -days 365 \
    -subj "/CN=sqlflow-mssql" \
    -addext "subjectAltName = DNS:sqlflow-mssql,DNS:localhost" && \
    chmod 600 /var/opt/mssql/security/mssql.key && \
    chmod 644 /var/opt/mssql/security/mssql.pem
 
# Convert to PKCS12 format for SQL Server with proper encryption and permissions
RUN openssl pkcs12 -export \
    -out /var/opt/mssql/security/mssql.pfx \
    -inkey /var/opt/mssql/security/mssql.key \
    -in /var/opt/mssql/security/mssql.pem \
    -passout pass:YourCertificatePassword \
    -name "SQLServerCert" && \
    chmod 644 /var/opt/mssql/security/mssql.pfx && \
    chown mssql:mssql /var/opt/mssql/security/mssql.pfx

# Copy SQL Server configuration file
COPY ./Scripts/sql/init.sql /var/opt/mssql/scripts/init.sql

# Set proper ownership of all SQL Server directories
RUN chown -R mssql:mssql /var/opt/mssql

# Create healthcheck script with
RUN echo '#!/bin/bash\n\
/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT 1" -C \n\
' > /var/opt/mssql/healthcheck.sh && \
    chmod +x /var/opt/mssql/healthcheck.sh

# Copy entrypoint script
COPY ./Scripts/sql/entrypoint.sh /var/opt/mssql/
RUN chmod +x /var/opt/mssql/entrypoint.sh

# Switch to non-root user for security
USER mssql

# Set entrypoint
ENTRYPOINT ["/var/opt/mssql/entrypoint.sh"]