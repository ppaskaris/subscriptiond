# Task 006: Add Worker State And Expiration Purger Ports

Status: Not Started

Depends On: 002_create_domain_time_abstractions

## Goal

Introduce provider-neutral worker state and expiration purger interfaces before rewriting the worker.

## Scope

- Add `IWorkerStateStore`.
- Add SQL `WorkerState` table to schema and migrations.
- Implement SQL worker state get-or-create, force refresh, complete channel pass, and complete purge.
- Add `IExpirationPurger`.
- Move existing SQL list/share-link/channel cleanup behind SQL expiration purger.

## Out Of Scope

- Cosmos worker state implementation.
- Worker rewrite.

## Validation

- Unit tests for worker state semantics.
- LocalDB integration tests for SQL worker state and purger.

## Implementation Summary

Not completed.
