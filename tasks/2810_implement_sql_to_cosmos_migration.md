# Task 028a: Implement SQL-To-Cosmos Migration

Status: Not Started

Depends On: 2800_design_sql_to_cosmos_migration_and_cutover

## Goal

Implement the designed production migration as a safe, resumable, secret-conscious command that writes final Cosmos document shapes.

## Scope

- Add a dedicated SQL-to-Cosmos migration command with explicit source and target configuration.
- Support validation-only/dry-run, full import, resume from durable checkpoints, and controlled retry of poison records.
- Read SQL in bounded batches and write Cosmos with bounded concurrency and RU-aware backoff.
- Reuse production mappers/projection-sizing utilities so migrated and newly written documents have identical shapes and invariants.
- Preserve list tokens and unconsumed share-link semantics without printing secrets.
- Calculate TTL from absolute timestamps at write time and skip or report data that is already beyond retention according to the migration design.
- Make writes idempotent and ETag-safe so reruns cannot corrupt target changes.
- Emit progress and reconciliation identifiers without exposing sensitive payloads.

## Out Of Scope

- Switching production configuration.
- Deleting or mutating the SQL source.
- Treating dry-run output as a repository of plaintext secret values.

## Validation

- Unit tests cover mapping, expiry boundaries, idempotent rerun, checkpoint resume, poison records, cancellation, and secret-safe output.
- LocalDB-to-emulator integration tests migrate representative complete data and compare domain-visible behavior through provider ports.
- An interrupted migration resumes without duplicates, missing memberships, TTL extension, or share-link reuse.
- Dry-run and validation-only modes make no target mutations.
- Full mandatory CI passes with LocalDB and Cosmos suites unskipped.

## Implementation Summary

Not implemented.
