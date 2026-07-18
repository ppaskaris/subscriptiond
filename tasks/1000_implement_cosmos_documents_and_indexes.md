# Task 011: Implement Cosmos Documents And Indexes

Status: Completed

Depends On: 0900_prepare_cosmos_emulator_test_harness

## Goal

Create Cosmos document DTOs, container initialization, TTL configuration, and indexing policies.

## Scope

- Add Cosmos document DTOs.
- Add mapping helpers between documents and domain.
- Add container names/options.
- Configure TTL behavior for lists, share links, and orphan channels.
- Configure narrowed indexing policies.
- Add provider-specific tests for document serialization, TTL fields, and indexing policy creation.

## Out Of Scope

- Full repository implementations.

## Validation

- Cosmos emulator tests for container setup and document round trips.

## Implementation Summary

Added provider-private Cosmos document DTOs for lists, projected and canonical
channels, videos, share links, and worker state. Added mapping helpers that keep
Cosmos shapes out of domain interfaces, serialize enum values explicitly, and
calculate per-item TTL values for lists, share links, and orphan channels.

Added `CosmosOptions`, standard container names, and a reusable container
initializer. All containers use `/id` partition keys; list, channel, and share
link containers enable item TTL, while their indexing policies exclude embedded
projection/video data or include only query fields. The system container has a
minimal id-only index.

Updated the Cosmos emulator fixture to create containers through the production
initializer. Added mapper/TTL serialization tests and an opt-in emulator test
that verifies the created TTL and indexing policies.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 130 passed,
  2 Cosmos tests skipped because the opt-in environment variable was not set.
- `git diff --check`

Follow-up emulator validation found and corrected two policy compatibility
issues: broad indexing now explicitly includes the mandatory `/*` root path,
and the system container relies on Cosmos's built-in `id` index instead of
attempting to override the system property. The emulator may add exclusions for
system paths such as `_etag`, so integration assertions allow those normalized
paths.

Follow-up validation passed:

- `dotnet test youtubed.sln --no-build --filter "Category=Cosmos"` with
  `YOUTUBED_RUN_COSMOS_TESTS=true`: 6 passed, 0 skipped.
