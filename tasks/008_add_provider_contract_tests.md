# Task 008: Add Provider Contract Tests

Status: Not Started

Depends On: 003_add_channel_status_and_url_lookup_cache, 004_refactor_list_read_models, 005_add_daily_authenticated_list_renewal, 006_add_worker_state_and_expiration_purger_ports

## Goal

Create shared contract tests that SQL passes first and Cosmos must pass later.

## Scope

- Define provider fixture abstraction for contract tests.
- Add list repository contract tests.
- Add channel repository contract tests.
- Add share link repository contract tests.
- Add worker state contract tests.
- Add projection update contract tests where SQL no-op behavior is acceptable.

## Out Of Scope

- Cosmos provider implementation.
- Cosmos emulator fixture.

## Validation

- SQL contract tests pass under LocalDB opt-in.

## Implementation Summary

Not completed.
