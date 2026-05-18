# Task 004: Refactor List Read Models

Status: Completed

Depends On: 0110_create_app_clock, 0120_create_domain_models, 0200_add_channel_status

## Goal

Make list views and domain services consume use-case read models that hide SQL normalization and Cosmos denormalization.

## Scope

- Add `ListChannelProjection` for channel management.
- Add hierarchical `ListVideoProjection` for the main list page with channels containing nested videos.
- Keep `StaleCount`, but compute it from channel context in the read model rather than storing it as a separate aggregate.
- Remove exact total video count UI.
- Keep main list page render capped at 100 videos.
- Use a stable `Now` on the view model.
- Change meta refresh to a fixed 15 seconds on the list page only.
- Ensure SQL builds each read model with selective joins.

## Out Of Scope

- Stored projections.
- Cosmos provider.
- Worker rewrite.

## Validation

- Run unit tests.
- Run LocalDB integration tests because list SQL projection behavior changes.

## Implementation Summary

Refactored list rendering around storage-agnostic domain projections while preserving current SQL-backed behavior and anonymous secret-link routes.

`IListRepository` now returns `ListVideoProjection` for the main list page and `ListChannelProjection` for channel management. SQL builds the video projection with selective list/channel reads plus a capped newest-video query, and builds the channel projection without joining `ChannelVideo`.

`ListService` now maps projections into MVC view models, computes `StaleCount` from projected active channels and stable `Now`, caps rendered videos at 100, and exposes `HasMoreVideos` for count-free UI copy. The list page now uses a fixed 15 second meta refresh only when channels are stale, and the exact total-video-count message was removed.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"` (102 passed)
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build` (148 passed)
