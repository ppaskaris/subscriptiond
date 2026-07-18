# Task 013a: Implement Cosmos Channel Repository

Status: Completed

Depends On: 1000_implement_cosmos_documents_and_indexes, 1100_implement_cosmos_list_repository

## Goal

Implement Cosmos canonical channel storage, stale lookahead, and reverse-reference handling.

## Scope

- Implement canonical channel point reads and writes.
- Implement stale lookahead query with lightweight results.
- Implement batch point reads.
- Implement subscription list id updates with ETag retry.
- Maintain `subscriptionCount` with `subscribedListIds`.
- Implement orphan TTL setup/clearing.
- Repair dead list references when repository operations discover them.

## Out Of Scope

- List projection update repository.
- Worker state store.
- Data migration tooling.

## Validation

- Cosmos channel contract tests pass.
- Conflict retry tests for channel writes pass.

## Implementation Summary

Added `CosmosChannelRepository` with canonical point reads, discovery and refresh
writes, lightweight stale lookahead/next-refresh queries, and batch point reads.
Canonical writes preserve concurrent reverse-reference state, cap refreshed
videos at 100, and make two total ETag-protected attempts.

Cosmos list membership and deletion now reconcile canonical
`subscribedListIds` and `subscriptionCount`. Reconciliation validates list point
reads, removes missing or inconsistent references, clears orphan TTL when a
valid subscription exists, and starts the seven-day orphan TTL when none remain.
Batch channel reads also repair dead references they discover.

Added the ascending `/staleAfter`, `/id` composite index required for stable
stale lookahead ordering, real Cosmos channel provider contract coverage, and
unit coverage for channel conflict retry, dead-reference cleanup, and orphan
TTL setup.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 153 passed,
  15 Cosmos tests skipped because the opt-in environment variable was not set.
- `dotnet test youtubed.sln --no-build --filter "Category=Cosmos"` with
  `YOUTUBED_RUN_COSMOS_TESTS=true`: 15 passed, 0 skipped.
- `dotnet test youtubed.sln --no-build --filter "Category=LocalDb"` with
  `YOUTUBED_RUN_LOCALDB_TESTS=true`: 76 passed, 0 skipped.
- `git diff --check`

Follow-up review fixes make batch point reads recheck active, subscribed, and due
eligibility after reverse-reference repair. List deletion now uses the read
document's ETag and re-reads once after a conflict, so reverse-reference cleanup
uses the exact document version that was deleted. Regression tests cover each
batch ineligibility transition and a channel added concurrently with deletion.
