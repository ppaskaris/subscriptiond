# Task 004: Add Daily Authenticated List Renewal

Status: Not Started

Depends On: 002_create_domain_time_abstractions

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
