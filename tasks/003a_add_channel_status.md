# Task 003a: Add Channel Status

Status: Not Started

Depends On: 001b_create_domain_models

## Goal

Add channel availability status to the SQL-backed app so permanent YouTube failures can become visible domain state instead of invisible retry loops.

## Scope

- Add channel status fields to SQL schema and `Schema.sql`.
- Add migration for channel status fields.
- Update SQL mapping to include channel status and status reason.
- Update channel domain/service behavior to carry status values.
- Allow metadata refresh to update URL and playlist id when channel metadata is available.

## Out Of Scope

- In-memory URL lookup cache.
- Full worker rewrite.
- Cosmos implementation.
- Permanent failure detection if it requires broader YouTube service changes; this can be staged if needed.

## Validation

- Unit tests for status mapping/domain behavior.
- LocalDB integration tests for status persistence.

## Implementation Summary

Not completed.
