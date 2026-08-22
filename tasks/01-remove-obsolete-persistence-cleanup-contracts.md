# Task 01: Remove obsolete persistence cleanup contracts

Implement this task as the first step in the architecture simplification sequence. The Cosmos migration replaced application-driven list and share-link cleanup with Cosmos TTL, and the current data model deliberately treats unreferenced channel documents as harmless cached data. Remove the persistence API surface that still implies those cleanup workflows exist.

## Required changes

- Remove `RemoveExpiredAsync` from `IListRepository` and every implementation, fake, mock setup, provider fixture, and contract test that exists only to support it.
- Remove `RemoveExpiredAsync` from `IShareLinkRepository` and every implementation, fake, mock setup, provider fixture, and contract test that exists only to support it.
- Remove `RemoveOrphanChannelsAsync` from `IChannelRepository` and every implementation, fake, mock setup, provider fixture, and contract test that exists only to support it.
- Remove production or test helpers that become unused as a direct result. Do not retain compatibility shims or no-op methods.
- Update relevant architecture or operations documentation if it refers to these methods or suggests that the application performs this cleanup.

## Constraints

- Preserve Cosmos TTL configuration and initialization for lists and share links.
- Do not add channel TTL or channel garbage collection. Unreferenced channel documents must remain accepted inert cache entries.
- Do not change controllers, routes, anonymous secret-link behavior, expiration durations, or visible lifecycle behavior.
- Keep domain models and repository interfaces storage-agnostic.

## Acceptance criteria

- No production repository interface exposes an application-driven expired-list, expired-share-link, or orphan-channel cleanup operation.
- No Cosmos repository contains a no-op cleanup method returning zero.
- No scheduled or hosted cleanup behavior is introduced.
- Existing TTL and lifecycle tests continue to describe the supported Cosmos behavior.
- Searches of tracked and non-ignored files show no stale references to the removed members except historical prose that is explicitly describing the former design.

## Validation

Run validation sequentially: build the solution, then run tests excluding Cosmos, then run the opted-in Cosmos emulator suite with `YOUTUBED_RUN_COSMOS_TESTS=true`. Report any unavailable or skipped validation accurately.
