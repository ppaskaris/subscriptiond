# Task 026a: Harden Cosmos Identity, Health, And Observability

Status: Not Started

Depends On: 2600_add_production_cosmos_infrastructure

## Goal

Use least-privilege production authentication and provide enough health, metrics, logs, and alerts to operate the Cosmos provider safely.

## Scope

- Add managed-identity/Entra ID Cosmos authentication for Azure hosting while retaining an explicit emulator/development connection-string path.
- Define and provision least-privilege data-plane roles; do not grant account-management rights to the web application.
- Include the fifth `recovery` container in least-privilege data-plane access and
  readiness/resource-drift diagnostics.
- Validate configuration eagerly without logging keys, tokens, connection strings, or secret-link values.
- Add readiness and liveness behavior that distinguishes configuration/resource drift, Cosmos unavailability, and ordinary transient throttling.
- Treat a missing/drifted recovery container or required index as readiness
  failure, but treat non-empty, leased, or poison recovery work as operational
  health/alert state rather than liveness failure.
- Surface lifecycle active-edge count/query drift and bounded recount outcomes in
  health metrics and alerts without scanning unrelated partitions.
- Emit structured metrics for RU, latency, 429s, retries, failures, reconciliation backlog/age, TTL cleanup lag, worker lease state, and migration state where applicable.
- Add staging alert rules and an operator dashboard/runbook for capacity, errors, poison recovery work, and unexpected spend.
- Verify logs retain useful operation context while protecting anonymous secret-link secrets.

## Out Of Scope

- Introducing user accounts.
- Production cutover.
- Distributed worker coordination, completed in Task 2700.

## Validation

- Staging runs exclusively through managed identity with the intended least-privilege role.
- Negative tests prove insufficient permissions and wrong resources fail readiness with actionable, secret-safe diagnostics.
- Health behavior does not restart the service merely because of expected short-lived 429 responses.
- Synthetic staging failures trigger the expected metrics and alerts.
- A log review confirms tokens, share passwords, keys, and connection strings are absent.
- Full mandatory CI and staging smoke tests pass.

## Implementation Summary

Not implemented.
