# Task 0300: Add The Three-Container Cosmos Foundation

Status: Completed

Depends On: 0200_simplify_refresh_and_maintenance

## Goal

Introduce the new Cosmos client, document types, container policies, and emulator harness without
yet enabling Cosmos as an application persistence provider.

## Scope

- Add the supported Cosmos SDK package and a minimal emulator fixture.
- Add internal document types for lists, channels/videos, and share links exactly as defined in
  [`../docs/cosmos-data-model.md`](../docs/cosmos-data-model.md).
- Add mappings between those documents and storage-agnostic domain objects.
- Configure one client and a serializer shared by production writes and serialized-size tests.
- Define only `lists`, `channels`, and `shareLinks` container names and handles.
- Create narrow indexing and TTL policies: list and share-link TTL enabled, channel TTL disabled,
  secret/video payloads excluded, and no composite recovery indexes.
- Allow development/emulator setup to create its isolated database and containers.
- Make production setup require an existing database with 1,000 RU/s manual shared throughput and
  validate database throughput plus container partition keys, TTL, and indexing before serving.
- Keep `Persistence:Provider=Cosmos` disabled until Task 0500.

## Out Of Scope

- Repository implementations.
- A system or recovery container.
- Dedicated container throughput or serverless provisioning.
- Azure account creation.
- Managed identity.

## Validation

- Unit tests cover document mapping, deterministic membership/video ordering, TTL calculation,
  token secrecy, and representative serialized sizes.
- Emulator tests create exactly three containers and verify partition keys, TTL, and exclusions.
- Tests prove drift detection for wrong partition key, TTL, or indexing policy.
- Configuration tests prove production does not silently create an unprovisioned or dedicated-
  throughput billing shape.
- Build, non-provider tests, LocalDB tests, Cosmos emulator tests, and `git diff --check` pass
  sequentially.

## Implementation Summary

- Added Microsoft.Azure.Cosmos 3.62.0, one singleton-client foundation registration, a shared
  System.Text.Json Cosmos serializer, and a persistence context exposing only the `lists`,
  `channels`, and `shareLinks` handles. `Persistence:Provider=Cosmos` remains deliberately disabled.
- Added internal list, channel/video, and share-link documents matching the simplified data model.
  Mapping sorts and de-duplicates list membership, rejects more than 100 channel IDs, sorts and
  de-duplicates videos to the newest 100, preserves storage-agnostic domain objects, computes
  minimum-positive TTL values from absolute deadlines, and never puts a list token in a share-link
  document or configuration error.
- Added the exact three `/id` container definitions with TTL enabled only for lists and share
  links, list token and channel video exclusions, the four narrow share-link query paths, and no
  composite indexes. Development/emulator initialization creates an isolated 1,000 RU/s shared-
  throughput database and the three containers. Production initialization performs reads only and
  rejects a missing database/container, non-manual or non-1,000 database throughput, dedicated
  container throughput, and partition-key, TTL, or indexing drift. Exact indexing validation also
  rejects composite, spatial, vector, and full-text indexes plus vector-embedding and full-text
  container policies that are outside this narrow model.
- Restored a minimal opt-in emulator fixture. Unit and emulator coverage proves mapping round trips,
  deterministic cardinality bounds and ordering, TTL calculations, secret-safe shapes/errors,
  shared serialization, representative maximum item sizes and RU charges, exact container count,
  live policy validation, drift detection, production no-create behavior, shared-throughput
  enforcement, and physical TTL deletion without reverse-reference repair. Incremented
  `AssemblyVersion` from `2.24.0.0` to `2.25.0.0` for the backward-compatible foundation feature.
- Validation: `dotnet build youtubed.sln` passed with zero warnings and errors; tests excluding
  LocalDB and Cosmos passed 141/141 with no skips; opted-in LocalDB tests passed 50/50 with no
  skips; opted-in Cosmos emulator tests passed 6/6 with no skips; `git diff --check` and the
  equivalent whitespace scan across new files passed. Spatial-index drift is exercised against the
  emulator; vector and full-text drift use focused unit coverage because this local emulator suite
  does not provision the newer feature policies those indexes require.
