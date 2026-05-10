# Task 008a: Add Provider Contract Test Harness

Status: Not Started

Depends On: 004_refactor_list_read_models, 005_add_daily_authenticated_list_renewal, 006a_add_worker_state_port, 006b_add_expiration_purger_port

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

Not completed.
