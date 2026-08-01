# Task 023: Complete Storage-Agnostic Repository Boundaries

Status: Completed

Depends On: 2200_make_authenticated_cosmos_list_render_single_read

## Goal

Finish the provider-neutral architecture so persistence ports expose domain/use-case types rather than MVC models or obsolete SQL-shaped records.

## Scope

- Change list, channel, share-link, and authenticated projection ports to use domain or explicit use-case types.
- Move web-specific token encoding and view concerns out of persistence-facing models.
- Remove or internalize obsolete SQL-only repository ports and row types, including the unused channel-video port where safe.
- Keep SQL row DTOs and Cosmos document DTOs private to their provider implementations.
- Update services, provider fixtures, shared contracts, and dependency injection without changing controller route templates or user-visible behavior.
- Remove duplicate mapping logic where an existing provider-neutral mapper can be reused safely.

## Out Of Scope

- Redesigning the UI.
- Changing SQL normalization or Cosmos denormalization.
- Adding accounts or authentication.

## Validation

- Architecture tests or compile-time project/namespace checks prevent persistence interfaces from referencing `youtubed.Models`, SQL rows, or Cosmos documents.
- Shared provider contracts exercise the new domain/use-case ports against SQL and Cosmos.
- Controller and service tests prove token, route, projection, and share-link behavior is unchanged.
- Build and full sequential non-provider, LocalDB, and opted-in Cosmos suites pass.
- `dotnet format --verify-no-changes` and `git diff --check` pass.

## Implementation Summary

Changed the list, channel, and share-link repositories to expose domain objects
instead of MVC models. The authenticated list projection port now accepts the
decoded token bytes, so base64url decoding remains in `ListService` and both
providers perform constant-time comparisons without depending on ASP.NET web
encoding. Services map domain objects to the existing MVC models, preserving
controller contracts, route templates, and user-visible behavior.

Internalized the Cosmos document DTOs and mapper while retaining test access
through friend assemblies. Removed the unused SQL-only channel-video repository,
row, service, dependency-injection registration, and duplicate integration tests;
the unified channel refresh pipeline remains the single video persistence path.
Also removed unused legacy channel metadata/status repository operations and
reused `CosmosDocumentMapper.ToShareLink` instead of maintaining a second mapping.

Added architecture tests that recursively inspect every persistence-port method
and reject references to MVC models, provider documents, or row types. The tests
also require Cosmos document DTOs and the mapper to remain non-public and prevent
the obsolete channel-video port/row from returning. Updated shared SQL and Cosmos
provider contracts, fixtures, direct repository tests, and service tests to use
the new domain boundaries. Incremented `AssemblyVersion` from `2.21.0.0` to
`2.22.0.0` for the backward-compatible internal architecture improvement.

Validation passed sequentially on 2026-08-01:

- `dotnet build youtubed.sln`: passed with 0 warnings and 0 errors;
- tests excluding LocalDB and Cosmos: 196 passed, 0 failed, 0 skipped;
- opted-in LocalDB tests with `YOUTUBED_RUN_LOCALDB_TESTS=true`: 71 passed,
  0 failed, 0 skipped;
- opted-in Cosmos emulator tests with `YOUTUBED_RUN_COSMOS_TESTS=true`: 68
  passed, 0 failed, 0 skipped;
- `dotnet format youtubed.sln --verify-no-changes`: passed;
- `git diff --check`: passed.
