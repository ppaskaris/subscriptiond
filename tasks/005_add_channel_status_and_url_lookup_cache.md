# Task 005: Add Channel Status And URL Lookup Cache

Status: Not Started

Depends On: 002_create_domain_time_abstractions

## Goal

Add channel availability status and replace durable URL lookup with an in-memory lookup cache around YouTube URL resolution.

## Scope

- Add channel status fields to SQL schema and `Schema.sql`.
- Add domain enums for status and status reason.
- Update SQL mapping and projection reads to include channel status.
- Add bounded in-memory URL-to-channel-id cache.
- Make YouTube channel id the canonical identity.
- Allow metadata refresh to update URL and playlist id.

## Out Of Scope

- Full worker rewrite.
- Permanent failure detection if it requires broader YouTube service changes; this can be staged if needed.

## Validation

- Unit tests for URL cache behavior.
- LocalDB integration tests for status persistence.

## Implementation Summary

Not completed.
