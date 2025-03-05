# SQL Server SSL Configuration Guide for SQLFlow Sandbox

## Script Location
The `mssql-ssl-wizard.ps1` script is located in the SQLFlow repository at:
`SQLFlow/Sandbox/db at master · TahirRiaz/SQLFlow`

This guide explains how to enable SSL encryption for SQL Server in the SQLFlow sandbox environment using the provided PowerShell script.

## Overview

Securing SQL Server communications with SSL encryption is critical for protecting sensitive data in transit. This process typically requires several manual steps, including certificate creation, configuration, and proper service account setup. The included PowerShell script automates much of this process for development and testing environments.

## Prerequisites

- Windows Server with SQL Server installed
- PowerShell with administrator privileges
- SQL Server service running with appropriate permissions

## SQL Server Service Account Requirements

For SSL encryption to work properly, the SQL Server service must run with an account that has access to the certificate's private key. 

**Important Notes:**
- The SQL Server service account must have `Read` permission on the certificate's private key
- If running SQL Server with a domain account or local account (rather than the default `NT Service\MSSQLSERVER`), ensure this account has proper certificate access
- For production environments, never use the default password included in the script

## Certificate Requirements

The certificate used for SQL Server encryption must meet these requirements:

1. Server authentication certificate (with server authentication EKU - Extended Key Usage)
2. Private key must be exportable
3. Certificate must be trusted by clients (added to Trusted Root Certification Authorities)

Adding the certificate to the trusted root store ensures client applications recognize the certificate without security warnings. Without this step, applications would need to bypass certificate validation, creating security risks.

## Installation Steps

### 1. Run the PowerShell Script

```powershell
# Run as Administrator
.\mssql-ssl-wizard.ps1 [optional_hostname]
```

If you don't provide a hostname parameter, the script will use the machine's NetBIOS name.

### 2. Script Actions

The script automatically:
- Creates a self-signed certificate valid for 5 years
- Exports the certificate to `C:\temp\SQLServerCert.pfx`
- Offers to add the certificate to your Trusted Root Certification Authorities
- Provides guidance for configuring SQL Server to use the certificate

### 3. SQL Server Configuration

After running the script:

1. Open SQL Server Configuration Manager
2. Navigate to SQL Server Network Configuration > Protocols for [instance_name]
3. Open Properties and go to the Certificate tab
4. Select the generated certificate
5. On the Flags tab, set "Force Encryption" to "Yes"
6. Restart the SQL Server service

## Troubleshooting

If you encounter connectivity issues after enabling encryption:

1. Verify the certificate is valid and not expired
2. Ensure the SQL Server service account has access to the certificate's private key
3. Check the SQL Server error log for certificate-related errors
4. Verify client applications are configured to trust the certificate

## Security Considerations

For production environments:
- Generate proper certificates from a trusted certificate authority
- Use strong, unique passwords for certificate protection
- Follow the principle of least privilege for service accounts
- Consider implementing a certificate rotation strategy

## Additional Resources

- [SQL Server Certificate Management (Microsoft Docs)](https://docs.microsoft.com/en-us/sql/database-engine/configure-windows/enable-encrypted-connections-to-the-database-engine)
- [Certificate Requirements for SSL Connections](https://docs.microsoft.com/en-us/sql/connect/odbc/linux-mac/using-ssl-to-encrypt-a-connection)