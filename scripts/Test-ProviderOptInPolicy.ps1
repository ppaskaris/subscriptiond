[CmdletBinding()]
param(
    [string] $ResultsDirectory = 'artifacts/release-validation/controlled-provider-opt-out'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ReleaseGate.psm1') -Force

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resultsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ResultsDirectory))
if (-not $resultsRoot.StartsWith($repositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ResultsDirectory must resolve inside the repository: $resultsRoot"
}

$savedLocalDbOptIn = [Environment]::GetEnvironmentVariable('YOUTUBED_RUN_LOCALDB_TESTS')
$savedCosmosOptIn = [Environment]::GetEnvironmentVariable('YOUTUBED_RUN_COSMOS_TESTS')

Push-Location $repositoryRoot
try {
    Remove-Item Env:YOUTUBED_RUN_LOCALDB_TESTS -ErrorAction SilentlyContinue
    Remove-Item Env:YOUTUBED_RUN_COSMOS_TESTS -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null

    foreach ($probe in @(
        @{ Name = 'LocalDB'; Filter = 'Provider=LocalDb'; File = 'disabled-localdb.trx' },
        @{ Name = 'Cosmos'; Filter = 'Provider=Cosmos'; File = 'disabled-cosmos.trx' }
    )) {
        & dotnet test youtubed.sln `
            --configuration Release `
            --no-build `
            --no-restore `
            --filter $probe.Filter `
            --results-directory $resultsRoot `
            --logger "trx;LogFileName=$($probe.File)"
        if ($LASTEXITCODE -ne 0) {
            throw "The controlled disabled-$($probe.Name) test command failed unexpectedly with exit code $LASTEXITCODE."
        }

        $rejected = $false
        try {
            Assert-ReleaseGateTestResults `
                -Path (Join-Path $resultsRoot $probe.File) `
                -SuiteName "controlled disabled $($probe.Name) suite"
        }
        catch {
            if ($_.Exception.Message -match 'skipped or otherwise unexecuted') {
                $rejected = $true
            }
            else {
                throw
            }
        }
        if (-not $rejected) {
            throw "The release gate accepted the controlled disabled-$($probe.Name) suite."
        }
    }

    Write-Host 'Provider opt-in policy probe passed: disabling LocalDB or Cosmos produced skipped suites and both were rejected.'
}
finally {
    Pop-Location
    [Environment]::SetEnvironmentVariable('YOUTUBED_RUN_LOCALDB_TESTS', $savedLocalDbOptIn)
    [Environment]::SetEnvironmentVariable('YOUTUBED_RUN_COSMOS_TESTS', $savedCosmosOptIn)
}
