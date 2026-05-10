# Task 010: Prepare Cosmos Emulator Test Harness

Status: Not Started

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

Not completed.
