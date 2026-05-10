# Task 007c: Remove SQL VisibleAfter

Status: Not Started

Depends On: 007b_rebuild_unified_worker_on_sql

## Goal

Remove the old SQL multi-worker lease column and claim behavior after the unified single-worker model no longer uses it.

## Scope

- Remove `VisibleAfter` reads and writes from SQL repositories.
- Drop `VisibleAfter` from `Schema.sql`.
- Add SQL migration to drop `VisibleAfter`.
- Remove or replace tests that assert old lease/claim behavior.

## Out Of Scope

- Further worker behavior changes.
- Cosmos provider.

## Validation

- Unit tests.
- LocalDB integration tests because SQL schema and stale-channel behavior change.

## Implementation Summary

Not completed.
