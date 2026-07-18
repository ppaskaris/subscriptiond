# Task 030: Complete The Production Release Gate

Status: Not Started

Depends On: 2900_validate_azure_staging_and_production_runbook

## Goal

Perform the final independent production-readiness audit and produce an evidence-backed go/no-go decision for the Cosmos deployment.

## Scope

- Trace every criterion from the original Cosmos project and Tasks 2000 through 2900 to implementation and validation evidence.
- Confirm all dependency tasks are `Completed` with no skipped, unavailable, or failed required validation.
- Review unresolved defects, operational risks, security findings, data-protection risks, capacity assumptions, and accepted exceptions.
- Re-run the complete release validation sequence from a clean checkout/build environment.
- Confirm the production infrastructure plan, application artifact, configuration, migration checkpoint state, rollback source, dashboards, alerts, and runbooks are the reviewed versions.
- Run the final pre-cutover staging smoke and reconciliation.
- Produce a signed/dated release report with explicit go/no-go, decision owner, rollback triggers, and evidence links.
- Do not deploy production as part of this task unless the user separately authorizes deployment.

## Out Of Scope

- Fixing newly discovered defects inside the release-gate task; reopen or create the appropriate implementation task instead.
- Waiving a release blocker without explicit user approval.
- Production cutover without separate authorization.

## Validation

- `dotnet build youtubed.sln` completes with zero warnings and errors.
- Non-provider, LocalDB, and Cosmos suites pass sequentially with zero failures and zero required skips.
- Coverage meets the ratcheted threshold.
- `dotnet format youtubed.sln --verify-no-changes --no-restore` and `git diff --check` pass.
- Direct and transitive NuGet vulnerability scanning reports no unapproved vulnerabilities.
- Infrastructure validation/what-if shows only the reviewed production changes.
- Migration validation-only and reconciliation pass against the final staging snapshot.
- Azure staging smoke, health, metrics, alerts, backup restore evidence, RU/size budgets, soak, and rollback rehearsal are current and passing.
- The release report concludes `GO`; otherwise keep this task `In Progress` and record the blockers.

## Implementation Summary

Not implemented.
