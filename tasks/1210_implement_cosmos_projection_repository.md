# Task 013b: Implement Cosmos Projection Repository

Status: Not Started

Depends On: 1100_implement_cosmos_list_repository, 1200_implement_cosmos_channel_repository

## Goal

Implement Cosmos list projection updates for refreshed channels.

## Scope

- Implement projection update by replacing only refreshed channel subdocuments in affected lists.
- Use ETag retry for list projection writes.
- Repair dead list references discovered during projection.
- Keep updates scoped to channels processed in the current batch.

## Out Of Scope

- Canonical channel stale lookahead.
- Share link repository.
- Worker state store.

## Validation

- Cosmos projection contract tests pass.
- Conflict retry tests for list projection writes pass.

## Implementation Summary

Not completed.
