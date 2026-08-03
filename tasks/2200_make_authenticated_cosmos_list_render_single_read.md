# Task 022: Make Authenticated Cosmos List Rendering A Single Read

Status: Completed

Depends On: 2000_bound_cosmos_list_projections, 2120_implement_cosmos_lifecycle_reconciliation

## Goal

Render the normal authenticated Cosmos list page from one list-document point read, with only the conditional renewal write when renewal is due.

## Scope

- Introduce a provider-neutral authenticated list projection operation or equivalent use-case port.
- For Cosmos, point-read the list once, validate the secret token, decide daily renewal from that document, and map the requested projection from the same document.
- When renewal is due, use the already-read ETag and re-read/reapply once only after an optimistic-concurrency conflict.
- Preserve constant-time token comparison, daily UTC renewal, missing/expired-list behavior, route templates, stale counts, render limits, and SQL behavior.
- Avoid returning Cosmos documents outside the provider layer.
- Add request-charge/request-count observability for the common list-page flow.

## Out Of Scope

- Changing the anonymous secret-link URL model.
- Projection sizing, completed by Task 2000.
- SQL-to-Cosmos migration.

## Validation

- Unit tests prove successful, rejected-token, same-day, renewal-day, conflict-retry, and concurrent-deletion flows.
- Emulator tests instrument the SDK pipeline and assert exactly one list point read on the normal same-day page flow.
- Renewal-day tests assert one initial read plus the required conditional write, with a second read only after an injected ETag conflict.
- Controller/application tests prove existing list and channel-management routes retain behavior.
- Representative RU budgets for the list page are documented and enforced by tests.
- Full sequential non-provider, LocalDB, and opted-in Cosmos suites pass.

## Implementation Summary

Added a provider-neutral authenticated video-projection operation and routed the
normal list-page controller action through it. SQL composes its existing
normalized authentication, renewal, and projection behavior. Cosmos now
point-reads the list once, constant-time compares the secret route token, maps
the bounded video projection from that document, and conditionally renews with
the already-read ETag. A 412 causes exactly one reread/reapply; a concurrent
delete returns no projection. The list lifecycle record is intentionally not
synchronously written on this page path: its prior deadline remains a safe
early check at which reconciliation reads the authoritative renewed list and
reschedules it.

Added operation-level Cosmos SDK request-count and request-charge histograms,
tagged by outcome, while preserving per-request debug logging. Documented and
enforced representative emulator budgets of one request/10 RU for the common
same-day page and two requests/25 RU for renewal. Added repository unit coverage
for successful mapping, rejected tokens, same-day access, renewal using the
initial ETag, one conflict retry, and concurrent deletion; service/controller
coverage proves the combined use-case path and existing routes; emulator
coverage instruments the actual SDK pipeline. Incremented `AssemblyVersion`
from `2.20.0.0` to `2.21.0.0` for the backward-compatible feature.

Review follow-up corrected terminal telemetry classification: unhandled
read/mapping/write failures now record `error`, an actual missing point read
records `missing`, and a second renewal 412 records `conflict_exhausted` before
the exception is rethrown. Meter-listener tests assert each failure outcome.

Validation passed sequentially on 2026-08-01:

- `dotnet build youtubed.sln`: passed with 0 warnings and 0 errors;
- tests excluding LocalDB and Cosmos: 193 passed, 0 failed, 0 skipped;
- opted-in LocalDB tests with `YOUTUBED_RUN_LOCALDB_TESTS=true`: 79 passed,
  0 failed, 0 skipped;
- opted-in Cosmos emulator tests with `YOUTUBED_RUN_COSMOS_TESTS=true`: 68
  passed, 0 failed, 0 skipped;
- focused emulator measurement: renewal used 2 SDK requests and 12.81 RU;
  same-day rendering used exactly 1 SDK request and 1.00 RU;
- `git diff --check`: passed.
