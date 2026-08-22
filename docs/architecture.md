# Simplified Persistence Architecture

## Purpose

The application uses Azure Cosmos DB for NoSQL while preserving its anonymous secret-link model.
It uses small, directly addressable documents and accepts a bounded multi-read on list pages.

The design targets a low-traffic, single-instance hobby deployment on the Azure Cosmos DB
lifetime free tier. Simplicity, recoverability by repetition, and low operator burden matter
more than minimizing every SDK request or supporting arbitrary scale-out.

## Decisions

- A list is the only authority for its channel membership.
- A channel document is the only authority for cached YouTube metadata, status, and videos.
- A share-link document is the only authority for that link's expiry and consumption state.
- Cosmos list documents store channel IDs, not embedded channel or video projections.
- Channel documents do not store reverse list references or subscription counts.
- Missing channel documents are cache misses, not consistency corruption.
- List rendering performs one list point read followed by a bounded channel `ReadMany`.
- Channel refresh is requested by list access and processed by a best-effort in-memory queue.
- Cosmos TTL expires lists and share links. It does not trigger related-document repair.
- The supported production topology is one application instance.
- ETag-protected writes receive one reread/reapply attempt after a conflict, then fail visibly.

## Explicit Non-Goals

The implementation does not provide:

- one-request list rendering;
- embedded list projections;
- channel-to-list reverse references;
- cross-container transaction emulation;
- recovery, lifecycle, edge, cursor, lease, poison, or fairness documents;
- durable worker scheduling;
- proactive refresh of lists nobody is viewing;
- multi-instance suppression of duplicate YouTube work;
- user accounts or authentication beyond existing secret URLs.

These are deliberate product constraints, not deferred requirements. Reintroducing one requires
evidence that the deployed workload needs it and a new design decision.

## Core Application Structure

The application retains:

- storage-agnostic domain models and repository interfaces;
- the application clock;
- channel availability status;
- bounded YouTube batching and cancellation-safe result finalization;
- daily list-expiration renewal;
- secret-safe token comparison;
- persistence contract-test infrastructure where the contracts describe visible behavior;
- the Cosmos emulator fixture and serializer where they remain useful.

Remove the machinery whose only purpose is maintaining duplicated Cosmos state:

- embedded list channel/video projections and their sizing policy;
- `IListProjectionRepository` and provider projection writers;
- channel reverse references, counts, orphan markers, and membership generations;
- membership and projection pending/version fields;
- the recovery container and all lifecycle, edge, cursor, and ticket documents;
- `IConsistencyRecoveryService`, its worker phase, and recovery scheduler state;
- Cosmos recovery telemetry, logical admission budgets, and adversarial recovery tests;
- durable worker state once the request-driven worker replaces it.

## Persistence Boundary

Controllers and services operate on domain objects and MVC view models. Repository interfaces
must not expose Cosmos documents, SDK response types, partition keys, or ETags. Cosmos document
types and mapping remain inside `Persistence/Cosmos`.

## Application Flows

### Create a list

Create the list identity, random secret token, settings, expiry, daily-renewal date, and an empty
channel-ID collection. No channel or worker document is involved.

### Authenticate and render a list

1. Point-read the list by ID.
2. Compare the supplied secret token in constant time.
3. Renew list expiry at most once per UTC day with an ETag-protected list write.
4. Read the listed channel documents by their known IDs in one bounded `ReadMany` operation.
5. Treat missing channel documents as recoverable cache misses and queue their IDs for discovery.
6. Queue active stale channels for refresh without delaying the HTTP response.
7. Flatten available cached videos, sort by `PublishedAt DESC, VideoId ASC`, and render at most
   `Constants.ListRenderMaxItems`.

A public list URL remains the authenticated URL: possession of its high-entropy secret is the
authentication mechanism. Never log the supplied or stored token.

### Manage channels

The channel-management page reads the list and its channel documents without reading another
projection. A missing channel is displayed as temporarily unavailable and queued for discovery.

Adding a channel first resolves the submitted URL to the canonical YouTube channel ID and ensures
a usable channel cache document exists. It then adds that ID to the list with an ETag-protected
write. If the process stops after channel creation, the extra channel document is harmless.

Removing a channel changes only the list document. Repeating the removal succeeds when the ID is
already absent. Deleting a list changes only the list document; no reverse cleanup is required.

### Refresh channels

A bounded in-memory queue deduplicates channel IDs. Viewing a list enqueues missing or stale
channels, and an existing force-refresh action enqueues every channel in that list. A background
service drains a bounded batch, uses the existing YouTube batching code, and writes each completed
channel document independently.

The queue is intentionally not durable. Application restart or App Service suspension may discard
pending IDs; the next list access enqueues them again. Two processes could perform duplicate
YouTube work, so production is constrained to one instance. Completed results still use ETags and
must not overwrite a newer channel version after the one permitted retry.

### Share links

Creating a share link writes one TTL-backed document keyed by its password. Listing links is a
low-volume cross-partition query by list ID. Consuming a link point-reads it, validates expiry and
unused state, point-reads the target list, then marks the link used with its ETag. A conflict means
another request consumed or changed the link and must not reveal the list token.

## Failure Semantics

The simplified model avoids workflows that require distributed repair:

- An unreferenced channel created before a failed list add is harmless cached data.
- A list referencing a missing channel remains valid; the channel is rediscovered on access.
- A failed channel refresh leaves the previous cache visible and the channel stale for retry.
- A lost in-memory refresh request is recreated by the next list access.
- A list TTL deletion cannot leave an invalid reverse reference because none exists.
- A failed share consume either leaves the link unused or consumes it without returning a token;
  it never returns the same token successfully to two competing callers.

Ordinary 404s, 409s, 412s, 429s, cancellation, and transient service failures need focused handling
and logging. They do not justify a durable application-level recovery subsystem.

## Capacity Boundaries

Initially retain the existing user-visible limits:

- at most 100 channel IDs per list;
- at most 100 cached videos per channel;
- at most 100 rendered videos per list page;
- list and channel documents must remain comfortably below the Cosmos 2-MiB item limit.

Because list documents contain IDs rather than video projections, their size is naturally small.
Measure small, representative, and maximum supported list-page reads against the emulator and one
real Azure free-tier account. Add tighter limits only when evidence requires them.
