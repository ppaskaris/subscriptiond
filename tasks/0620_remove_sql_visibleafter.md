# Task 007c: Remove SQL VisibleAfter

Status: Completed

Depends On: 0610_rebuild_unified_worker_on_sql

## Goal

Remove the old SQL multi-worker lease column and claim behavior after the unified single-worker model no longer uses it.

## Scope

- Remove `VisibleAfter` reads and writes from SQL repositories.
- Drop `VisibleAfter` from `Schema.sql`.
- Add SQL migration to drop `VisibleAfter`.
- Remove or replace tests that assert old lease/claim behavior.

## Out Of Scope

- Further worker behavior changes.
- Cosmos provider.

## Validation

- Unit tests.
- LocalDB integration tests because SQL schema and stale-channel behavior change.

## Implementation Summary

Removed SQL `VisibleAfter` from the active schema, added an idempotent drop migration, removed SQL reads/writes/filters for the old lease field, and simplified stale-channel refresh to select the first configured batch from the stale lookahead. Removed the obsolete single-channel refresh hosted-service path and its service methods/tests so no unleased legacy loop remains.

Updated data-transfer columns, SQL fixtures, repository/pipeline tests, and design docs to reflect the post-lease unified-worker model. Orphan channel purging now deletes orphan rows without a `VisibleAfter` gate. Bumped `AssemblyVersion` to `2.12.1.0`.

Follow-up review fix: metadata-only refreshes that successfully update channel metadata but still have no upload playlist now advance `StaleAfter` using the normal channel refresh delay, preventing immediate reselection after the SQL lease field was removed. Bumped `AssemblyVersion` to `2.12.1.1`.

Second follow-up review fix: metadata refresh now normalizes missing YouTube upload playlist IDs to an empty string before persistence so SQL's non-null `Channel.PlaylistId` column can be updated safely. Added LocalDB coverage for metadata-present/no-playlist refresh persistence. Bumped `AssemblyVersion` to `2.12.1.2`.

Validation:

- `dotnet build youtubed.sln`: passed.
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: passed, 123 tests.
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: passed, 176 tests.
