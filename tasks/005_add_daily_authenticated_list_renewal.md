# Task 005: Add Daily Authenticated List Renewal

Status: Not Started

Depends On: 001a_create_app_clock, 001b_create_domain_models, 004_refactor_list_read_models

## Goal

Renew list expiration at most once per UTC day on authenticated list access, and never from maintenance or projection reads.

## Scope

- Add `ExpirationRenewedOn` to domain.
- Add SQL schema migration and `Schema.sql` update.
- Add authenticated list access method that validates token and renews once per day.
- Update controllers to use authenticated access methods.
- Ensure maintenance/projection reads do not renew expiration.

## Out Of Scope

- Cosmos TTL implementation.
- Provider selection.

## Validation

- Unit tests with fake `IAppClock`.
- LocalDB integration tests for renewal behavior.

## Implementation Summary

Not completed.
