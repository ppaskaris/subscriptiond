# Task 025b: Establish Cosmos Size, RU, And Resilience Budgets

Status: Completed

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

Established one centralized Cosmos release policy with explicit small (1 channel,
5 videos), normal (20 channels, 20 videos each), and supported-maximum (100
channels, 100 canonical videos each, at most 500 projected videos) shapes. The
policy records request-count and local-emulator RU budgets for list rendering,
membership, refresh/fan-out, share, reconciliation, and scheduler operations and
applies a documented 20% regression tolerance. Unit tests deliberately exceed
request and RU thresholds and prove the guard fails. Existing maximum-payload
tests continue to enforce the strict 1,900,000-byte ceiling, 350-RU point-read
budget, and 3,000-RU projection-write budget.

Instrumented every Cosmos SDK response, exception, and caller cancellation with
request charge, latency, HTTP operation, resource category, outcome,
status/substatus, and SDK throttle retry count. Logs no longer contain request
URIs (which can include share-link ids) or raw exception/diagnostics text; terminal
failures log only a sanitized error class. Added metrics and classification tests
for exhausted 429 throttling, 408/504 timeout, caller cancellation, 503 service
unavailability, and non-transient failures.

Production and emulator clients now explicitly use a ten-second request timeout,
at most nine 429 retries, and at most 30 seconds of throttle-retry waiting. A
three-retry/ten-second candidate was rejected after a full emulator run surfaced
exhausted 429s during real membership mutation and durable ticket admission (75
passed, 2 failed); that run is design evidence, not passing validation. The
nine-retry/30-second bounded policy subsequently passed all concurrency and
restart cases. The independent application ETag policy remains one reread/retry.
Existing worker tests continue to prove completed YouTube work is persisted with
no new external call after cancellation, and the full emulator suite proves
durable-side-effect restart recovery.

Review follow-up replaced declarative coverage with objective operation evidence.
Concrete small, normal, and supported-maximum list graphs and canonical video
counts are instantiated and serialized; emulator tests persist/read/replace the
small and normal shapes, while the padded maximum continues through the real
membership/projection paths. End-to-end measurements now isolate a real pending
projection fan-out from canonical refresh, membership remove from add, and share
create/list/delete from consume. A recovery test scopes and asserts a real
restart reconciliation pass. A no-op projection point read is not accepted as
fan-out evidence.

The same follow-up added fixed, low-cardinality logical-operation propagation via
repository scopes. The SDK handler emits both logical and HTTP operations, and
handler-pipeline tests prove success, exhausted 429 retry/status metadata,
caller cancellation, timeout, and 503 outcomes, visible sanitized logs, secret
exclusion, and rejection of arbitrary tag values. The emulator now injects 429,
408, 503, and cancellation immediately after a durable membership commit,
disposes the interrupted provider, recreates providers, and proves list/channel/
edge convergence for every case.

Final review follow-up added deterministic exhaustion evidence through the real
Cosmos SDK retry handler. An emulator-backed gateway `CosmosClient` seeds a point-
read target, then an HTTP transport fault returns 429/3200 only for that target.
The transport observes exactly ten attempts (one initial plus the configured nine
retries) and the SDK returns terminal 429. Because SDK v3.62 does not add a
throttle-retry-count response header, the production handler safely derives retry
count 9 from the terminal diagnostics status aggregate without logging raw
diagnostics or URIs; the test asserts the metric, sanitized log, and secret
exclusion. The earlier single scripted-handler 429 test is retained only for
header-fallback/formatting coverage and is not described as SDK retry evidence.
The companion real-client recovery case injects three 429/3200 responses, forwards
attempt four to the emulator, succeeds, and asserts retry count 3 in the
`list_page` metric/log with no target id. Diagnostics parsing sums all preceding
429 substatus aggregates for successful responses while continuing to subtract
the terminal un-retried 429 during exhaustion.

The final telemetry correction aggregates matching 429 counts across both
`GatewayCalls` and `DirectCalls` before subtracting a terminal un-retried 429
exactly once. A focused diagnostics-shape regression covers GatewayCalls present
with only non-throttled traffic plus DirectCalls throttles, throttles in both
categories, multiple 429 substatuses, successful completion, and exhausted
completion. This preserves the real gateway SDK evidence while covering the
production-default Direct mode summary shape.

Documented free-tier assumptions (one shared provisioned account, 1,000 RU/s and
25 GB), a 30% operating reserve, stop-growth thresholds of sustained 700 RU/s or
20 GB, existing hard cardinality rejection boundaries, emulator-versus-cloud
qualifications, and the budget review process. `AssemblyVersion` is incremented
from `2.22.2.0` to `2.23.0.0`.

Final local-emulator measurements on 2026-08-07 included membership add/remove
at 22 requests/109.81 RU and 19/93.00; one-channel refresh plus fan-out at
14/60.63; distinct pending one-list fan-out at 8/38.29; share create/consume/list/
delete at 1/7.05, 3/13.24, 1/2.82, and 2/8.24; scheduler force at 2/11.29; and
restart reconciliation at 46/259.36. The small dataset measured 1,622 bytes,
1.05 read RU, and 13.71 replace RU; normal measured 74,738 bytes, 2.19 read RU,
and 32.86 replace RU. The supported-maximum observation remains 1,773,836 bytes,
291.80 RU for its point read, and 2,500.77 RU for replacement. Emulator RU values
are regression observations, not Azure billing predictions.

Final validation passed sequentially on 2026-08-07:

- `dotnet build youtubed.sln`: passed with 0 warnings and 0 errors;
- tests excluding LocalDB and Cosmos: 224 passed, 0 failed, 0 skipped;
- opted-in LocalDB tests with `YOUTUBED_RUN_LOCALDB_TESTS=true`: 71 passed,
  0 failed, 0 skipped;
- full opted-in Cosmos emulator suite with `YOUTUBED_RUN_COSMOS_TESTS=true`: 81
  passed, 0 failed, 0 skipped in 6 minutes 31 seconds;
- `git diff --check`: passed.

The first 80-test full-suite attempt was not counted as passing evidence: the new
SDK retry test passed, but one repetition of the pre-existing membership/
projection race surfaced an unexpected emulator 404 (79 passed, 1 failed). The
isolated race then passed all repetitions in 33 seconds. A fresh complete
build/non-provider/LocalDB/Cosmos sequence produced the final results above.
During the successful-retry follow-up, one non-provider run exposed that two
global meter listeners could observe each other's samples; the handler assertions
now select their unique logical-operation/outcome tags. That failed 222/223 run
is not counted as evidence; the fresh final chain plus the Direct/Gateway
regression passed all 224 tests as shown.
