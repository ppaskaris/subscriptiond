Set-StrictMode -Version Latest

function Assert-ReleaseGateCommandSucceeded {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [int] $ExitCode
    )

    if ($ExitCode -ne 0) {
        throw "$Name failed with exit code $ExitCode."
    }
}

function Invoke-ReleaseGateGitDiffCheck {
    [CmdletBinding()]
    param(
        [string] $EventName,
        [string] $BaseSha,
        [string] $BeforeSha,
        [string] $HeadSha,
        [string] $LogPath
    )

    function Assert-CommitSha {
        param(
            [string] $Value,
            [string] $Name
        )

        if ($Value -notmatch '^[0-9a-fA-F]{40,64}$') {
            throw "$Name is not a full Git object id."
        }
        & git cat-file -e "$Value^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "$Name does not identify an available commit: $Value"
        }
    }

    function Invoke-GitCheck {
        param(
            [string] $Name,
            [string[]] $Arguments
        )

        Write-Host "Git whitespace check: $Name"
        $savedErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $output = @(& git @Arguments 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $savedErrorActionPreference
        }

        foreach ($line in $output) {
            Write-Host $line
            if ($LogPath) {
                Add-Content -LiteralPath $LogPath -Value ([string] $line)
            }
        }
        Assert-ReleaseGateCommandSucceeded -Name $Name -ExitCode $exitCode
    }

    if ($LogPath) {
        $logDirectory = Split-Path -Parent $LogPath
        if ($logDirectory) {
            New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        }
        Set-Content -LiteralPath $LogPath -Value '' -Encoding UTF8
    }

    switch ($EventName) {
        'pull_request' {
            Assert-CommitSha -Value $BaseSha -Name 'Pull request base SHA'
            Assert-CommitSha -Value $HeadSha -Name 'Pull request merge SHA'
            Invoke-GitCheck -Name 'Pull request committed range' -Arguments @('diff', '--check', $BaseSha, $HeadSha)
        }
        'push' {
            Assert-CommitSha -Value $HeadSha -Name 'Push SHA'
            if ($BeforeSha -match '^0{40,64}$') {
                $emptyTreeSha = (@() | & git mktree).Trim()
                Assert-ReleaseGateCommandSucceeded -Name 'Resolve the empty Git tree' -ExitCode $LASTEXITCODE
                Invoke-GitCheck -Name 'New-branch committed contents' -Arguments @('diff', '--check', $emptyTreeSha, $HeadSha)
            }
            else {
                Assert-CommitSha -Value $BeforeSha -Name 'Push before SHA'
                Invoke-GitCheck -Name 'Push committed range' -Arguments @('diff', '--check', $BeforeSha, $HeadSha)
            }
        }
        default {
            if ($EventName) {
                Write-Host "No committed-range whitespace check is defined for event '$EventName'; checking local changes."
            }
        }
    }

    Invoke-GitCheck -Name 'Staged changes' -Arguments @('diff', '--check', '--cached')
    Invoke-GitCheck -Name 'Working-tree changes' -Arguments @('diff', '--check')
}

function Assert-ReleaseGateTestResults {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $SuiteName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$SuiteName did not produce its required TRX file: $Path"
    }

    [xml] $trx = Get-Content -Raw -LiteralPath $Path
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        throw "$SuiteName TRX has no result counters."
    }

    $total = [int] $counters.total
    $executed = [int] $counters.executed
    $failed = [int] $counters.failed
    $notExecuted = [int] $counters.notExecuted

    if ($total -le 0) {
        throw "$SuiteName selected zero tests."
    }
    if ($notExecuted -ne 0 -or $executed -ne $total) {
        throw "$SuiteName contains skipped or otherwise unexecuted tests (total=$total, executed=$executed, notExecuted=$notExecuted)."
    }
    if ($failed -ne 0) {
        throw "$SuiteName contains $failed failed test(s)."
    }

    [pscustomobject] @{
        Suite = $SuiteName
        Total = $total
        Executed = $executed
        Failed = $failed
        Skipped = $notExecuted
    }
}

function Assert-ReleaseGateNuGetAudit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "NuGet audit did not produce its required JSON file: $Path"
    }

    $audit = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $findings = @()
    foreach ($project in @($audit.projects)) {
        $frameworksProperty = $project.PSObject.Properties['frameworks']
        if ($null -eq $frameworksProperty) {
            continue
        }
        foreach ($framework in @($frameworksProperty.Value)) {
            foreach ($packageGroupName in @('topLevelPackages', 'transitivePackages')) {
                $packageGroupProperty = $framework.PSObject.Properties[$packageGroupName]
                if ($null -eq $packageGroupProperty) {
                    continue
                }
                foreach ($package in @($packageGroupProperty.Value)) {
                    $vulnerabilitiesProperty = $package.PSObject.Properties['vulnerabilities']
                    if ($null -eq $vulnerabilitiesProperty) {
                        continue
                    }
                    foreach ($vulnerability in @($vulnerabilitiesProperty.Value)) {
                        $findings += [pscustomobject] @{
                            Project = $project.path
                            Framework = $framework.framework
                            Package = $package.id
                            Version = $package.resolvedVersion
                            Severity = $vulnerability.severity
                            Advisory = $vulnerability.advisoryurl
                        }
                    }
                }
            }
        }
    }

    if ($findings.Count -ne 0) {
        $summary = ($findings | ForEach-Object { "$($_.Package) $($_.Version) [$($_.Severity)]" }) -join ', '
        throw "NuGet reported vulnerable direct or transitive packages: $summary. See $Path for advisory details."
    }

    return $findings
}

