# Task 007a: Build Batched Channel Refresh Pipeline

Status: Not Started

Depends On: 003a_add_channel_status, 003b_add_channel_url_lookup_cache, 004_refactor_list_read_models

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

Not completed.
