# Task 024: Add A Mandatory Provider CI Release Gate

Status: In Progress

Depends On: 2300_complete_storage_agnostic_repository_boundaries

## Goal

Make every change prove SQL and Cosmos behavior in a repeatable repository-owned continuous-integration pipeline where required tests cannot silently skip.

## Scope

- Add a tracked CI workflow that runs on pull requests and the protected production branch.
- Build once, then run non-provider tests, LocalDB tests, and Cosmos emulator tests sequentially.
- Provision or connect to reliable isolated LocalDB and Cosmos emulator instances in CI.
- Publish TRX logs, coverage, and useful emulator/service diagnostics on failure.
- Fail when required LocalDB or Cosmos tests are skipped, not merely when tests fail.
- Add a coverage report and set an initial meaningful threshold for production and provider code; document how the threshold can only move upward or be intentionally reviewed.
- Add format verification, `git diff --check`, and direct-plus-transitive NuGet vulnerability scanning.
- Document the same release-validation command sequence for local execution.

## Out Of Scope

- Azure production deployment.
- Replacing xUnit.
- Treating emulator validation as a substitute for later Azure staging validation.

## Validation

- A clean CI run executes every required suite with zero skips and publishes its artifacts.
- A controlled test demonstrates that disabling/unavailable LocalDB or Cosmos fails the workflow.
- A controlled formatting or vulnerable-package policy failure is detected by the workflow.
- The workflow does not expose connection strings, emulator keys, list tokens, or other secrets.
- Local documentation reproduces the CI command order successfully.

## Implementation Summary

Implemented a repository-owned PowerShell release gate and a Windows GitHub
Actions workflow for pull requests and pushes to `master`. The workflow starts
LocalDB and the preinstalled Azure Cosmos DB emulator, builds once, then runs
mutually exclusive non-provider, LocalDB, and Cosmos suites sequentially. Custom
xUnit provider traits make suite selection explicit. TRX assertions require a
non-empty suite, zero failures, and zero skipped/unexecuted tests, so missing
opt-ins and unavailable services cannot silently pass.

Added Cobertura collection and a merged HTML/Markdown report with 80% line
coverage floors for both production and persistence-provider code. The floors
may move upward normally; lowering them requires an intentional reviewed script
and rationale change. The same gate runs format verification, `git diff --check`,
and a JSON direct-plus-transitive NuGet vulnerability scan. Git whitespace
verification covers the PR base-to-tested-merge range, the push before-to-SHA
range, or the empty tree for a newly created branch, plus staged and unstaged
local changes. CI always publishes
TRX, console/test-host diagnostics, coverage, and audit output. Failure
diagnostics include service status/version and only metadata about emulator log
files, avoiding credential-bearing emulator log content.

Added controlled policy probes. Synthetic skipped-suite, formatting-failure, and
vulnerable-transitive-package inputs must be rejected. A compiled-suite probe
also removes each provider opt-in in turn and proves that the resulting 71
LocalDB skips and 68 Cosmos skips fail the TRX policy. Documented local
prerequisites, exact command order, artifacts, thresholds, and the required
branch-protection check in `docs/release-validation.md`. Incremented
`AssemblyVersion` from `2.22.0.0` to `2.23.0.0` for the CI feature.

Local validation passed sequentially on 2026-08-01:

- Release build: passed with 0 warnings and 0 errors;
- non-provider suite: 196 passed, 0 failed, 0 skipped;
- opted-in LocalDB suite: 71 passed, 0 failed, 0 skipped;
- opted-in Cosmos emulator suite: 68 passed, 0 failed, 0 skipped;
- merged coverage: 83.63% production and 85.92% persistence-provider lines,
  both above the 80% floors;
- `dotnet format youtubed.sln --verify-no-changes --no-restore`: passed;
- local staged and working-tree `git diff --check` checks: passed;
- direct-plus-transitive NuGet vulnerability scan: passed with zero findings;
- controlled policy probes: passed by rejecting skipped suites, both disabled
  provider suites, a formatting failure, a vulnerable transitive package, and
  actual Git PR/push/new-branch/index/worktree whitespace failures.

Status remains In Progress because this ordinal-2400 task requires evidence from
an actual clean CI run that publishes its artifacts. The new workflow cannot run
on GitHub until these files are committed and pushed, and pushing was not part of
this implementation assignment. Branch protection must then be configured or
confirmed to require the `SQL and Cosmos release validation` check on `master`.
