-- Loads the Sakila sample into a dedicated SAKILA schema. Runs once, as the init user, on first database
-- creation. This is the ONLY script the image auto-executes for Oracle: the raw jOOQ scripts live outside the
-- init directory (mounted at /opt/sakila) precisely because the image's init runner recurses into subfolders
-- and would otherwise run them directly as SYS, creating SYS-owned objects (Oracle forbids triggers on SYS
-- objects) and bypassing the schema targeting below.
--
-- The jOOQ Oracle scripts are SQL*Plus-authored and need care: their data uses '&' (which SQL*Plus would treat
-- as a substitution-variable prompt), and they mix ';' and '/' statement terminators (which re-executes some
-- CREATEs, raising harmless "name already used" errors). So substitution is turned off and errors are made
-- non-fatal, and their unqualified objects and rows are steered into SAKILA through CURRENT_SCHEMA, so the
-- tables (and their triggers) are owned by SAKILA rather than SYS.
SET DEFINE OFF
SET ECHO OFF
WHENEVER SQLERROR CONTINUE

-- The image runs init scripts as SYS against the CDB root; switch into the FREEPDB1 pluggable database (the
-- one applications connect to) so SAKILA and its data are created there, not in the root container.
ALTER SESSION SET CONTAINER = FREEPDB1;

CREATE USER sakila IDENTIFIED BY sakila DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS;
GRANT CONNECT, RESOURCE, CREATE VIEW TO sakila;

ALTER SESSION SET CURRENT_SCHEMA = SAKILA;
@/opt/sakila/oracle-sakila-schema.sql
@/opt/sakila/oracle-sakila-data.sql
COMMIT;
