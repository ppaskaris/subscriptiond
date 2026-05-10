# Task 006b: Add Expiration Purger Port

Status: Not Started

Depends On: 001a_create_app_clock, 001b_create_domain_models

## Goal

Move existing SQL cleanup behavior behind a provider-neutral expiration purger so SQL can delete expired data while Cosmos later no-ops in favor of TTL.

## Scope

- Add `IExpirationPurger`.
- Implement SQL expiration purger for expired lists, expired share links, and expired/orphan channel cleanup that exists at this stage.
- Move existing cleanup service/repository calls behind the purger without changing worker scheduling yet.

## Out Of Scope

- Worker state.
- Cosmos expiration purger.
- Unified worker rewrite.

## Validation

- Unit tests where useful.
- LocalDB integration tests for SQL purge behavior.

## Implementation Summary

Not completed.
