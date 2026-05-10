# Task 003a: Add Channel Status

Status: Completed

Depends On: 0120_create_domain_models

## Goal

Add channel availability status to the SQL-backed app so permanent YouTube failures can become visible domain state instead of invisible retry loops.

## Scope

- Add channel status fields to SQL schema and `Schema.sql`.
- Add migration for channel status fields.
- Update SQL mapping to include channel status and status reason.
- Update channel domain/service behavior to carry status values.
- Allow metadata refresh to update URL and playlist id when channel metadata is available.

## Out Of Scope

- In-memory URL lookup cache.
- Full worker rewrite.
- Cosmos implementation.
- Permanent failure detection if it requires broader YouTube service changes; this can be staged if needed.

## Validation

- Unit tests for status mapping/domain behavior.
- LocalDB integration tests for status persistence.

## Implementation Summary

Added SQL channel status persistence with numeric `Status`, numeric `StatusReason`, and `StatusUpdatedAt` fields in both `Schema.sql` and a rerunnable one-way migration from the pre-task schema. The numeric status fields reference `ChannelStatus` and `ChannelStatusReason` lookup tables, preserving Dapper enum auto-mapping while keeping database values readable through joins.

Extended channel models and SQL mappings to carry the domain `ChannelStatus` and `ChannelStatusReason` values. Stale channel claiming and list stale counts now include only active channels, while list channel data still includes unavailable channels for management visibility.

Metadata refresh now updates canonical channel URL, title, thumbnail, and playlist id when YouTube metadata is available. Successful metadata refresh clears unavailable status. A missing YouTube channel during refresh marks the channel `Unavailable` with reason `NotFound`, records the status timestamp, pushes `StaleAfter` far into the future, and skips video refresh.

Review follow-up fixes:

- Metadata refresh now uses the stored YouTube channel id through `IYoutubeService.GetChannelByIdAsync`, avoiding URL parsing entirely when refreshing known channels.
- Rediscovery now handles existing channels by either canonical id or submitted URL, avoiding duplicate insert attempts after URL canonicalization.
- Data transfer now includes the channel status columns so unavailable channels are preserved across transfer and dry-run scripts.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"` (96 passed)
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build` (139 passed)
- Applied `20260510_AddChannelStatus.sql` twice against a temporary LocalDB database with a pre-migration `youtubed.Channel` table and verified status lookup joins.
