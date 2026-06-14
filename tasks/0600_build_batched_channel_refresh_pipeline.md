# Task 007a: Build Batched Channel Refresh Pipeline

Status: Completed

Depends On: 0200_add_channel_status, 0210_add_channel_url_lookup_cache, 0300_refactor_list_read_models

## Goal

Create the provider-agnostic channel refresh pipeline that can process a bounded batch of stale channels and persist completed YouTube results without yet replacing the hosted worker loop.

## Scope

- Query stale channels in bounded lookahead.
- Point-read or load the selected batch before processing.
- Process channel refreshes in batches of 10.
- Bulk fetch channel metadata before playlist fetches.
- Fetch playlist items per channel.
- Add 5 second delay between YouTube API calls.
- Bulk fetch video durations across the batch.
- Stop starting new YouTube calls when cancellation is requested.
- Persist already-fetched YouTube results before returning.
- Persist canonical channel documents before projection updates.
- Keep SQL projection update port as no-op.

## Out Of Scope

- Replacing the hosted worker loop.
- Worker state scheduling.
- Expiration purging.
- Removing SQL `VisibleAfter`.
- Cosmos provider.

## Validation

- Unit tests for batch selection, cancellation behavior, and persistence ordering.
- LocalDB integration tests for stale channel refresh persistence.

## Implementation Summary

Added a provider-agnostic `ChannelRefreshPipeline` registered in DI without replacing the existing hosted worker loop. The pipeline queries a bounded stale-channel lookahead, loads the first batch of 10 full channel documents, bulk-fetches YouTube channel metadata, fetches playlist items per channel with the configured 5 second inter-call delay, bulk-fetches video durations for the batch, and persists any completed results before updating projections.

Extended `IChannelRepository` with stale lookahead, batch load, and refresh-result save methods. The SQL implementation keeps `VisibleAfter` filtering while it still exists, loads channel subscriptions and current videos into storage-agnostic domain objects, and saves canonical channel metadata/status before replacing refreshed video rows. Added `IListProjectionRepository` with a SQL no-op implementation because SQL read models continue to be computed from joins.

Split the YouTube service contract so existing callers can keep using `GetVideosAsync`, while the new pipeline can call bulk channel metadata, playlist page fetches, and single-request duration chunk fetches. The pipeline controls playlist paging and 50-video duration chunking so cancellation checks and the 5 second inter-call delay apply before every actual YouTube API call.

Cancellation during the YouTube phase stops starting additional YouTube calls but still persists already-fetched metadata or completed video refreshes using a non-cancelled finalization token. Cancellation from an in-flight YouTube call is caught inside the pipeline so previously completed results still reach finalization, and duration chunk cancellation finalizes channels whose playlist videos were fully covered by earlier duration chunks.

Metadata lookup and playlist refresh are separable: missing metadata no longer marks a channel unavailable when the stored playlist id is still usable. The pipeline falls back to the stored playlist id, persists a video refresh only after playlist and duration calls complete, and marks a channel unavailable only when metadata is missing and no stored playlist fallback exists.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"` (120 passed)
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build` (176 passed)
