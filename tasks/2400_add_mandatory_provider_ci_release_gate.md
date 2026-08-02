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
and a fail-closed JSON direct-plus-transitive NuGet vulnerability scan backed
by a complete package inventory. The audit policy validates schema and
invocation metadata, matches non-empty project sets, and rejects malformed,
empty, or incomplete output. Git whitespace
verification covers the PR base-to-tested-merge range, the push before-to-SHA
range, or the empty tree for a newly created branch, plus staged and unstaged
local changes. CI always publishes
TRX, console/test-host diagnostics, coverage, and audit output. Failure
diagnostics include service status/version and only metadata about emulator log
files, avoiding credential-bearing emulator log content.

Added controlled policy probes. Synthetic skipped-suite, formatting-failure,
vulnerable-transitive-package, and malformed/incomplete-audit inputs must be
rejected. Third-party workflow actions are pinned to reviewed immutable commit
SHAs. A compiled-suite probe
also removes each provider opt-in in turn and proves that the resulting 71
LocalDB skips and 68 Cosmos skips fail the TRX policy. Documented local
prerequisites, exact command order, artifacts, thresholds, and the required
branch-protection check in `docs/release-validation.md`. Incremented
`AssemblyVersion` from `2.22.0.0` to `2.23.0.0` for the CI feature and to
`2.23.0.1` for the release-gate policy hardening follow-up.

Local validation passed sequentially on 2026-08-02:

- Release build: passed with 0 warnings and 0 errors;
- non-provider suite: 196 passed, 0 failed, 0 skipped;
- opted-in LocalDB suite: 71 passed, 0 failed, 0 skipped;
- opted-in Cosmos emulator suite: 68 passed, 0 failed, 0 skipped;
- merged coverage: 83.51% production and 85.75% persistence-provider lines,
  both above the 80% floors;
- `dotnet format youtubed.sln --verify-no-changes --no-restore`: passed;
- local staged and working-tree `git diff --check` checks: passed;
- direct-plus-transitive NuGet vulnerability scan: passed with zero findings;
- controlled policy probes: passed by rejecting skipped suites, both disabled
  provider suites, malformed/empty/incomplete NuGet audit data, a formatting
  failure, a vulnerable transitive package, and actual Git
  PR/push/new-branch/index/worktree whitespace failures.

Status remains In Progress because this ordinal-2400 task requires evidence from
an actual clean CI run that publishes its artifacts. Commit `1fb2ff9` is pushed
to `origin/codex/cosmos-migration`, but the branch has no pull request, the
commit has no workflow check runs or artifacts, and `master` has neither classic
branch protection nor a repository ruleset. A pull request must run the workflow
successfully and publish its artifacts, after which branch protection or a
ruleset must require the `SQL and Cosmos release validation` check on `master`.
