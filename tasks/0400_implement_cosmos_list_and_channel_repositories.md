# Task 0400: Implement Cosmos List And Channel Repositories

Status: Completed

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

- Added Cosmos list and channel repositories behind the existing storage-agnostic ports. Lists use
  point reads, sorted distinct membership capped at 100 IDs, one bounded channel `ReadMany`,
  constant-time authentication, once-daily TTL renewal, settings/membership mutation, and explicit
  deletion. Channel management maps each missing cache document to an explicitly marked,
  storage-agnostic "Temporarily unavailable" entry so it stays visible and removable while the
  existing service safely queues rediscovery; video reads keep using authoritative `ChannelIds`
  alongside the available cache-backed channels.
- Added ETag-protected list and channel writes with one reread/reapply attempt and visible failure
  on a second conflict. Discovery leaves an already cached channel's metadata/videos intact while
  making it active and eligible again. Completed refreshes independently replace channel cache
  documents, preserve videos for metadata-only outcomes, and deterministically de-duplicate/order
  and cap refreshed videos at 100.
- Added bounded, secret-safe Cosmos request telemetry for operation, container, request count, RU,
  latency, HTTP status, and application retry count. Telemetry does not include item IDs, tokens,
  resource URIs, document bodies, exceptions, or raw SDK diagnostics. Added a Cosmos expiration
  purger that deliberately performs no application deletes because Cosmos TTL owns list/share-link
  cleanup and channel retention is unmanaged.
- Added focused unit tests, shared Cosmos list/channel provider contracts, and emulator tests for
  the exact common-page point-read/`ReadMany` request shape. The concurrency tests synchronize two
  genuine emulator writers after their stale ETag reads, prove a real 412 response and successful
  single retry for both membership and refresh, and verify the resulting documents. Incremented
  `AssemblyVersion` from `2.25.0.0` to `2.26.0.0` for this backward-compatible provider feature.
- Validation: `dotnet build youtubed.sln` passed with zero warnings and errors; tests excluding
  LocalDB and Cosmos passed 148/148 with no skips; opted-in LocalDB tests passed 50/50 with no
  skips; opted-in Cosmos emulator tests passed 14/14 with no skips; `git diff --check` and the
  equivalent trailing-whitespace scan across new files passed.
