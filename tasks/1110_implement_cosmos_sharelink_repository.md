# Task 012b: Implement Cosmos ShareLink Repository

Status: Completed

Depends On: 1000_implement_cosmos_documents_and_indexes, 1100_implement_cosmos_list_repository

## Goal

Implement Cosmos share-link behavior behind the provider-neutral ports.

## Scope

- Implement share link create with password id conflicts handled.
- Implement share link list-by-list query.
- Implement share link consume by password.
- Verify target list exists before marking a share link used.
- Use ETag on share-link consume.
- Implement scoped delete and delete-all operations.

## Out Of Scope

- List membership behavior.
- Channel repository.
- Worker state.

## Validation

- Cosmos contract tests for share-link behavior pass.

## Implementation Summary

Added `CosmosShareLinkRepository` behind the existing provider-neutral share-link
port. Creates use password as the document and partition id and report Cosmos id
conflicts to the service's existing password-generation retry loop. List-scoped
queries use the indexed `listId` path and sort the low-volume results in memory,
avoiding a composite index solely for share management.

Consume point-reads the share link and target list before marking the link used,
recomputes retention TTL from the absolute expiration, and replaces with the
share-link ETag. An optimistic-concurrency conflict causes one re-read and retry;
a second conflict is thrown. Scoped deletes validate list ownership and use an
ETag, while delete-all enumerates only links belonging to the requested list.

Added Cosmos provider contracts for create/list, consume, scoped/delete-all, and
missing target-list behavior. Added a provider-specific unit test proving consume
re-reads the target list and reapplies the update after one ETag conflict, plus a
test proving a second ETag conflict is propagated.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 138 passed,
  10 Cosmos tests skipped because that run did not set the opt-in environment
  variable.
- `dotnet test youtubed.sln --no-build --filter "Category=Cosmos"` with
  `YOUTUBED_RUN_COSMOS_TESTS=true`: 10 passed, 0 skipped.
- `dotnet test youtubed.sln --no-build --filter
  "FullyQualifiedName~CosmosShareLinkRepositoryTests"`: 2 passed.
- `git diff --check`
