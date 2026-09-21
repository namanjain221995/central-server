# ADR-0003: Separate migration runner and two database roles

Status: accepted (Phase 0)

## Context

Schema migrations need DDL rights. The running application does not — and
granting them to it means any application-level compromise (SQL injection,
RCE) can alter the schema, including dropping the triggers that protect the
audit trail. Additionally, migrations applied by API startup race when
multiple replicas start simultaneously.

## Decision

- Migrations live in their own assembly, `EndpointPlatform.Migrations`, which
  is also a standalone executable (migrate → apply runtime grants → seed).
  Deployments run it exactly once before starting/replacing API processes.
- Two PostgreSQL roles:
  - **owner** — DDL; used only by the migration job.
  - **runtime** — DML on ordinary tables; `SELECT, INSERT` only on
    `audit_log_entries`; no `CREATE` on the schema. Created by
    `infra/postgres/setup-database.sql`, which both the Ubuntu host and
    `scripts/run-local.ps1` run.
- The migration job re-applies grants (idempotent) every run, with
  `ALTER DEFAULT PRIVILEGES` so tables added by future migrations are covered
  automatically, and the audit-table exception re-asserted afterwards.
- Role names are passed as parameters and quoted with `format(%I)` — never
  concatenated into SQL.

## Consequences

- The owner credential exists only in the deployment pipeline's environment,
  never in an API process.
- APIs cannot perform DDL even if fully compromised (verified by live test:
  CREATE TABLE, DROP TRIGGER, UPDATE/DELETE/TRUNCATE on audit all denied).
- `Database:MigrateOnStartup` exists for convenience but defaults to false and
  is not used by the development or deployment flow; the runner is the
  supported path.
