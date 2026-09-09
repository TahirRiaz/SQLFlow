# DeltaForge local source databases

This directory spins up the three non-SQL-Server source engines DeltaForge supports: **PostgreSQL**, **MySQL**,
and **Oracle**, each seeded with the well-known [Sakila](https://github.com/jOOQ/sakila) sample database. Sakila
has the same schema on every engine (tables `actor`, `film`, `customer`, `rental`, ...), so a pipeline authored
against one engine reads almost identically against another.

These are **source** systems only. Every DeltaForge flow writes to a SQL Server sink, which you run separately
(a local SQL Server instance or its own container). The provider integration tests need that sink too.

## Prerequisites

- Docker (Desktop or Engine) with the Compose v2 plugin (`docker compose`, not `docker-compose`).
- Network access on the first run, to download the Sakila SQL and pull the images.
- `curl` (Linux/macOS/Git Bash) or PowerShell 5.1+ (Windows) for the sample fetch step.

## Quick start

```powershell
# Windows / PowerShell
pwsh docker/up.ps1
```

```bash
# Linux / macOS / Git Bash
./docker/up.sh
```

`up` downloads the sample SQL into `docker/<engine>/initdb/` (if not already there), then runs
`docker compose up -d`. The SQL in each `initdb` directory runs **once**, the first time a container initializes
its data volume. Watch readiness with:

```bash
docker compose -f docker/docker-compose.yml ps
```

Oracle is the slowest to report healthy on a first run because it builds the `FREEPDB1` pluggable database and
loads Sakila before opening.

Stop everything with `docker/down.ps1` / `./docker/down.sh`. Add `-Volumes` (PowerShell) or `VOLUMES=1` (bash)
to also delete the data volumes, which forces a fresh re-seed on the next start.

## Connection details

| Engine     | Host / Port      | Database / Service | User     | Password      | Sakila location            |
|------------|------------------|--------------------|----------|---------------|----------------------------|
| PostgreSQL | localhost:5432   | `deltaforge`       | postgres | postgres      | database `deltaforge`, schema `public` |
| MySQL      | localhost:3306   | `sakila`           | root     | devpassword   | database `sakila`          |
| Oracle     | localhost:1521   | `FREEPDB1`         | system   | devpassword   | schema `SAKILA`            |

Override any of these with a `docker/.env` file (copy `docker/.env.example`); `docker/.env` is git-ignored.

### .NET / ADO connection strings

Use `127.0.0.1`, not `localhost`. On Windows, `localhost` resolves to IPv6 (`::1`) first, and Docker Desktop's
published-port forwarding can accept the connection there and then drop it, which surfaces as a confusing
"end of stream" error. Forcing IPv4 avoids it.

```text
# PostgreSQL (Npgsql)
Host=127.0.0.1;Port=5432;Database=deltaforge;Username=postgres;Password=postgres

# MySQL (MySqlConnector). AllowPublicKeyRetrieval lets caching_sha2_password auth (MySQL 8.4's default)
# complete over a non-TLS local connection.
Server=127.0.0.1;Port=3306;Database=sakila;User ID=root;Password=devpassword;AllowPublicKeyRetrieval=True;SslMode=Preferred

# Oracle (Oracle.ManagedDataAccess)
User Id=system;Password=devpassword;Data Source=127.0.0.1:1521/FREEPDB1
```

First-boot timing: the official MySQL and PostgreSQL images keep the server on a local socket only (no TCP
port) until every init script has finished, and the jOOQ Sakila data scripts insert row by row, so on the very
first `up` the TCP port opens only after the sample finishes loading (a few minutes for MySQL and PostgreSQL).
`docker compose ps` shows "healthy" once the socket is up; if a host connection is refused right after that,
the data load is still running. Oracle opens its listener once it prints `DATABASE IS READY TO USE!`.

For DeltaForge YAML, declare the source with the matching provider:

```yaml
connections:
  shop:
    provider: postgres    # or: mysql, oracle
    connection: ${env:SQLFLOW_SOURCE}
```

## Running the provider integration tests

The foreign-source integration tests in `tests/SqlFlow.Core.Tests/Integration/ForeignSourceIngestionTests.cs`
each skip unless their source connection string is set, and they all need the SQL Server sink. Set the
environment variables (for example in the git-ignored `.sqlflow/env` file) and run the suite:

```text
SQLFLOW_TEST_DB      = <the SQL Server sink, e.g. Server=localhost,1433;Database=TestDB;User ID=sa;Password=...;TrustServerCertificate=True>
SQLFLOW_TEST_PG      = Host=localhost;Port=5432;Database=deltaforge;Username=postgres;Password=postgres
SQLFLOW_TEST_MYSQL   = Server=localhost;Port=3306;Database=sakila;User ID=root;Password=devpassword
SQLFLOW_TEST_ORACLE  = User Id=system;Password=devpassword;Data Source=localhost:1521/FREEPDB1
```

```bash
dotnet test tests/SqlFlow.Core.Tests --filter FullyQualifiedName~ForeignSourceIngestionTests
```

Each test seeds its own small table on the source, ingests it into the sink, verifies the keyed upsert, and
drops the table afterwards, so they do not depend on the Sakila data being present. Sakila is there for
exploratory work and for building full pipelines by hand.

## How the sample data is loaded

`fetch-samples` downloads the schema and data scripts from the jOOQ/sakila mirror into the per-engine `initdb`
directories, named so they run in order:

- **MySQL** and **PostgreSQL** run the scripts as-is; the MySQL script creates its own `sakila` database, the
  PostgreSQL script loads into the default `deltaforge` database's `public` schema.
- **Oracle** loads into a dedicated `SAKILA` schema through the committed wrapper `10_load_sakila.sql`. The
  jOOQ Oracle scripts are SQL*Plus-authored: their data uses `&` (which SQL*Plus would treat as a substitution
  prompt) and they mix `;` and `/` terminators, so the wrapper runs them with `SET DEFINE OFF` and errors made
  non-fatal, and steers their unqualified objects and rows into `SAKILA` via `CURRENT_SCHEMA`. The fetch step
  drops the raw scripts into the `oracle/initdb/sql/` subfolder, which the image does not auto-execute; the
  wrapper pulls them in with `@@`.

The downloaded `*.sql` files are git-ignored; only the small hand-written init scripts (including the Oracle
wrapper) and this documentation are tracked. Re-fetch with `pwsh docker/fetch-samples.ps1 -Force` or
`FORCE=1 ./docker/fetch-samples.sh`.
