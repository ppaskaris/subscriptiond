# Task 06: Apply miscellaneous architecture cleanups

Implement this task after Tasks 01 through 05. Apply the remaining small simplifications as one focused cleanup without changing routes, persistence shape, or application behavior.

## Required changes

### Use the framework background-service base class

- Replace the custom `Services/HostedService` lifecycle implementation with `Microsoft.Extensions.Hosting.BackgroundService` for `ChannelRefreshHostedService`.
- Remove the custom base class once unused.
- Preserve cancellation, shutdown, requeue, error-delay, and testable single-iteration behavior.

### Remove redundant list model conversions

- Reassess `ListModel` after Task 03 and remove it if it still merely duplicates `SubscriptionList`.
- Avoid repeated `ListModel` to `SubscriptionList` and back conversions.
- Keep web-only formatting such as Base64 URL token rendering outside the persistence layer. Use an MVC view model or a narrowly named application result if the domain model should not carry that concern.
- Do not expose mutable token buffers unnecessarily.

### Simplify list controller responsibilities

- Split `ListController` only where doing so creates clear responsibility boundaries, such as list display/settings, channel management, and share-link management.
- Preserve every existing controller attribute route template exactly, including route parameter names and HTTP methods.
- Preserve status-code behavior, redirects, antiforgery behavior, and view names.
- Do not introduce mediator or command-handler boilerplate solely to reduce controller length.

### Use one Cosmos options-registration pattern

- Remove the duplicate raw-singleton plus options-wrapper registration of `CosmosOptions`.
- Prefer the standard validated options pattern unless a concrete startup constraint makes another single pattern simpler.
- Preserve fail-fast validation for required connection string and database name and preserve test override ergonomics.

## Constraints

- Treat these as small cleanups. Do not combine them with changes to the Cosmos schema, TTL, indexing, refresh scheduling semantics, authentication model, or public URLs.
- Reuse existing utilities and framework primitives.
- Remove files, registrations, tests, and helpers that become genuinely unused; do not leave forwarding compatibility layers.
- Keep the application compatible with its existing frontend stack.

## Acceptance criteria

- The custom hosted-service base class is gone and shutdown behavior remains correct.
- There is one clear representation of a loaded list at the service/domain boundary, with web-only token formatting kept in the appropriate layer.
- Controller responsibilities are easier to navigate while all route-contract tests pass unchanged or with only structural test updates.
- `CosmosOptions` has one registration and consumption pattern with startup validation.
- Dependency injection contains no duplicate or obsolete registrations.
- No new architectural framework or layer has been introduced.

## Validation

Run validation sequentially: build, tests excluding Cosmos, then the opted-in Cosmos emulator suite with `YOUTUBED_RUN_COSMOS_TESTS=true` because hosting and Cosmos configuration are affected. Include routing tests and hosted-service cancellation/shutdown tests. Report unavailable or skipped checks as unverified.
