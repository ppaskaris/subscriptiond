[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ReleaseGate.psm1') -Force

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("subscriptiond-release-gate-policy-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Invoke-ControlledGit {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & git @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Controlled Git command failed: git $($Arguments -join ' ')"
    }
}

function New-ControlledGitRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $path = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path $path | Out-Null
    Push-Location $path
    try {
        Invoke-ControlledGit -Arguments @('init', '--quiet')
        Invoke-ControlledGit -Arguments @('config', 'user.name', 'Release Gate Policy')
        Invoke-ControlledGit -Arguments @('config', 'user.email', 'release-gate@example.invalid')
        Set-Content -LiteralPath 'controlled.txt' -Value 'clean'
        Invoke-ControlledGit -Arguments @('add', 'controlled.txt')
        Invoke-ControlledGit -Arguments @('-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'clean base')
        return (& git rev-parse HEAD).Trim()
    }
    finally {
        Pop-Location
    }
}

function Assert-ControlledRejection {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Probe
    )

    $rejected = $false
    try {
        & $Probe
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "The release gate accepted the controlled $Name failure."
    }
}

try {
    $skippedTrx = Join-Path $testRoot 'skipped.trx'
    Set-Content -LiteralPath $skippedTrx -Encoding UTF8 -Value @'
<?xml version="1.0" encoding="utf-8"?>
<TestRun><ResultSummary><Counters total="1" executed="0" passed="0" failed="0" notExecuted="1" /></ResultSummary></TestRun>
'@

    $skipRejected = $false
    try {
        Assert-ReleaseGateTestResults -Path $skippedTrx -SuiteName 'controlled disabled provider'
    }
    catch {
        $skipRejected = $true
    }
    if (-not $skipRejected) {
        throw 'The release gate accepted a controlled skipped provider suite.'
    }

    $vulnerableJson = Join-Path $testRoot 'vulnerable.json'
    Set-Content -LiteralPath $vulnerableJson -Encoding UTF8 -Value @'
{"projects":[{"path":"controlled.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[],"transitivePackages":[{"id":"Controlled.Vulnerable","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High","advisoryurl":"https://example.invalid/advisory"}]}]}]}]}
'@

    $vulnerabilityRejected = $false
    try {
        Assert-ReleaseGateNuGetAudit -Path $vulnerableJson
    }
    catch {
        $vulnerabilityRejected = $true
    }
    if (-not $vulnerabilityRejected) {
        throw 'The release gate accepted a controlled vulnerable transitive package.'
    }

    $formatFailureRejected = $false
    try {
        Assert-ReleaseGateCommandSucceeded -Name 'controlled format verification' -ExitCode 2
    }
    catch {
        $formatFailureRejected = $true
    }
    if (-not $formatFailureRejected) {
        throw 'The release gate accepted a controlled formatting-policy failure.'
    }

    $rangeBase = New-ControlledGitRepository -Name 'committed-range'
    $rangeRepository = Join-Path $testRoot 'committed-range'
    Push-Location $rangeRepository
    try {
        Invoke-ReleaseGateGitDiffCheck -EventName 'pull_request' -BaseSha $rangeBase -HeadSha $rangeBase
        Set-Content -LiteralPath 'controlled.txt' -Value 'committed trailing whitespace   '
        Invoke-ControlledGit -Arguments @('add', 'controlled.txt')
        Invoke-ControlledGit -Arguments @('-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'bad whitespace')
        $rangeHead = (& git rev-parse HEAD).Trim()

        Assert-ControlledRejection -Name 'pull-request committed-range whitespace' -Probe {
            Invoke-ReleaseGateGitDiffCheck -EventName 'pull_request' -BaseSha $rangeBase -HeadSha $rangeHead
        }
        Assert-ControlledRejection -Name 'push committed-range whitespace' -Probe {
            Invoke-ReleaseGateGitDiffCheck -EventName 'push' -BeforeSha $rangeBase -HeadSha $rangeHead
        }
        Assert-ControlledRejection -Name 'new-branch committed whitespace' -Probe {
            Invoke-ReleaseGateGitDiffCheck -EventName 'push' -BeforeSha ('0' * 40) -HeadSha $rangeHead
        }
    }
    finally {
        Pop-Location
    }

    New-ControlledGitRepository -Name 'staged' | Out-Null
    Push-Location (Join-Path $testRoot 'staged')
    try {
        Set-Content -LiteralPath 'controlled.txt' -Value 'staged trailing whitespace   '
        Invoke-ControlledGit -Arguments @('add', 'controlled.txt')
        Assert-ControlledRejection -Name 'staged whitespace' -Probe {
            Invoke-ReleaseGateGitDiffCheck
        }
    }
    finally {
        Pop-Location
    }

    New-ControlledGitRepository -Name 'working-tree' | Out-Null
    Push-Location (Join-Path $testRoot 'working-tree')
    try {
        Set-Content -LiteralPath 'controlled.txt' -Value 'working tree trailing whitespace   '
        Assert-ControlledRejection -Name 'working-tree whitespace' -Probe {
            Invoke-ReleaseGateGitDiffCheck
        }
    }
    finally {
        Pop-Location
    }

    Write-Host 'Release-gate policy self-test passed: skipped suites, vulnerable packages, formatting failures, and actual Git range/index/worktree whitespace failures were rejected.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
