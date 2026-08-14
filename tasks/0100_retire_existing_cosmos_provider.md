# Task 0100: Retire The Existing Cosmos Provider

Status: Not Started

Depends On: None

## Goal

Return the branch to a clean, working SQL-backed baseline while retaining provider selection and
the reusable domain boundary. Remove the current Cosmos implementation so none of its recovery or
projection behavior becomes an accidental compatibility requirement for the replacement.

## Scope

- Make explicit `Persistence:Provider=Cosmos` fail during registration with an actionable message
  that the simplified provider is being rebuilt.
- Delete the production `Persistence/Cosmos` implementation, including document types, repositories,
  projection sizing, recovery store/service, lifecycle logic, worker state, initializer, retry
  instrumentation, and recovery-specific exceptions.
- Delete Cosmos-specific unit, provider, integration, TTL, sizing, RU, telemetry, concurrency, and
  recovery tests tied to the retired document shapes.
- Remove obsolete Cosmos configuration from checked-in application settings.
- Remove the Cosmos SDK reference if no code remaining after cleanup uses it; Task 0300 will add the
  required version back with the new foundation.
- Preserve SQL repositories, domain models, provider-neutral contracts, MVC behavior, LocalDB tests,
  and provider contract infrastructure that still exercise SQL-visible behavior.
- Preserve the anonymous secret-link model and all controller route templates.
- Update architecture tests so they describe the temporarily SQL-only implementation without
  weakening storage-agnostic boundaries.

## Out Of Scope

- Simplifying the shared worker and persistence ports; Task 0200 owns that work.
- Implementing any replacement Cosmos document or repository.
- Reverting useful SQL or domain improvements merely because they were introduced on this branch.
- Modifying deployed Azure resources.

## Validation

- The solution builds with zero warnings and errors.
- Tests excluding LocalDB and Cosmos pass with no required skips.
- Opted-in LocalDB tests pass because provider registration and shared boundaries changed.
- Selecting Cosmos fails early with the documented temporary message.
- A tracked-file search finds no production recovery/lifecycle/edge/cursor/projection Cosmos types.
- `git diff --check` passes.

## Implementation Summary

Not implemented.
