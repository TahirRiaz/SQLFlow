# SQL Server Self-Signed Certificate Generator
# This script creates a self-signed certificate for SQL Server and optionally imports it to trusted roots

# Ensure script is running with admin privileges
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This script requires administrative privileges. Please run as Administrator." -ForegroundColor Red
    exit 1
}

# Create temp directory if it doesn't exist
if (-not (Test-Path -Path "C:\temp")) {
    New-Item -ItemType Directory -Path "C:\temp" | Out-Null
    Write-Host "Created C:\temp directory" -ForegroundColor Green
}

# Get hostname parameter or use system NetBIOS name
$hostName = $args[0]
if (-not $hostName) {
    Write-Host "No hostname provided, attempting to use system NetBIOS name..." -ForegroundColor Yellow
    $hostName = $env:COMPUTERNAME
    Write-Host "Using hostname: $hostName" -ForegroundColor Cyan
} else {
    Write-Host "Using provided hostname: $hostName" -ForegroundColor Cyan
}

# Set certificate file path
$certPath = "C:\temp\SQLServerCert.pfx"
$certPassword = ConvertTo-SecureString -String "Str0ngP@ssword" -Force -AsPlainText

# Check if certificate file already exists
$createNewCert = $true
if (Test-Path -Path $certPath) {
    Write-Host "`nA certificate file already exists at: $certPath" -ForegroundColor Yellow
    $useExisting = Read-Host "Do you want to use the existing certificate? (Y/N)"
    
    if ($useExisting -eq "Y" -or $useExisting -eq "y") {
        $createNewCert = $false
        try {
            # Try to import the existing certificate to get its details
            # Check PowerShell version to handle different parameter sets for Get-PfxCertificate
$psVersion = $PSVersionTable.PSVersion.Major

if ($psVersion -ge 5) {
    # PowerShell 5.0 or newer supports the -Password parameter
    # Load the certificate using .NET instead of Get-PfxCertificate
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
$cert.Import($certPath, "Str0ngP@ssword", [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)

} else {
    # For older PowerShell versions, prompt for password interactively
    Write-Host "Using older PowerShell version. Please enter the certificate password:"
    $certPassword = Read-Host -AsSecureString
    $cert = Get-PfxCertificate -FilePath $certPath
}

            
            Write-Host "`nUsing existing certificate:" -ForegroundColor Green
            Write-Host "Subject: $($cert.Subject)" -ForegroundColor Cyan
            Write-Host "Valid until: $($cert.NotAfter)" -ForegroundColor Cyan
            Write-Host "Thumbprint: $($cert.Thumbprint)" -ForegroundColor Cyan
            
            # Also get the certificate from the certificate store
            $certFromStore = Get-ChildItem -Path "Cert:\LocalMachine\My" | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
            if (-not $certFromStore) {
                Write-Host "Note: Certificate exists as a file but is not in the certificate store." -ForegroundColor Yellow
                Write-Host "Importing certificate to store..." -ForegroundColor Cyan
                $certFromStore = Import-PfxCertificate -FilePath $certPath -Password $certPassword -CertStoreLocation "Cert:\LocalMachine\My"
                Write-Host "Certificate imported to store successfully." -ForegroundColor Green
            }
            $cert = $certFromStore
        }
        catch {
            Write-Host "Error accessing existing certificate: $_" -ForegroundColor Red
            $retry = Read-Host "Would you like to create a new certificate instead? (Y/N)"
            if ($retry -eq "Y" -or $retry -eq "y") {
                $createNewCert = $true
            }
            else {
                Write-Host "Exiting script." -ForegroundColor Red
                exit 1
            }
        }
    }
    else {
        Write-Host "Will create a new certificate." -ForegroundColor Cyan
        $backupPath = "C:\temp\SQLServerCert_$(Get-Date -Format 'yyyyMMdd_HHmmss').pfx"
        Copy-Item -Path $certPath -Destination $backupPath
        Write-Host "Existing certificate backed up to: $backupPath" -ForegroundColor Green
    }
}

if ($createNewCert) {
    # Certificate parameters
    $subjectName = "CN=$hostName"
    $dnsNames = @($hostName, "localhost")
    $ipAddresses = @((Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -like "*Ethernet*" -or $_.InterfaceAlias -like "*Wi-Fi*" }).IPAddress)

    Write-Host "`nCreating certificate with the following parameters:" -ForegroundColor Cyan
    Write-Host "Subject: $subjectName" -ForegroundColor Cyan
    Write-Host "DNS Names: $($dnsNames -join ', ')" -ForegroundColor Cyan
    Write-Host "IP Addresses: $($ipAddresses -join ', ')" -ForegroundColor Cyan
    Write-Host "Output Path: $certPath" -ForegroundColor Cyan

    # Create the certificate
    try {
        # Create self-signed certificate valid for 5 years
        $cert = New-SelfSignedCertificate `
            -Subject $subjectName `
            -DnsName $dnsNames `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -NotBefore (Get-Date) `
            -NotAfter (Get-Date).AddYears(5) `
            -CertStoreLocation "Cert:\LocalMachine\My" `
            -FriendlyName "SQL Server Certificate for $hostName" `
            -HashAlgorithm SHA256 `
            -KeyUsage DigitalSignature, KeyEncipherment, DataEncipherment `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1,1.3.6.1.5.5.7.3.2")
        
        # Export the certificate to PFX file with private key
        Export-PfxCertificate -Cert $cert -FilePath $certPath -Password $certPassword | Out-Null
        
        Write-Host "`nCertificate created successfully!" -ForegroundColor Green
        Write-Host "Certificate exported to: $certPath" -ForegroundColor Green
        Write-Host "Certificate password: Str0ngP@ssword" -ForegroundColor Yellow
        Write-Host "Certificate thumbprint: $($cert.Thumbprint)" -ForegroundColor Cyan
    }
    catch {
        Write-Host "Error creating certificate: $_" -ForegroundColor Red
        exit 1
    }
}

