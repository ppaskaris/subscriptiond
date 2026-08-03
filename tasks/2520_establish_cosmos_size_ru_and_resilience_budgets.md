# Task 025b: Establish Cosmos Size, RU, And Resilience Budgets

Status: Not Started

Depends On: 2000_bound_cosmos_list_projections, 2200_make_authenticated_cosmos_list_render_single_read, 2500_add_adversarial_cosmos_concurrency_tests, 2510_prove_cosmos_ttl_lifecycle

## Goal

Turn the free-tier and bounded-document objectives into measurable release budgets and prove acceptable behavior under throttling and transient failures.

## Scope

- Define representative small, normal, and supported-maximum datasets.
- Establish budgets for serialized document size, list-page reads, membership writes, channel refreshes, projection fan-out, share operations, reconciliation, and scheduler operations.
- Measure local emulator request shapes and record where emulator RU values may differ from the test server's Cosmos account.
- Add automated regression thresholds with an explicit tolerance and review process.
- Exercise Cosmos 429 responses, SDK retry exhaustion, timeouts, cancellation, service unavailability, and restart recovery.
- Ensure logs/metrics expose request charge, latency, status/substatus, retry count, and affected operation without exposing secrets.
- Document free-tier capacity assumptions and the traffic/cardinality threshold at which the hobby deployment must reject additional growth safely.

## Out Of Scope

- Provisioning or automatically deploying cloud resources.
- Unlimited load testing.
- Hiding real regressions by raising budgets without documented review.

## Validation

- Supported-maximum documents stay below the Task 2000 safety ceiling.
- Local emulator measurements satisfy the documented size and request-shape budgets; any RU estimates are clearly identified as emulator observations.
- Automated tests fail when a representative request count, size, or RU budget is intentionally exceeded.
- Transient failures either recover within documented policy or fail visibly without corrupting state.
- Cancellation tests prove completed YouTube work is finalized and no new external work begins after cancellation.
- The applicable local unit, LocalDB, and Cosmos emulator suites pass sequentially.

## Implementation Summary

Not implemented.
