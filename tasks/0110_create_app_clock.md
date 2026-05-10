# Task 001a: Create App Clock And Replace Ambient Time

Status: Not Started

Depends On: 0100_document_target_architecture

## Goal

Introduce `IAppClock` and use it everywhere application code currently depends on ambient system time or randomized scheduling delays.

## Scope

- Add `IAppClock` with `UtcNow`, `UtcToday`, `RandomDelay`, and `UtcNowAfterRandomDelay`.
- Add a production implementation.
- Register `IAppClock` in dependency injection.
- Replace service-layer and repository-call-site uses of `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, and `Constants.RandomlyBetween` where they are part of app behavior.
- Move new/changed timestamp behavior to UTC.
- Update tests to use a fake clock where deterministic timestamp behavior is asserted.

## Out Of Scope

- Domain model refactor beyond what is needed to inject and pass clock values.
- Cosmos implementation.
- Worker rewrite.
- SQL schema changes.

## Validation

- Run unit tests.
- Run LocalDB tests if SQL-facing timestamp behavior changes.

## Implementation Summary

Not completed.
