# SQL Server Installation Guide

This guide walks you through downloading and installing SQL Server Developer Edition on your laptop.

## Prerequisites

- Windows operating system (Windows 10 or newer recommended)
- At least 6 GB of available hard-disk space
- Minimum 1.4 GHz processor
- Minimum 1 GB RAM (recommended: 4 GB or more)
- Internet connection

## Step 1: Download SQL Server Developer Edition

1. Go to the Microsoft SQL Server downloads page: [https://www.microsoft.com/en-us/sql-server/sql-server-downloads](https://www.microsoft.com/en-us/sql-server/sql-server-downloads)

2. Scroll down to the "Developer" section and click the "Download now" button.

3. A file named `SQL2022-SSEI-Dev.exe` (or similar, depending on the version) will be downloaded to your computer.

## Step 2: Begin the Installation

1. Locate the downloaded file and double-click to run it.

2. When the SQL Server Installation Center appears, select "Basic" for a simpler installation process or "Custom" if you want more control over which components are installed.

## Step 3: Basic Installation

If you selected "Basic" installation:

1. Accept the license terms by checking the box.

2. Choose an installation location or use the default path.

3. Click "Install" to begin the installation process.

4. Wait for the installation to complete. This may take several minutes.

## Step 4: Custom Installation

If you selected "Custom" installation:

1. In the SQL Server Installation Center, click on "Installation" in the left menu.

2. Click on "New SQL Server stand-alone installation or add features to an existing installation."

3. The SQL Server Setup wizard will run. Enter your product key or select "Developer" from the free editions list.

4. Accept the license terms and click "Next."

5. The setup will run rule checks. Address any warnings or failures, then click "Next."

6. On the Feature Selection page, choose the components you want to install. At minimum, select:
   - Database Engine Services
   - Management Tools - Basic
   - Management Tools - Complete (recommended)

7. Click "Next."

8. On the Instance Configuration page, you can use the default instance name (MSSQLSERVER) or specify a named instance. Click "Next."

9. On the Server Configuration page, you can leave the default service accounts or configure them as needed. Click "Next."

10. On the Database Engine Configuration page:
    - Select "Windows authentication mode" or "Mixed Mode" (allows both Windows authentication and SQL Server authentication)
    - If you choose Mixed Mode, enter and confirm a strong password for the SA account
    - Click "Add Current User" to add yourself as a SQL Server administrator
    - Click "Next"

11. Review the configuration summary and click "Install."

12. Wait for the installation to complete. This may take several minutes.

## Step 5: Install SQL Server Management Studio (SSMS)

SQL Server Management Studio is a separate tool that provides a graphical interface for managing SQL Server.

1. After SQL Server installation completes, you'll see a link to download SSMS. Click it, or go directly to [https://aka.ms/ssmsfullsetup](https://aka.ms/ssmsfullsetup)

2. Run the downloaded SSMS installer (SSMS-Setup-ENU.exe).

3. Accept the default installation location or choose a different one.

4. Click "Install" and wait for the installation to complete.

## Step 6: Verify Installation

1. Launch SQL Server Management Studio.

2. In the "Connect to Server" dialog:
   - Server type: Database Engine
   - Server name: Your computer name (or localhost)
   - Authentication: Windows Authentication (or SQL Server Authentication if you set up Mixed Mode)

3. Click "Connect." If the connection is successful, you have installed SQL Server correctly.

## Step 7: Enable SSL
Before starting SQLFlow setup, ensure your SQL Server or Azure SQL Database has SSL properly configured. Detailed instructions and an automation script are available at:
[SQL Server SSL Configuration Wizard](https://github.com/TahirRiaz/SQLFlow/blob/master/Sandbox/db/mssql-ssl-wizard.md)

## Additional Resources

- [SQL Server Documentation](https://learn.microsoft.com/en-us/sql/sql-server/)
- [SQL Server Developer Edition Overview](https://www.microsoft.com/en-us/sql-server/sql-server-downloads-dev-center)
- [SQL Server Management Studio Documentation](https://learn.microsoft.com/en-us/sql/ssms/download-sql-server-management-studio-ssms)

## Troubleshooting

If you encounter installation issues:

1. Check the SQL Server installation log files located at: `%ProgramFiles%\Microsoft SQL Server\[version number]\Setup Bootstrap\Log`

2. Visit the [SQL Server Installation Forum](https://social.msdn.microsoft.com/Forums/sqlserver/en-US/home?forum=sqlsetupandupgrade) for community support.

3. Make sure your system meets all prerequisites for the version you're installing.