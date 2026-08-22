# Task 03: Replace list projections with application-layer composition

Implement this task after Task 02. Remove the residual projection architecture from list persistence. Preserve the current Cosmos request shape while moving authentication, renewal decisions, list/channel composition, video selection, missing-channel handling, refresh scheduling, and view-model construction into the application service layer.

## Required changes

- Remove `ListVideoProjection` and `ListChannelProjection` and all code and tests whose only purpose is mapping those types.
- Remove these application-specific operations from `IListRepository`:
  - `GetAuthenticatedVideoProjectionAsync`
  - `GetVideoProjectionAsync`
  - `GetChannelProjectionAsync`
- Use `IListRepository.GetAsync` to retrieve a complete `SubscriptionList`, including its channel IDs from Task 02.
- Use `IChannelRepository.GetBatchAsync` for the single bounded batch read of referenced channels.
- Make `ListService` responsible for:
  - decoding and comparing the secret token safely;
  - deciding whether daily expiration renewal is needed;
  - composing a list with the returned channel cache entries;
  - identifying missing channel IDs by comparing membership with returned channels;
  - creating the temporary unavailable presentation for missing channels;
  - selecting and ordering videos by `PublishedAt` descending and video ID ascending;
  - enforcing `Constants.ListRenderMaxItems` and `HasMoreVideos`;
  - enqueueing missing, stale, and forced refresh requests;
  - building the existing MVC view models.
- Consolidate the duplicated channel-to-view-model mapping left by the two projection types.
- Reshape tests around observable service and repository contracts rather than reproducing the removed projection implementation.

## Request and concurrency requirements

- An authenticated list page must still use one list point read followed by zero or one bounded channel `ReadMany` call, plus an optional expiration-renewal write.
- Do not replace `ReadMany` with an `IN` query or an unbounded loop of individual reads.
- Daily renewal must remain ETag-protected with one reread/reapply attempt after a conflict, then fail visibly.
- Do not re-read the list merely because the service already has the loaded aggregate, unless a concurrency retry requires it.
- Missing channel documents remain recoverable cache misses and must not invalidate the list.

## Constraints

- Preserve all controller attribute route templates and user-visible behavior.
- Preserve constant-time secret comparison and never log supplied or stored tokens.
- Keep repositories focused on persistence operations and returning storage-agnostic domain objects.
- Do not introduce a generic unit-of-work, query-object framework, mediator, or new projection layer under another name.
- Keep the list and channel documents unchanged.

## Acceptance criteria

- The two projection domain types and the three projection repository methods no longer exist.
- `CosmosListRepository` does not authenticate users, manufacture view state, read channel documents, or sort/select videos.
- List display, channel management, force refresh, missing-channel recovery, daily expiration renewal, and maximum-video behavior remain unchanged.
- Repository contract tests cover persistence behavior; `ListService` tests cover orchestration and presentation behavior.
- Integration tests verify the documented point-read/`ReadMany` request envelope and renewal behavior.

## Validation

This changes cross-document read orchestration and Cosmos behavior. Run validation sequentially: build, tests excluding Cosmos, then the opted-in Cosmos emulator suite with `YOUTUBED_RUN_COSMOS_TESTS=true`. Verify concurrent renewal behavior and representative request counts/RU charges. Report unavailable or skipped checks as unverified.