function New-ReleaseGateCoverageReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $CoveragePaths,

        [Parameter(Mandatory = $true)]
        [string] $OutputDirectory,

        [Parameter(Mandatory = $true)]
        [double] $ProductionThreshold,

        [Parameter(Mandatory = $true)]
        [double] $ProviderThreshold
    )

    $lines = @{}
    foreach ($coveragePath in $CoveragePaths) {
        if (-not (Test-Path -LiteralPath $coveragePath -PathType Leaf)) {
            throw "A required coverage file is missing: $coveragePath"
        }

        [xml] $coverage = Get-Content -Raw -LiteralPath $coveragePath
        foreach ($class in @($coverage.coverage.packages.package.classes.class)) {
            $filename = ([string] $class.filename).Replace('/', '\')
            foreach ($line in @($class.lines.line)) {
                $key = "$filename|$($line.number)"
                $hits = [int] $line.hits
                if (-not $lines.ContainsKey($key) -or $hits -gt $lines[$key].Hits) {
                    $lines[$key] = [pscustomobject] @{
                        Filename = $filename
                        Line = [int] $line.number
                        Hits = $hits
                    }
                }
            }
        }
    }

    $productionLines = @($lines.Values | Where-Object { $_.Filename -notmatch '(^|\\)youtubed\.Tests(\\|$)' })
    $providerLines = @($productionLines | Where-Object { $_.Filename -match '(^|\\)Persistence(\\|$)' })

    if ($productionLines.Count -eq 0 -or $providerLines.Count -eq 0) {
        throw "Coverage data did not contain both production and persistence-provider source lines."
    }

    $productionCovered = @($productionLines | Where-Object { $_.Hits -gt 0 }).Count
    $providerCovered = @($providerLines | Where-Object { $_.Hits -gt 0 }).Count
    $productionPercent = [math]::Round(100.0 * $productionCovered / $productionLines.Count, 2)
    $providerPercent = [math]::Round(100.0 * $providerCovered / $providerLines.Count, 2)

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $summaryPath = Join-Path $OutputDirectory 'coverage-summary.md'
    $htmlPath = Join-Path $OutputDirectory 'coverage-report.html'
    $summary = @"
# Release gate coverage

| Scope | Covered | Coverable | Line coverage | Required |
| --- | ---: | ---: | ---: | ---: |
| Production (`youtubed`) | $productionCovered | $($productionLines.Count) | $productionPercent% | $ProductionThreshold% |
| Persistence providers | $providerCovered | $($providerLines.Count) | $providerPercent% | $ProviderThreshold% |
"@
    Set-Content -LiteralPath $summaryPath -Value $summary -Encoding UTF8

    $html = @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Release gate coverage</title>
<style>body{font-family:system-ui;margin:2rem}table{border-collapse:collapse}th,td{border:1px solid #bbb;padding:.5rem;text-align:right}th:first-child,td:first-child{text-align:left}</style>
</head><body><h1>Release gate coverage</h1><table><thead><tr><th>Scope</th><th>Covered</th><th>Coverable</th><th>Line coverage</th><th>Required</th></tr></thead>
<tbody><tr><td>Production (youtubed)</td><td>$productionCovered</td><td>$($productionLines.Count)</td><td>$productionPercent%</td><td>$ProductionThreshold%</td></tr>
<tr><td>Persistence providers</td><td>$providerCovered</td><td>$($providerLines.Count)</td><td>$providerPercent%</td><td>$ProviderThreshold%</td></tr></tbody></table></body></html>
"@
    Set-Content -LiteralPath $htmlPath -Value $html -Encoding UTF8

    if ($productionPercent -lt $ProductionThreshold) {
        throw "Production line coverage is $productionPercent%, below the required $ProductionThreshold%."
    }
    if ($providerPercent -lt $ProviderThreshold) {
        throw "Persistence-provider line coverage is $providerPercent%, below the required $ProviderThreshold%."
    }

    [pscustomobject] @{
        ProductionPercent = $productionPercent
        ProviderPercent = $providerPercent
        SummaryPath = $summaryPath
        HtmlPath = $htmlPath
    }
}

Export-ModuleMember -Function Assert-ReleaseGateCommandSucceeded, Invoke-ReleaseGateGitDiffCheck, Assert-ReleaseGateTestResults, Assert-ReleaseGateNuGetAudit, New-ReleaseGateCoverageReport
