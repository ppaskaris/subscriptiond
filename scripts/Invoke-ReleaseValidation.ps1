[CmdletBinding()]
param(
    [string] $ResultsDirectory = 'artifacts/release-validation'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ReleaseGate.psm1') -Force

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resultsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ResultsDirectory))
if (-not $resultsRoot.StartsWith($repositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ResultsDirectory must resolve inside the repository: $resultsRoot"
}

# These floors are policy. Raise them as coverage improves. Lowering either value
# requires an intentional review of this script and the coverage rationale in docs/release-validation.md.
$productionCoverageThreshold = 80.0
$providerCoverageThreshold = 80.0

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Command,

        [string] $LogPath
    )

    Write-Host "`n==> $Name"
    $savedErrorActionPreference = $ErrorActionPreference
    $commandError = $null
    $exitCode = -1
    try {
        # PowerShell 5.1 can surface a native program's stderr as a non-terminating
        # NativeCommandError even when the program exits successfully. Native gate
        # commands are authoritative through their exit codes.
        $ErrorActionPreference = 'Continue'
        if ($LogPath) {
            & $Command 2>&1 | Tee-Object -FilePath $LogPath | ForEach-Object { Write-Host $_ }
        }
        else {
            & $Command
        }
        $exitCode = $LASTEXITCODE
    }
    catch {
        $commandError = $_
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($null -ne $commandError) {
        throw $commandError
    }
    Assert-ReleaseGateCommandSucceeded -Name $Name -ExitCode $exitCode
}

function Get-SingleCoveragePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SuiteDirectory
    )

    $coverageFiles = @(Get-ChildItem -LiteralPath $SuiteDirectory -Recurse -Filter 'coverage.cobertura.xml' -File)
    if ($coverageFiles.Count -eq 0) {
        throw "Expected a coverage file below $SuiteDirectory, found none."
    }

    # VSTest can copy its attachment into the deployment directory as well as
    # retaining the collector attachment. Accept only byte-identical copies.
    $hashGroups = @($coverageFiles | Get-FileHash -Algorithm SHA256 | Group-Object Hash)
    if ($hashGroups.Count -ne 1) {
        throw "Found conflicting coverage files below $SuiteDirectory."
    }

    return ($coverageFiles | Sort-Object { $_.FullName.Length } | Select-Object -First 1).FullName
}

function Invoke-TestSuite {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $DirectoryName,

        [Parameter(Mandatory = $true)]
        [string] $Filter
    )

    $suiteDirectory = Join-Path $resultsRoot $DirectoryName
    New-Item -ItemType Directory -Force -Path $suiteDirectory | Out-Null
    $trxName = "$DirectoryName.trx"
    $diagnosticPath = Join-Path $suiteDirectory "$DirectoryName.testhost.log"
    $testArguments = @(
        'test', 'youtubed.sln',
        '--configuration', 'Release',
        '--no-build',
        '--no-restore',
        '--filter', $Filter,
        '--settings', 'release-coverage.runsettings',
        '--results-directory', $suiteDirectory,
        '--logger', "trx;LogFileName=$trxName",
        '--diag', $diagnosticPath
    )

    Invoke-CheckedCommand -Name $Name -LogPath (Join-Path $suiteDirectory 'console.log') -Command {
        & dotnet @testArguments
    }

    $trxPath = Join-Path $suiteDirectory $trxName
    $testSummary = Assert-ReleaseGateTestResults -Path $trxPath -SuiteName $Name
    Write-Host "$($testSummary.Suite): $($testSummary.Total) executed, zero failed, zero skipped."
    return Get-SingleCoveragePath -SuiteDirectory $suiteDirectory
}

$savedLocalDbOptIn = [Environment]::GetEnvironmentVariable('YOUTUBED_RUN_LOCALDB_TESTS')
$savedCosmosOptIn = [Environment]::GetEnvironmentVariable('YOUTUBED_RUN_COSMOS_TESTS')

Push-Location $repositoryRoot
try {
    if (Test-Path -LiteralPath $resultsRoot) {
        Remove-Item -LiteralPath $resultsRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null

    Invoke-CheckedCommand -Name 'Restore' -LogPath (Join-Path $resultsRoot 'restore.log') -Command {
        & dotnet restore youtubed.sln
    }
    Invoke-CheckedCommand -Name 'Build once' -LogPath (Join-Path $resultsRoot 'build.log') -Command {
        & dotnet build youtubed.sln --configuration Release --no-restore
    }

    Remove-Item Env:YOUTUBED_RUN_LOCALDB_TESTS -ErrorAction SilentlyContinue
    Remove-Item Env:YOUTUBED_RUN_COSMOS_TESTS -ErrorAction SilentlyContinue
    $nonProviderCoverage = Invoke-TestSuite -Name 'Non-provider tests' -DirectoryName 'non-provider' -Filter 'Provider!=LocalDb&Provider!=Cosmos'

    $env:YOUTUBED_RUN_LOCALDB_TESTS = 'true'
    Remove-Item Env:YOUTUBED_RUN_COSMOS_TESTS -ErrorAction SilentlyContinue
    $localDbCoverage = Invoke-TestSuite -Name 'Required LocalDB tests' -DirectoryName 'localdb' -Filter 'Provider=LocalDb'

    Remove-Item Env:YOUTUBED_RUN_LOCALDB_TESTS -ErrorAction SilentlyContinue
    $env:YOUTUBED_RUN_COSMOS_TESTS = 'true'
    $cosmosCoverage = Invoke-TestSuite -Name 'Required Cosmos emulator tests' -DirectoryName 'cosmos' -Filter 'Provider=Cosmos'

    $coverageReport = New-ReleaseGateCoverageReport `
        -CoveragePaths @($nonProviderCoverage, $localDbCoverage, $cosmosCoverage) `
        -OutputDirectory (Join-Path $resultsRoot 'coverage') `
        -ProductionThreshold $productionCoverageThreshold `
        -ProviderThreshold $providerCoverageThreshold
    Write-Host "Coverage: production $($coverageReport.ProductionPercent)%, providers $($coverageReport.ProviderPercent)%."

    Invoke-CheckedCommand -Name 'Format verification' -LogPath (Join-Path $resultsRoot 'format.log') -Command {
        & dotnet format youtubed.sln --verify-no-changes --no-restore
    }
    Write-Host "`n==> Git whitespace verification"
    Invoke-ReleaseGateGitDiffCheck `
        -EventName $env:RELEASE_GATE_EVENT_NAME `
        -BaseSha $env:RELEASE_GATE_BASE_SHA `
        -BeforeSha $env:RELEASE_GATE_BEFORE_SHA `
        -HeadSha $env:RELEASE_GATE_HEAD_SHA `
        -LogPath (Join-Path $resultsRoot 'git-diff-check.log')

    $auditPath = Join-Path $resultsRoot 'nuget-vulnerabilities.json'
    Invoke-CheckedCommand -Name 'Direct and transitive NuGet vulnerability scan' -LogPath $auditPath -Command {
        & dotnet list youtubed.sln package --vulnerable --include-transitive --format json --no-restore
    }
    Assert-ReleaseGateNuGetAudit -Path $auditPath | Out-Null

    Write-Host "`nRelease validation passed. Artifacts: $resultsRoot"
}
finally {
    Pop-Location
    [Environment]::SetEnvironmentVariable('YOUTUBED_RUN_LOCALDB_TESTS', $savedLocalDbOptIn)
    [Environment]::SetEnvironmentVariable('YOUTUBED_RUN_COSMOS_TESTS', $savedCosmosOptIn)
}