# Ask if user wants to add to trusted root
$addToTrustedRoot = Read-Host "`nDo you want to add this certificate to the Trusted Root Certification Authorities store? (Y/N)"

if ($addToTrustedRoot -eq "Y" -or $addToTrustedRoot -eq "y") {
    try {
        # Export certificate to CER file (public key only)
        $cerPath = "C:\temp\SQLServerCert.cer"
        Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
        
        # Import to Trusted Root Certification Authorities store
        Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
        
        Write-Host "Certificate successfully added to the Trusted Root Certification Authorities store!" -ForegroundColor Green
        
        # Clean up the CER file
        Remove-Item -Path $cerPath -Force
    }
    catch {
        Write-Host "Error adding certificate to trusted root: $_" -ForegroundColor Red
    }
}

# Ask if user wants to import certificate to SQL Server
$importToSQL = Read-Host "`nDo you want to automatically import this certificate to SQL Server Configuration? (Y/N)"

if ($importToSQL -eq "Y" -or $importToSQL -eq "y") {
    try {
        # Check if SQL Server Configuration is available
        $sqlConfigPath = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server"
        
        if (Test-Path $sqlConfigPath) {
            # Get SQL Server instances
            $instances = Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server" | 
                         Where-Object { $_.Name -match "MSSQL\d+\." }
            
            if ($instances.Count -gt 0) {
                # Display available instances
                Write-Host "`nAvailable SQL Server instances:" -ForegroundColor Cyan
                $instanceList = @()
                $i = 1
                
                foreach ($instance in $instances) {
                    $instanceName = $instance.Name -replace ".*\\(.*)", '$1'
                    Write-Host "$i. $instanceName" -ForegroundColor White
                    $instanceList += $instanceName
                    $i++
                }
                
                $selectedInstance = Read-Host "`nSelect the instance number to configure (1-$($instanceList.Count))"
                
                if ([int]$selectedInstance -ge 1 -and [int]$selectedInstance -le $instanceList.Count) {
                    $instanceName = $instanceList[[int]$selectedInstance - 1]
                    
                    # Set registry values for the certificate - note this is a simplified approach
                    # In a production environment, using SQL Server Configuration Manager is recommended
                    $certThumbprint = $cert.Thumbprint
                    
                    Write-Host "`nThe certificate has been prepared for SQL Server." -ForegroundColor Green
                    Write-Host "For security reasons, manual configuration in SQL Server Configuration Manager is required:" -ForegroundColor Yellow
                    
                    # Display instructions for manual configuration
                    Write-Host "`n===========================================================" -ForegroundColor White
                    Write-Host "SQL SERVER CERTIFICATE IMPORT INSTRUCTIONS" -ForegroundColor Cyan
                    Write-Host "===========================================================" -ForegroundColor White
                    Write-Host "1. Open SQL Server Configuration Manager" -ForegroundColor White
                    Write-Host "2. Expand 'SQL Server Network Configuration'" -ForegroundColor White
                    Write-Host "3. Right-click on 'Protocols for $instanceName'" -ForegroundColor White
                    Write-Host "4. Select 'Properties'" -ForegroundColor White
                    Write-Host "5. Select the 'Certificate' tab" -ForegroundColor White
                    Write-Host "6. The certificate should be visible in the dropdown" -ForegroundColor White
                    Write-Host "   If not, click 'Import' button" -ForegroundColor White
                    Write-Host "7. Browse to: $certPath" -ForegroundColor Yellow
                    Write-Host "8. Enter the password: Str0ngP@ssword" -ForegroundColor Yellow
                    Write-Host "9. Click 'OK' to close the Properties dialog" -ForegroundColor White
                    Write-Host "10. Restart the SQL Server service for changes to take effect" -ForegroundColor White
                }
                else {
                    Write-Host "Invalid selection" -ForegroundColor Red
                }
            }
            else {
                Write-Host "No SQL Server instances found" -ForegroundColor Red
            }
        }
        else {
            Write-Host "SQL Server does not appear to be installed on this machine" -ForegroundColor Red
        }
    }
    catch {
        Write-Host "Error configuring SQL Server: $_" -ForegroundColor Red
    }
}
else {
    # Display manual instructions
    Write-Host "`n===========================================================" -ForegroundColor White
    Write-Host "SQL SERVER CERTIFICATE IMPORT INSTRUCTIONS" -ForegroundColor Cyan
    Write-Host "===========================================================" -ForegroundColor White
    Write-Host "1. Open SQL Server Configuration Manager" -ForegroundColor White
    Write-Host "2. Expand 'SQL Server Network Configuration'" -ForegroundColor White
    Write-Host "3. Right-click on 'Protocols for MSSQLSERVER' (or your instance name)" -ForegroundColor White
    Write-Host "4. Select 'Properties'" -ForegroundColor White
    Write-Host "5. Select the 'Certificate' tab" -ForegroundColor White
    Write-Host "6. Click 'Import' button" -ForegroundColor White
    Write-Host "7. Browse to: $certPath" -ForegroundColor Yellow
    Write-Host "8. Enter the password: Str0ngP@ssword" -ForegroundColor Yellow
    Write-Host "9. Click 'OK' to close the Properties dialog" -ForegroundColor White
    Write-Host "10. Restart the SQL Server service for changes to take effect" -ForegroundColor White
    Write-Host "===========================================================" -ForegroundColor White
}

Write-Host "`nNOTE: You may need to enable SSL encryption in SQL Server:" -ForegroundColor Magenta
Write-Host "1. In SQL Server Configuration Manager, go to Protocols > Properties" -ForegroundColor White
Write-Host "2. On the 'Flags' tab, set 'Force Encryption' to 'Yes'" -ForegroundColor White
Write-Host "3. Restart the SQL Server service" -ForegroundColor White
Write-Host "===========================================================" -ForegroundColor White