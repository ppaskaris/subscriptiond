# Task 003b: Add Channel URL Lookup Cache

Status: Completed

Depends On: 0110_create_app_clock, 0120_create_domain_models

## Goal

Replace durable channel URL lookup as a domain concept with a bounded in-memory cache around YouTube URL resolution.

## Scope

- Add bounded in-memory URL-to-channel-id cache.
- Make YouTube channel id the canonical lookup identity for submitted URLs.
- Stop relying on durable URL uniqueness for correctness.
- Keep stored channel URL as display metadata.

## Out Of Scope

- Channel status schema changes.
- Worker rewrite.
- Cosmos implementation.

## Validation

- Unit tests for URL cache behavior.
- Existing channel add/discovery tests pass.

## Implementation Summary

Added a singleton bounded `IMemoryCache`-backed channel URL lookup cache with a 24 hour entry duration and 1000 entry size limit. Channel submission now resolves the submitted URL through YouTube once, caches the submitted URL to YouTube channel id mapping, caches failed lookups as null results, and uses the channel id as the canonical persistence identity on cache hits.

Channel discovery now stores the canonical channel-id URL as display metadata and saves discovered YouTube channels by id only. Cache hits return the existing channel after a single id lookup and successful YouTube resolutions return the resolved model without an extra post-save fetch. SQL no longer treats `Channel.Url` as a durable lookup key: `IChannelRepository` exposes id lookup, `SaveDiscoveredChannelAsync` matches by `Id`, `Schema.sql` no longer creates `UK_Channel_Url`, and `20260516_DropChannelUrlUniqueConstraint.sql` drops the old unique constraint for existing databases.

Added cache unit tests for hit, expiration, bounded size behavior, and cached null lookup results. Updated channel repository and service integration tests to assert id-based discovery and non-unique display URL behavior.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"` (100 passed)
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build` (143 passed)
