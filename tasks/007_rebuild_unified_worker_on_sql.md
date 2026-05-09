# Task 007: Rebuild Unified Worker On SQL

Status: Not Started

Depends On: 003_refactor_list_projection_behavior, 005_add_channel_status_and_url_lookup_cache, 006_add_worker_state_and_expiration_purger_ports

## Goal

Replace the two existing hosted services with one provider-agnostic worker while still using SQL as the backing provider.

## Scope

- Merge channel refresh and maintenance loops into one worker.
- Use worker state for `NextChannelRefreshAt` and `NextPurgeAt`.
- Use fixed purge interval.
- Query stale channels in bounded lookahead.
- Process channel refreshes in batches of 10.
- Add 5 second delay between YouTube API calls.
- Stop starting new YouTube calls when cancellation is requested.
- Persist already-fetched YouTube results before shutdown.
- Persist canonical channel documents before projection updates.
- SQL projection update port remains no-op.
- Remove SQL `VisibleAfter` usage and then drop column through migration and `Schema.sql`.

## Out Of Scope

- Cosmos provider.
- Advanced YouTube quota limiter beyond bounded batches and fixed delay.

## Validation

- Unit tests for worker state transitions and cancellation flow.
- LocalDB integration tests for stale channel selection and refresh persistence.
- Manual review of logs for worker cadence.

## Implementation Summary

Not completed.
