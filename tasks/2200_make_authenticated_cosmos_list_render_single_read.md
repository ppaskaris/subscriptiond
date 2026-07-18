# Task 022: Make Authenticated Cosmos List Rendering A Single Read

Status: Not Started

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
- Production migration.

## Validation

- Unit tests prove successful, rejected-token, same-day, renewal-day, conflict-retry, and concurrent-deletion flows.
- Emulator tests instrument the SDK pipeline and assert exactly one list point read on the normal same-day page flow.
- Renewal-day tests assert one initial read plus the required conditional write, with a second read only after an injected ETag conflict.
- Controller/application tests prove existing list and channel-management routes retain behavior.
- Representative RU budgets for the list page are documented and enforced by tests.
- Full sequential non-provider, LocalDB, and opted-in Cosmos suites pass.

## Implementation Summary

Not implemented.
