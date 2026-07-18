# Task 010: Prepare Cosmos Emulator Test Harness

Status: Completed

Depends On: 0800_add_persistence_provider_selection

## Goal

Add opt-in Cosmos emulator test infrastructure before implementing the full Cosmos provider.

## Scope

- Add Cosmos test category.
- Add `YOUTUBED_RUN_COSMOS_TESTS=true` gate.
- Add emulator connection options.
- Add fixture that creates isolated test database/containers.
- Add basic smoke test for creating containers and writing a system document.

## Out Of Scope

- Full Cosmos provider behavior.
- Production Azure deployment configuration.

## Validation

- Cosmos tests skip by default.
- Cosmos smoke test passes when emulator is running and opt-in variable is set.

## Implementation Summary

Added the Azure Cosmos DB SDK to the test project and an opt-in `CosmosFact`
gate controlled by `YOUTUBED_RUN_COSMOS_TESTS`. Cosmos tests are categorized as
`Cosmos` and skip without initializing the fixture when the gate is disabled.

Added emulator connection options that use the standard local emulator connection
string by default and allow an override through
`YOUTUBED_COSMOS_EMULATOR_CONNECTION_STRING`.

Added a serialized Cosmos fixture that creates a unique database per test run,
creates isolated `lists`, `channels`, `shareLinks`, and `system` containers with
`/id` partition keys, and deletes the database with best-effort cleanup. The client
uses gateway mode and camel-case JSON serialization.

Added a smoke test that writes and point-reads a system document from the `system`
container.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category=Cosmos"` with the opt-in
  variable unset: 1 skipped, 0 failed
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 126 passed,
  1 Cosmos test skipped, 0 failed
- `git diff --check`

The opted-in Cosmos smoke test could not be run because no Cosmos emulator was
installed or listening on port 8081 in this environment.
