# Task 001b: Create Domain Models

Status: Completed

Depends On: 0100_document_target_architecture

## Goal

Introduce storage-agnostic domain objects so services and repository interfaces can move away from Dapper-shaped models before provider-specific SQL and Cosmos implementations diverge.

## Scope

- Add `Domain` types for subscription list, list channel projection, list video projection, channel, channel video, share link, worker state, channel status, and status reason.
- Keep domain read models storage-agnostic and use-case shaped.
- Model `ListVideoProjection` as a hierarchy of channels with nested videos.
- Leave SQL rows and future Cosmos documents in provider-specific persistence layers.
- Add focused unit tests for simple domain behavior if useful.

## Out Of Scope

- Rewriting repositories to return the new domain types.
- SQL schema changes.
- Cosmos implementation.
- Worker rewrite.
- App-wide clock replacement.

## Validation

- Run unit tests.

## Implementation Summary

Added a new `youtubed.Domain` namespace with storage-agnostic domain objects for subscription lists, channels, channel videos, share links, consumed share links, worker state, channel status, and status reasons.

Added use-case-shaped list read models:

- `ListChannelProjection` for channel management without video rows.
- `ListVideoProjection` as a hierarchy of projected channels with nested `ChannelVideo` items for list rendering.

Projection-specific channel shapes are nested as `ListChannelProjection.Channel` and `ListVideoProjection.Channel` to keep the top-level domain namespace focused.

The new domain types are not wired into existing repositories yet, so SQL rows and MVC models remain unchanged for later refactor tasks.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build` (LocalDB opt-in tests skipped)
