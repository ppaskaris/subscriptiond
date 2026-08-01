# Release Validation

The mandatory provider release gate runs for every pull request and every push
to the protected production branch, `master`. It uses a Windows runner because
the repository requires both SQL Server LocalDB and the Azure Cosmos DB emulator.
The workflow starts both services, then calls the same repository-owned script
used locally.

## Local prerequisites

- .NET SDK 10
- Node.js and npm (the application build compiles the CSS bundle)
- Git
- SQL Server LocalDB instance named `MSSQLLocalDB`
- Azure Cosmos DB emulator at its default `https://localhost:8081` endpoint

Do not set a real Azure Cosmos connection string for release validation. The
test fixture uses the emulator's documented local default when
`YOUTUBED_COSMOS_EMULATOR_CONNECTION_STRING` is absent. The workflow does not
print or upload environment variables, connection strings, emulator keys, list
tokens, or application secrets.

## Run the gate locally

Start LocalDB and the Cosmos emulator, then run this command from the repository
root:

```powershell
./scripts/Invoke-ReleaseValidation.ps1
```

The script owns the command order and runs it sequentially:

1. restore dependencies;
2. build `Release` once;
3. run non-provider tests without rebuilding;
4. opt in to and run only LocalDB tests without rebuilding;
5. opt in to and run only Cosmos emulator tests without rebuilding;
6. merge coverage from all three suites and enforce its floors;
7. run `dotnet format --verify-no-changes`;
8. run Git whitespace checks for the committed CI event range, the local index,
   and the working tree;
9. scan direct and transitive NuGet packages for known vulnerabilities.

Every test run produces TRX, console output, test-host diagnostics, and Cobertura
coverage below `artifacts/release-validation`. The TRX policy requires at least
one selected test, every selected test to execute, zero failures, and zero
skips. Consequently, removing a provider opt-in or losing either service fails
the gate instead of yielding a successful skipped suite. The CI policy self-test
also feeds controlled skipped-test, formatting-failure, and vulnerable-package
results to the same assertions and requires each to be rejected. After the real
provider suites pass, CI also removes each provider opt-in in turn, selects that
provider's tests, observes the resulting skips, and proves that the TRX policy
rejects both controlled disabled-provider runs.

For pull requests, the whitespace policy compares the event's base commit to
the tested merge commit. For pushes, it compares the event's `before` commit to
the tested SHA. GitHub reports an all-zero `before` value when creating a branch;
that case compares the empty Git tree to the tested SHA, so no committed content
escapes inspection. Full history is checked out so both range endpoints exist.
Local runs additionally check staged and unstaged changes, and CI performs those
checks too in case an earlier validation command unexpectedly changes a file.

The merged HTML and Markdown coverage reports enforce 80% line coverage for the
production assembly and 80% for persistence-provider code. These are initial
floors, not targets. Coverage improvements should raise them. A reduction must
be an intentional, reviewed policy change to
`scripts/Invoke-ReleaseValidation.ps1`, with its rationale recorded here; it
must never be made merely to get a pull request green.

CI always uploads the release-validation directory. On failure it additionally
uploads LocalDB status, the Cosmos emulator version/status, a metadata-only
inventory of emulator logs, and the test-host diagnostic logs. Emulator log
contents are deliberately excluded because service logs are not a safe place to
assume credentials will never appear. GitHub branch protection should require
the `SQL and Cosmos release validation` check before `master` can be updated.
