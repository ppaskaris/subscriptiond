# Task 013b: Implement Cosmos Projection Repository

Status: Completed

Depends On: 1100_implement_cosmos_list_repository, 1200_implement_cosmos_channel_repository

## Goal

Implement Cosmos list projection updates for refreshed channels.

## Scope

- Implement projection update by replacing only refreshed channel subdocuments in affected lists.
- Use ETag retry for list projection writes.
- Repair dead list references discovered during projection.
- Keep updates scoped to channels processed in the current batch.

## Out Of Scope

- Canonical channel stale lookahead.
- Share link repository.
- Worker state store.

## Validation

- Cosmos projection contract tests pass.
- Conflict retry tests for list projection writes pass.

## Implementation Summary

Added `CosmosListProjectionRepository` to update only refreshed embedded channel
documents. Refreshed channels are grouped by affected list so each list is point
read and replaced once per batch. Replacements preserve unrelated channel
subdocuments, recompute list TTL from its absolute expiration, use the document
ETag, and re-read/reapply once after a precondition failure before throwing.

Projection reads now detect missing lists and lists that no longer contain an
expected channel. The repository repairs only those confirmed dead references
in the canonical channel document, keeps `subscribedListIds` and
`subscriptionCount` consistent, and applies the seven-day orphan TTL when the
last valid reference is removed. Repair writes are also ETag protected.

Added focused unit coverage for batch-scoped replacement, concurrent list-change
preservation, the two-attempt conflict limit, and dead-reference/orphan repair.
Added the shared Cosmos projection provider contract while retaining the seeded
channel behavior needed until task 1200 supplies the production Cosmos channel
repository.

Review follow-up removes malformed `subscribedListIds` during repair so they do
not count as live subscriptions or suppress orphan TTL. Candidate dead list
references are now point-read again immediately before every channel write
attempt, including the retry after a channel ETag conflict, so a concurrently
re-added membership observed during repair is retained.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 145 passed,
  11 Cosmos tests skipped because the opt-in environment variable was not set.
- `dotnet test youtubed.sln --no-build --filter "Category=Cosmos"` with
  `YOUTUBED_RUN_COSMOS_TESTS=true`: 11 passed, 0 skipped, including the Cosmos
  projection provider contract.
