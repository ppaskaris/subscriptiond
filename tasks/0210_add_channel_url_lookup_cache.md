# Task 003b: Add Channel URL Lookup Cache

Status: Not Started

Depends On: 0110_create_app_clock, 0120_create_domain_models

## Goal

Replace durable channel URL lookup as a domain concept with a bounded in-memory cache around YouTube URL resolution.

## Scope

- Add bounded in-memory URL-to-channel-id cache.
- Make YouTube channel id the canonical lookup identity for submitted URLs.
- Stop relying on durable URL uniqueness for correctness.
- Keep stored channel URL as display metadata.

## Out Of Scope

- Channel status schema changes.
- Worker rewrite.
- Cosmos implementation.

## Validation

- Unit tests for URL cache behavior.
- Existing channel add/discovery tests pass.

## Implementation Summary

Not completed.
