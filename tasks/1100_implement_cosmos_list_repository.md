# Task 012a: Implement Cosmos List Repository

Status: Completed

Depends On: 1000_implement_cosmos_documents_and_indexes

## Goal

Implement Cosmos list behavior behind the provider-neutral ports.

## Scope

- Implement list point reads.
- Implement authenticated access with once-per-day renewal and TTL updates.
- Implement list membership add/remove with ETag retry.
- Implement list channel and hierarchical list video read models from embedded channels.

## Out Of Scope

- Share link repository.
- Channel repository.
- Worker state.
- Projection update repository.

## Validation

- Cosmos contract tests for list behavior pass.

## Implementation Summary

Added `CosmosListRepository` behind the existing provider-neutral list port. List
creation, reads, settings updates, deletion, channel/video projections, and
membership changes use Cosmos point reads and preserve Cosmos documents inside
the provider layer. Embedded channels are reshaped into the existing hierarchical
domain read models, including the global video render limit.

Daily renewal now updates `expiredAfter`, `expirationRenewedOn`, and list TTL in
one ETag-protected replacement. Membership and other replacement writes make two
total attempts, re-reading and reapplying after one optimistic-concurrency
conflict. Adds seed the embedded projection from a canonical channel point read;
duplicate adds and removes remain idempotent. Cosmos expiration removal is a
no-op because container TTL owns physical cleanup.

Added opt-in Cosmos list provider contract coverage plus non-emulator unit tests
for hierarchical projection mapping and ETag conflict retry.

Follow-up review fixes make every Cosmos list replacement recompute relative TTL
from the absolute `ExpiredAfter` value, preventing settings, membership, or
projection writes from extending list lifetime. List view services now return a
missing result safely if TTL cleanup or concurrent deletion removes a list
between its initial read and the projection point read.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 137 passed,
  6 Cosmos tests skipped because the opt-in environment variable was not set.
- `dotnet test youtubed.sln --no-build --filter "Category=Cosmos"` with
  `YOUTUBED_RUN_COSMOS_TESTS=true`: 6 passed, 0 skipped, including all four
  Cosmos list provider contracts.
