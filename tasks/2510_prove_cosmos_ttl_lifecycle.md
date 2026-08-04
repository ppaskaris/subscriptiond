# Task 025a: Prove Cosmos TTL Lifecycle Behavior

Status: Completed

Depends On: 2120_implement_cosmos_lifecycle_reconciliation

## Goal

Prove that TTL is an actual end-to-end cleanup mechanism for every Cosmos lifecycle, including related-reference convergence.

## Scope

- Add isolated short-TTL emulator tests for expired lists, expired and used share links, and orphan channels with embedded videos.
- Poll with bounded deadlines and useful diagnostics rather than using fixed long sleeps.
- Verify physical deletion, not merely the `ttl` property or no-op purger return value.
- After list TTL deletion, verify reverse references, `subscriptionCount`, orphan markers, and orphan TTL converge for active and unavailable channels.
- Verify renewal and re-subscription clear or recompute TTL without extending unrelated lifetimes.
- Document expected TTL and reconciliation latency ranges for operations and alerts.

## Out Of Scope

- Changing retention policy values unless testing exposes an explicit product need.
- Replacing Cosmos TTL with application bulk deletion.

## Validation

- Required TTL lifecycle tests pass against the local emulator before task completion.
- Failure output identifies the retained document, TTL value, timestamps, and pending reconciliation state.
- No test passes merely because `IExpirationPurger` returns zero.
- The opted-in local Cosmos suite runs the supported emulator tests without skips.

## Implementation Summary

Expanded the emulator lifecycle coverage to prove physical TTL deletion rather
than relying on the Cosmos no-op `IExpirationPurger`. The list lifecycle case now
uses active and unavailable channels with embedded videos, waits for the list's
physical deletion, runs authoritative 404 reconciliation, verifies reverse
references, counts, orphan markers, and short orphan TTLs, then waits for both
channel documents to be physically deleted. A separate case proves policy-aligned
expired-unused and used share links created through the production repository and
mapper are physically deleted. The test asserts the persisted five-second TTL and
exact unused/used `UsedAt` state before polling. Renewal and re-subscription
coverage proves list TTL is recomputed from its unchanged absolute expiry, orphan
TTL is disabled on the re-added channel, embedded videos survive, and an unrelated
orphan document's ETag and TTL remain unchanged.

TTL tests provision and delete task-specific Cosmos containers so their short
deadlines, lifecycle records, and cursors cannot contaminate the shared emulator
suite. Bounded polling checks every 250 ms for up to 90 seconds. Failure output
includes container/id, retained TTL, Cosmos `_ts`, expiry/use/orphan timestamps,
reverse-reference/count state, membership-pending state, and the current lifecycle
state/checkpoints. The tests never call the no-op purger. Full-suite validation
also exposed that the existing multi-instance adversarial test could consume stale
pending records from earlier tests; its setup now clears the disposable fixture's
list, channel, and recovery documents before constructing the intended two-workflow
race. Each instance is held inside its intended Membership or Projection item;
Membership retains the two-item admission it needs to converge, while Projection
has a one-item pass budget so it cannot make an unintended follow-up `EdgeDue`
claim that changes the membership edge owner while the target workflows overlap.
Each concurrent instance records diagnostics independently, avoiding shared mutable
logger state; diagnostics are combined only after both operations complete. The
strict zero-failure assertion is retained with work-kind/exception diagnostics.

Documented absolute TTL deadlines, app-clock whole-second calculation, Cosmos
server-`_ts` countdown, rounding/skew/write-latency qualifications, write-time
recomputation, asynchronous physical deletion, the observational
seconds-to-minutes expectation, the emulator's 90-second test bound, one-minute
recovery admission, ten-minute present-list rechecks, and 15-minute
recovery/cleanup alerts. Incremented
`AssemblyVersion` from `2.22.1.0` to `2.22.2.0` for the backward-compatible
reliability proof and test-isolation correction.

Final validation passed sequentially on 2026-08-04:

- `dotnet build youtubed.sln`: passed with 0 warnings and 0 errors.
- Tests excluding LocalDB and Cosmos: 196 passed, 0 failed, 0 skipped.
- Opted-in LocalDB tests with `YOUTUBED_RUN_LOCALDB_TESTS=true`: 71 passed,
  0 failed, 0 skipped.
- Focused opted-in concurrent-instance Cosmos test: 1 passed, 0 failed,
  0 skipped; the test contains six repeated overlaps.
- Full opted-in Cosmos emulator suite with `YOUTUBED_RUN_COSMOS_TESTS=true`:
  77 passed, 0 failed, 0 skipped in 6 minutes 6 seconds.
- Earlier diagnostic full-suite runs were not treated as passing evidence. They
  exposed stale shared-fixture work and then identified an unintended Projection
  follow-up `EdgeDue` claim as the cause of membership-edge owner conflicts; one
  interrupted execution-layer run produced no test summary. Fixture isolation,
  work-kind exception diagnostics, and the asymmetric pass budgets were applied
  before the clean focused and full runs above.
- `dotnet format youtubed.sln --verify-no-changes --no-restore`: passed.
- `git diff --check`: passed.
