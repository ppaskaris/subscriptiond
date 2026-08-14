# Task 0400: Implement Cosmos List And Channel Repositories

Status: Not Started

Depends On: 0300_add_three_container_cosmos_foundation

## Goal

Implement list membership, authentication, rendering, settings, lifecycle, channel discovery, and
channel refresh without denormalized projections or reverse references.

## Scope

- Implement list create, point read, authenticated read, once-daily renewal, settings update,
  add/remove membership, explicit delete, and Cosmos TTL behavior.
- Store a sorted distinct `channelIds` collection capped at 100 IDs.
- Use one list point read plus zero or one bounded `ReadManyItemsAsync` call to construct list video
  and channel-management read models.
- Return a safe missing-channel representation so the service can queue rediscovery without
  treating the list as corrupt.
- Implement channel point reads, discovery saves, explicit-ID batch reads, and completed refresh
  saves with at most 100 newest deterministic videos.
- Use ETags for conflicting list/channel writes with one reread/reapply attempt and then throw.
- Treat a channel created before a failed membership add as harmless cached data.
- Treat a missing channel referenced by a list as a repairable cache miss.
- Record bounded, secret-safe request-count, RU, latency, status, and retry telemetry without raw
  diagnostics or resource URIs.
- Implement Cosmos expiration purging as a no-op only for list/share TTL; channel cache retention is
  intentionally unmanaged in this design.

## Out Of Scope

- Embedded channel/video data in list documents.
- Reverse membership, subscription counts, orphan state, or channel TTL.
- Projection fan-out or any recovery record.
- Cosmos provider registration for the full application.

## Validation

- Focused unit tests cover authentication, renewal, capacity, deterministic ordering, one-conflict
  retry, second-conflict failure, missing channels, bounded videos, and secret-safe telemetry.
- Shared provider contracts cover visible list, membership, channel, status, and refresh behavior
  without asserting identical SQL/Cosmos mechanics.
- Emulator tests prove the common list page uses one point read and one `ReadMany`, with an optional
  renewal write, and performs no projection or recovery writes.
- Genuine concurrent add/remove and refresh writes produce one of the documented ETag outcomes and
  leave each authoritative document internally valid.
- Build, non-provider tests, LocalDB tests, Cosmos emulator tests, and `git diff --check` pass
  sequentially.

## Implementation Summary

Not implemented.
