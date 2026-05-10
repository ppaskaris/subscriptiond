# Task 006a: Add Worker State Port

Status: Not Started

Depends On: 0110_create_app_clock, 0120_create_domain_models

## Goal

Introduce provider-neutral worker state before rewriting the background worker.

## Scope

- Add `IWorkerStateStore`.
- Add SQL `WorkerState` table to schema and migrations.
- Implement SQL worker state get-or-create.
- Implement force channel refresh.
- Implement complete channel pass with observed-state protection.
- Implement complete purge.

## Out Of Scope

- Expiration purger.
- Cosmos worker state implementation.
- Worker rewrite.

## Validation

- Unit tests for worker state semantics.
- LocalDB integration tests for SQL worker state persistence.

## Implementation Summary

Not completed.
