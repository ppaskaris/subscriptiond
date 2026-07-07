# Task 008a: Add Provider Contract Test Harness

Status: Completed

Depends On: 0300_refactor_list_read_models, 0400_add_daily_authenticated_list_renewal, 0500_add_worker_state_port, 0510_add_expiration_purger_port

## Goal

Create the shared contract-test infrastructure that SQL can use first and Cosmos can use later.

## Scope

- Define provider fixture abstraction for contract tests.
- Add SQL fixture implementation backed by LocalDB.
- Add shared test base/helpers for creating lists, channels, videos, share links, and worker state.
- Preserve LocalDB opt-in behavior.

## Out Of Scope

- Writing every provider contract suite.
- Cosmos provider implementation.
- Cosmos emulator fixture.

## Validation

- Harness compiles.
- A minimal SQL-backed smoke contract test passes under LocalDB opt-in.

## Implementation Summary

Added shared provider contract-test infrastructure under `youtubed.Tests/ProviderContracts`.

The harness defines a provider fixture abstraction, a provider context exposing the current repository ports, a SQL provider fixture backed by the existing LocalDB fixture, and a shared base class with helpers for creating lists, channels, channel videos, share links, worker state, projection access, and purger access through domain-visible provider interfaces.

Added a minimal SQL-backed smoke contract test that preserves LocalDB opt-in behavior with `LocalDbFact` and the existing `LocalDb` collection.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 123 passed
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: 177 passed
