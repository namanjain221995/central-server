# ADR-0006: Testing stack — xUnit v2 + VSTest, Shouldly, real PostgreSQL

Status: accepted (Phase 0)

## Context

`dotnet test` working out of the box is a hard requirement. Two stack choices
needed recording:

1. **xUnit v3 vs v2.** xUnit v3 runs on Microsoft.Testing.Platform (MTP). On
   the .NET 10.0.400 SDK, `dotnet test` launches the MTP host in server mode
   (`--server dotnettestcli`), and neither xunit.v3 4.0.0 nor 3.2.2 completed
   that handshake in this repository: 4.0.0 reported "Zero tests ran" while
   the same assembly executed directly passed all tests; 3.2.2 printed its own
   help text. A tooling-integration failure, not a test failure — but one that
   breaks the primary developer workflow.
2. **Assertions.** FluentAssertions 8+ moved to the Xceed licence, which
   requires a paid commercial licence for company use.

## Decision

- xUnit **2.9.x** with `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`
  (the VSTest path `dotnet test` supports natively). Common settings live in
  `build/Tests.props`.
- **Shouldly** (BSD-3-Clause) for assertions; NSubstitute for mocking.
- Integration tests run against a **real, locally installed PostgreSQL** (and
  Redis for the Admin API suite), reached through
  `ENDPOINTPLATFORM_TEST_POSTGRES` / `ENDPOINTPLATFORM_TEST_REDIS`. Each
  fixture creates a throwaway database and drops it afterwards.

## Amendment (2026-09-20): Testcontainers removed

The integration suites originally started PostgreSQL and Redis with
**Testcontainers**, which requires a Docker daemon. Docker was removed from the
project entirely — development, tests and deployment now use natively installed
services — so the fixtures connect to a local server instead.

The property that mattered is unchanged: these tests run against a real
PostgreSQL, because triggers, `jsonb`, `inet`, partial indexes, role privileges
and up/down migrations do not exist in an in-memory or SQLite provider. What is
lost is the pinned image tag: the server version is now whatever the developer
or CI has installed, so docs/development.md states the expected version (17.x)
rather than enforcing it.

## Consequences

- No xUnit v3 features (TestContext, MTP-native runs). Acceptable; nothing in
  the suite needs them.
- Revisit once the SDK/xunit.v3 handshake stabilises — the migration is
  mechanical (namespaces and csproj properties).
- No licence exposure from FluentAssertions.
