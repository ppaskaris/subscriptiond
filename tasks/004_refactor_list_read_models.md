# Task 004: Refactor List Read Models

Status: Not Started

Depends On: 001a_create_app_clock, 001b_create_domain_models, 003a_add_channel_status

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

Not completed.
