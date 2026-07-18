# Task 023: Complete Storage-Agnostic Repository Boundaries

Status: Not Started

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

Not implemented.
