# Multi-Persistence System Design

## Goals

The application will support multiple persistence providers while preserving the anonymous secret-link model. SQL Server remains the first working provider. Cosmos DB for NoSQL is added later as an RU-optimized provider that can run inside the Azure free tier.

The migration should happen in stages:

1. Move application behavior toward the Cosmos-friendly model while still backed by SQL Server.
2. Introduce provider-neutral domain objects and repository ports.
3. Rebuild the background worker around bounded batches, explicit worker state, and projection updates.
4. Add Cosmos DB provider implementations behind the same ports.
5. Prove provider behavior with shared contract tests.

## Provider Boundaries

Controllers and services should not depend on SQL rows or Cosmos documents. They should operate on domain objects and view models.

Recommended namespaces:

- `Domain`: storage-agnostic entities and use-case read models.
- `Models`: MVC input and view models.
- `Persistence/SqlServer`: Dapper row DTOs and SQL provider implementations.
- `Persistence/Cosmos`: Cosmos document DTOs and Cosmos provider implementations.

Repository interfaces should return domain objects only. SQL rows and Cosmos documents stay private to their provider.

## Domain Concepts

`SubscriptionList` is the list identity, settings, and lifecycle:

- `Id`
- `Token`
- `Title`
- `PlaybackRate`
- `ExpiredAfter`
- `ExpirationRenewedOn`

`ListChannelProjection` is the read model for channel management:

- `List`
- `Channels`

`ListVideoProjection` is the read model for the main list page:

- `List`
- `Channels`
  - `Videos`

The domain layer should not mirror either SQL normalization or Cosmos denormalization. It knows about `SubscriptionList`, `Channel`, and `ChannelVideo` as entities/read models. The provider decides whether those relationships come from SQL joins or a denormalized Cosmos document.

`Channel` contains fields needed to render channel management and to group videos under the channel:

- `Id`
- `Url`
- `Title`
- `Thumbnail`
- `StaleAfter`
- `Status`
- `StatusReason`
- `StatusUpdatedAt`

`Channel` is the canonical YouTube channel object:

- `Id`
- `Url`
- `Title`
- `Thumbnail`
- `PlaylistId`
- `Status`
- `StatusReason`
- `StatusUpdatedAt`
- `StaleAfter`
- `SubscribedListIds`
- `SubscriptionCount`
- `OrphanedAfter`
- `Videos`

`ChannelVideo` has the same canonical and rendered shape:

- `VideoId`
- `ChannelId`
- `Title`
- `Duration`
- `PublishedAt`
- `ThumbnailUrl`

`ShareLink` is a top-level object keyed by password and points to a list id.

## Provider Behavior

SQL Server keeps normalized relational storage:

- `List`
- `Channel`
- `ChannelVideo`
- `ListChannel`
- `ShareLink`
- `WorkerState`

SQL computes `ListChannelProjection` and `ListVideoProjection` dynamically with joins. SQL projection writes are no-ops.

Cosmos stores RU-optimized documents:

- `lists`: list settings plus embedded projected channels and projected videos.
- `channels`: canonical channel plus embedded canonical videos and reverse list references.
- `shareLinks`: share links keyed by password.
- `system`: singleton worker state.
- `recovery`: Cosmos-only list lifecycle and list/channel edge records used to
  converge cross-container writes.

Cosmos reads the list page by point-reading one list document, then reshapes the embedded denormalized data into the same domain read models returned by SQL.

## Cosmos Cross-Container Consistency

Cosmos cannot atomically update a list document and a channel document because
they are in different logical partitions and containers. The list document is
therefore the source of truth for membership: `(listId, channelId)` exists if and
only if the list exists and its `channels` array contains the channel. The
channel's `subscribedListIds`, `subscriptionCount`, orphan fields, and TTL are
derived canonical state. Embedded channel/video data in a list is a render
projection, not membership authority.

Every Cosmos membership mutation increments a scalar `membershipVersion` and
sets `membershipRecoveryPending` in the same conditional list write as the
membership change. A Cosmos-only recovery container retains one lifecycle record
per list and one small, deterministic edge record per candidate list/channel
pair. An edge is created before an add can commit and is retained while the
membership exists. Consequently, list TTL or explicit deletion cannot erase the
only durable record identifying the channels that need reverse-reference repair.

The recovery worker uses indexed due-work queries, durable total-order keysets, ETag
claims, and fixed per-pass item/RU budgets. It point-reads the authoritative list
before changing a channel, so old add, remove, delete, and projection work all
collapse to the current truth. This work is independent of channel refresh
eligibility; active, fresh, unavailable, and orphaned channels converge by the
same path. Detailed invariants, failure cases, retry policy, and bounds are in
[`implementation-contracts.md`](implementation-contracts.md), with document
shapes in [`cosmos-schema-plan.md`](cosmos-schema-plan.md).

Channel reverse references carry a generation that changes with their normalized
set, so projection traversal restarts safely when membership changes. Each list
lifecycle record transactionally counts/generates its active edge set and caps it
at 125, including failed candidates and poison/leased work. Lifecycle cleanup
uses deterministic keysets and cannot complete until the count is zero and a
fresh from-start query confirms no active edge.

Recovery page admission is also durably round-robin across Membership,
Projection, EdgeDue, and LifecycleDue, so one kind cannot consume every shared
RU/item budget forever. Membership traversal adopts only the exact generation
increment returned by its own retirement batch; any external generation change
forces a from-start keyset restart.

## Shared Ports

The exact names can evolve during implementation, but the architecture should include ports for:

- list reads, authenticated access, edits, daily renewal, and membership updates
- channel discovery, stale lookahead, batch reads, refresh saves, and reverse-reference repair
- share link creation, listing, consume, and deletion
- list projection updates
- expiration purging
- worker state

The SQL provider can implement Cosmos-oriented projection updates as no-ops. The Cosmos provider implements them as document patches or replacements.

## Time

New time-dependent code should use `IAppClock`:

```csharp
public interface IAppClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly UtcToday { get; }
    TimeSpan RandomDelay(TimeSpan min, TimeSpan max);
    DateTimeOffset UtcNowAfterRandomDelay(TimeSpan min, TimeSpan max);
}
```

Domain and persistence code should use UTC timestamps. The view can still humanize timestamps for display.

## Contract Tests

Provider contract tests should prove shared behavior against SQL first and Cosmos later. Cosmos tests should run against the local Cosmos emulator and be opt-in, similar to LocalDB tests.

Suggested environment variable:

```text
YOUTUBED_RUN_COSMOS_TESTS=true
```

Contract tests should verify domain-visible behavior, not identical storage mechanics. Provider-specific tests can verify SQL schema details or Cosmos TTL/indexing behavior.

## Implementation Contracts

Provider-neutral interface sketches, conflict retry policy, configuration knobs, and logging expectations are captured in [`implementation-contracts.md`](implementation-contracts.md).
