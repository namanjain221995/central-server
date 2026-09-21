-- ---------------------------------------------------------------------------
-- Creates the platform database and its two roles. Idempotent.
--
-- Run with psql, as a PostgreSQL superuser, connected to the "postgres"
-- database, with five psql variables set:
--
--   owner / ownerpw   schema owner. DDL rights; used ONLY by the migration job.
--   app   / apppw     restricted runtime role used by the Admin and Agent APIs.
--   db                database name.
--
-- On the Ubuntu host, infra/ubuntu/setup-postgres.sh feeds this file to psql and
-- sets the variables from the environment (\getenv), so no password ever becomes
-- a command-line argument. On a developer machine:
--
--   psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
--        -v owner=endpoint_owner -v ownerpw=... -v app=endpoint_app -v apppw=... \
--        -v db=endpoint_platform -f infra/postgres/setup-database.sql
--
-- Two roles, on purpose. The runtime role is created here with no object
-- privileges at all; the migration job grants it exactly what it needs, which on
-- the audit table is INSERT and SELECT and nothing else. That split is what
-- makes "the application cannot rewrite history" a property of the database
-- rather than a promise made by application code (ADR-0003, ADR-0004).
--
-- Neither role is a superuser. Nothing in the migrations needs one.
--
-- Every value goes through format() with %I / %L, which emits a correctly quoted
-- identifier or literal, so neither a role name nor a password can break out of
-- its syntactic context. \gexec runs each generated statement; psql does NOT
-- substitute variables inside dollar-quoted blocks, which is why this is not a
-- DO $$ ... $$ block.
-- ---------------------------------------------------------------------------

\echo 'endpoint-platform: roles...'

SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'owner', :'ownerpw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'owner')
\gexec

-- Re-asserted on every run, so re-running after a password change in the secrets
-- file is how the server learns the new value.
SELECT format(
    'ALTER ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
    :'owner', :'ownerpw')
\gexec

SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'app', :'apppw')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app')
\gexec

-- The application role must never be able to create databases or roles.
SELECT format(
    'ALTER ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
    :'app', :'apppw')
\gexec

\echo 'endpoint-platform: database...'

-- template0 so UTF8 is accepted whatever encoding this cluster's template1 has.
SELECT format('CREATE DATABASE %I OWNER %I TEMPLATE template0 ENCODING ''UTF8''', :'db', :'owner')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db')
\gexec

SELECT format('ALTER DATABASE %I OWNER TO %I', :'db', :'owner')
\gexec

-- Deny the implicit PUBLIC grants. Anything the application needs is granted
-- explicitly by the migration job.
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'db')
\gexec

SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'db', :'app')
\gexec

\connect :"db"

REVOKE ALL ON SCHEMA public FROM PUBLIC;

\echo 'endpoint-platform: database and roles are ready.'
