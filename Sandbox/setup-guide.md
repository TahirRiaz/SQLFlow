# SQLFlow Manual Setup and Database Restoration Guide

This guide walks you through setting up SQLFlow, pulling Docker images, starting containers, and restoring databases from backups **manually** (i.e., without the provided PowerShell automation script). By following these steps, you will:

1. Download a backup from a GitHub release.
2. Set environment variables for SQLFlow.
3. Clean up any existing containers or volumes that might conflict.
4. Update your `docker-compose.yml` paths (if necessary).
5. Pull the latest Docker images.
6. Start only the SQL Server container.
7. Restore your database(s) inside the container.
8. Update internal connection strings.
9. Start the remaining containers.
10. Verify everything is working.

---

## Prerequisites

1. **Docker** (and Docker Compose) must be installed on your machine.
2. **Administrator privileges** may be required for certain steps (like setting machine-level environment variables on Windows).
3. You have **git** or a web browser to download the backup files from GitHub.
4. You have the appropriate `.bak` files or know how to retrieve them from the GitHub releases of `[TahirRiaz/SQLFlow](https://github.com/TahirRiaz/SQLFlow/releases)` (by default).

---

## 1. Downloading the Backup (.zip) from GitHub Releases

1. **Identify the GitHub repository**: By default, the backups are hosted on [TahirRiaz/SQLFlow](https://github.com/TahirRiaz/SQLFlow/releases).

2. **Navigate to the releases page**:  
   Open your browser and go to:  
   [https://github.com/TahirRiaz/SQLFlow/releases](https://github.com/TahirRiaz/SQLFlow/releases)

3. **Locate a `.zip` backup**:  
   Find the release that contains the `.zip` backup file(s) you want to use. It might be named something like `SQLFlow_Backup_YYYYMMDD.zip`.

4. **Download the `.zip` file**:  
   - Click on the asset (the `.zip` file) to download it to your preferred local folder.  
   - For example, let's say you choose `C:\SQLFlow_Backups` (Windows) or `~/SQLFlow_Backups` (Linux/macOS).

5. **Extract the `.zip`**:  
   - Extract the `.zip` contents into the same folder or a subfolder.  
   - After extraction, you should have:
     - A `docker-compose.yml` file (likely),
     - One or more `.bak` files, or a folder containing `.bak` files.

6. **Confirm your final folder structure**:  
   You should see something like:
   ```
   C:\SQLFlow_Backups
   ├─ docker-compose.yml
   ├─ (possibly other files/folders)
   └─ MyDatabaseBackup.bak  (or located in a subfolder)
   ```

---

## 2. Setting Environment Variables

These environment variables tell SQLFlow how to connect internally to your SQL Server. You have two basic approaches:

### Option A: Set them at the **system (machine) level** (Windows example)

1. Open an **elevated** PowerShell or Command Prompt.
2. Run:
   ```powershell
   setx SQLFlowConStr "Server=sqlflow-mssql,1433;Database=dw-sqlflow-prod;User ID=SQLFlow;Password=Passw0rd123456;TrustServerCertificate=True;"
   setx SQLFlowOpenAiApiKey "YOUR-OPENAI-API-KEY"
   ```
3. Close and reopen your terminal to ensure they’ve been added to your environment.

### Option B: Use a **`.env` file** (Docker Compose approach)

1. Create a file named `.env` **in the same folder** as your `docker-compose.yml`.
2. Inside `.env`, put:
   ```bash
   SQLFlowConStr=Server=sqlflow-mssql,1433;Database=dw-sqlflow-prod;User ID=SQLFlow;Password=Passw0rd123456;TrustServerCertificate=True;
   SQLFlowOpenAiApiKey=YOUR-OPENAI-API-KEY
   ```
3. Docker Compose automatically loads these variables from `.env` at runtime.

> **Note**: Make sure to replace `YOUR-OPENAI-API-KEY` with a valid key if you are using GPT-based features.

---

## 3. Clean Up Existing Containers, Networks, and Volumes

If you have a previous SQLFlow deployment or Docker containers that might conflict, you should remove them to avoid port conflicts or volume collisions.

### 3.1 Stopping & Removing Existing Containers

1. **Stop all containers** (if you suspect they’re related to SQLFlow):
   ```bash
   docker ps -a
   ```
   Look for containers named something like `sqlflow-ui`, `sqlflow-api`, `sqlflow-mssql`, etc. Then stop & remove them:
   ```bash
   docker stop <container_id_or_name>
   docker rm <container_id_or_name>
   ```
   Repeat for each container that is part of SQLFlow.

### 3.2 Remove any leftover Docker networks

1. **List networks** containing `sqlflow`:
   ```bash
   docker network ls --filter "name=sqlflow"
   ```
2. **Remove** them if any exist:
   ```bash
   docker network rm <network_id>
   ```

### 3.3 Remove any leftover Docker volumes

1. **List volumes** containing `sqlflow`:
   ```bash
   docker volume ls --filter "name=sqlflow"
   ```
2. **Remove** them if any exist:
   ```bash
   docker volume rm <volume_name>
   ```

If no containers, networks, or volumes appear, you can skip this step.

---

## 4. Update Paths Inside `docker-compose.yml`

In some releases, the `docker-compose.yml` might reference specific paths like `C:/SQLFlow` (Windows) or other custom paths. You may need to adjust them to your actual directory structure.

1. **Open `docker-compose.yml`** in a text editor.
2. Search for volume mappings like:
   ```yaml
   volumes:
     - "C:/SQLFlow/data:/var/opt/mssql/data"
   ```
   or environment references such as `C:/SQLFlow/logs`.
3. Replace them with your chosen folder path.  
   For example, if your extracted folder is `C:\SQLFlow_Backups`, replace:
   ```
   C:/SQLFlow
   ```
   with:
   ```
   C:/SQLFlow_Backups
   ```
4. **Save the changes**.

> If you’re on Linux or macOS, ensure paths use the correct format, for example `/home/<user>/SQLFlow_Backups` or `~/SQLFlow_Backups`.

---

## 5. Pull the Required Docker Images

From the same folder where your `docker-compose.yml` resides (e.g., `C:\SQLFlow_Backups` or `~/SQLFlow_Backups`), run:

```bash
cd /path/to/SQLFlow_Backups

# If you have Docker Compose V2:
docker compose pull

# OR if you still have docker-compose (classic):
docker-compose pull
```

This will download all images needed by the SQLFlow solution (e.g., `sqlflow-mssql`, `sqlflow-ui`, `sqlflow-api`, etc.).

---

## 6. Start Only the SQL Server Container

We start only the SQL Server container first, to ensure the DB is ready before we restore.

In the same directory as `docker-compose.yml`:

```bash
docker compose up -d sqlflow-mssql
```
(or `docker-compose up -d sqlflow-mssql` if using classic docker-compose.)

- **Verify** it’s up:
  ```bash
  docker ps
  ```
  Look for `sqlflow-mssql` in a **`healthy`** or **`running`** state.

- **Wait** ~30 seconds for SQL Server’s initial startup to complete. Check logs:
  ```bash
  docker compose logs sqlflow-mssql
  ```
  You should eventually see:  
  `SQL Server is now ready for client connections.`

---

## 7. Restore the Database from Your `.bak` File

Now that `sqlflow-mssql` is running, you can restore the database. Below is a manual approach:

1. **Identify your `.bak` file**: Suppose it’s called `MyDatabaseBackup.bak` in `C:\SQLFlow_Backups`.

2. **Create a backup folder** inside the container (optional but recommended):
   ```bash
   docker exec sqlflow-mssql mkdir -p /var/opt/mssql/bak
   docker exec sqlflow-mssql chown -R mssql:mssql /var/opt/mssql/bak
   ```

3. **Copy your `.bak` file** into the container:
   ```bash
   docker cp "C:\SQLFlow_Backups\MyDatabaseBackup.bak" sqlflow-mssql:/var/opt/mssql/bak/
   ```
   (Adjust paths for Linux/macOS as needed.)

4. **Open a SQL shell** in the container (or from your host):
   ```bash
   docker exec -it sqlflow-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U SA -P "Passw0rd123456"
   ```
   > Replace the password above if your container uses a different SA password.

5. **Run the RESTORE command**. Below is an example T-SQL sequence you can type after you’re in the `sqlcmd` environment:

   ```sql
   -- We'll restore to a new database name. Adjust as you wish.
   RESTORE DATABASE [dw-sqlflow-prod]
   FROM DISK = N'/var/opt/mssql/bak/MyDatabaseBackup.bak'
   WITH MOVE 'YourDataLogicalName' 
       TO '/var/opt/mssql/data/dw-sqlflow-prod_Primary.mdf',
        MOVE 'YourLogLogicalName'
       TO '/var/opt/mssql/log/dw-sqlflow-prod_Log.ldf',
       REPLACE,
       STATS = 10;
   GO
   ```

   Where:
   - `YourDataLogicalName` and `YourLogLogicalName` are the logical file names embedded in the backup. If you’re unsure, run:
     ```sql
     RESTORE FILELISTONLY 
       FROM DISK = N'/var/opt/mssql/bak/MyDatabaseBackup.bak';
     GO
     ```
     This shows you which logical names exist in the backup (look for `Type = D` for data and `Type = L` for log).

6. **Check if the database was restored**:
   ```sql
   SELECT name 
   FROM sys.databases 
   WHERE name = 'dw-sqlflow-prod';
   GO
   ```
   If you see it listed, your restore succeeded!

7. **Exit** `sqlcmd` by typing:
   ```
   quit
   ```

---

## 7b. Update Connection Strings in `dw-sqlflow-prod` (Optional/Advanced)

Inside the `dw-sqlflow-prod` database, there may be a `[flw].[SysDataSource]` table that configures how SQLFlow connects to other data sources. If your restored database references old server paths or aliases, you can fix them:

1. Open `sqlcmd` or a SQL client again (e.g., Azure Data Studio, SSMS, etc.).
2. Connect to `dw-sqlflow-prod` using credentials. For example:
   ```bash
   docker exec -it sqlflow-mssql /opt/mssql-tools18/bin/sqlcmd \
       -S localhost -U SA -P "Passw0rd123456" \
       -d dw-sqlflow-prod
   ```
3. **Run an `UPDATE`** on `[flw].[SysDataSource]` to match your local container. For instance:

   ```sql
   UPDATE [flw].[SysDataSource]
     SET ConnectionString = 'Server=host.docker.internal,1477;Initial Catalog=dw-ods-prod;User ID=SQLFlow;Password=Passw0rd123456;TrustServerCertificate=True;Encrypt=False;Command Timeout=360;'
     WHERE Alias = 'dw-ods-prod-db';

   -- Repeat for other aliases if needed
   GO
   ```

   The key idea is that your SQLFlow containers typically reach the SQL Server via `host.docker.internal,1477` **(if you mapped port 1477)** or `localhost,1477`. Adjust accordingly.

---

## 8. Start the Remaining Containers

With the database restored and any internal references updated, you can start the rest of the SQLFlow stack:

```bash
docker compose up -d
```
(or `docker-compose up -d`)

- This brings up `sqlflow-api`, `sqlflow-ui`, and any other services specified in `docker-compose.yml`.

- **Verify** they’re running:
  ```bash
  docker compose ps
  ```
  You should see something like:
  ```
  NAME               COMMAND                  STATE    PORTS
  sqlflow-api        ...                      Up
  sqlflow-ui         ...                      Up
  sqlflow-mssql      ...                      Up
  ...
  ```

---

## 9. Access SQLFlow and Verify

1. **Open your browser** and go to:
   - [http://localhost:8110](http://localhost:8110) or
   - [https://localhost:8111](https://localhost:8111)

2. **Login** with the default credentials:
   ```
   Email:    demo@sqlflow.io
   Password: @Demo123
   ```
3. You should now see the SQLFlow interface.

---

## 10. Summary & Troubleshooting

- **Environment Variables**: Ensure `SQLFlowConStr` and `SQLFlowOpenAiApiKey` are set if your application or the containers expect them.  
- **Docker**: Make sure Docker Desktop (or Docker Engine) is **running** and not blocked by firewall settings.  
- **Common Ports**: By default, `docker-compose.yml` might open ports `8110`, `8111` (for HTTP/HTTPS UI) and `1477` (for the SQL Server container’s external port). If these are in use, edit them in `docker-compose.yml`.  
- **Check Logs**: If something doesn’t start properly, run:
  ```bash
  docker compose logs
  ```
  or for a specific service:
  ```bash
  docker compose logs sqlflow-api
  ```
- **Database Restoration**: If you run into file or permission errors, confirm:
  1. The `.bak` file actually copied into the container.
  2. The `docker exec ... chmod /var/opt/mssql/bak/` commands gave the `mssql` user ownership.
  3. The logical file names in your `RESTORE` statement match the `.bak` metadata (`RESTORE FILELISTONLY` helps you confirm).

---

# Done!

