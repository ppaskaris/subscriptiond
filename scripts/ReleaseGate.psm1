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
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $InventoryPath
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "NuGet audit did not produce its required JSON file: $Path"
    }
    if (-not (Test-Path -LiteralPath $InventoryPath -PathType Leaf)) {
        throw "NuGet package inventory did not produce its required JSON file: $InventoryPath"
    }

    $audit = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $inventory = Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
    foreach ($document in @(
        @{ Name = 'NuGet audit'; Value = $audit; RequiredParameters = @('--vulnerable', '--include-transitive') },
        @{ Name = 'NuGet package inventory'; Value = $inventory; RequiredParameters = @('--include-transitive') }
    )) {
        if ($null -eq $document.Value -or $document.Value -isnot [pscustomobject]) {
            throw "$($document.Name) JSON root must be an object."
        }
        $versionProperty = $document.Value.PSObject.Properties['version']
        if ($null -eq $versionProperty -or $versionProperty.Value -ne 1) {
            throw "$($document.Name) JSON must declare supported schema version 1."
        }
        $parametersProperty = $document.Value.PSObject.Properties['parameters']
        if ($null -eq $parametersProperty -or $parametersProperty.Value -isnot [string]) {
            throw "$($document.Name) JSON must declare its invocation parameters."
        }
        $parameterTokens = @($parametersProperty.Value -split '\s+' | Where-Object { $_ })
        foreach ($requiredParameter in $document.RequiredParameters) {
            if ($parameterTokens -notcontains $requiredParameter) {
                throw "$($document.Name) JSON does not prove that $requiredParameter was requested."
            }
        }
        $projectsProperty = $document.Value.PSObject.Properties['projects']
        if ($null -eq $projectsProperty -or @($projectsProperty.Value).Count -eq 0) {
            throw "$($document.Name) JSON must contain at least one audited project."
        }
    }

    $sourcesProperty = $audit.PSObject.Properties['sources']
    $sources = @()
    if ($null -ne $sourcesProperty) {
        $sources = @($sourcesProperty.Value)
    }
    if ($sources.Count -eq 0 -or @($sources | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
        throw 'NuGet audit JSON must identify at least one package source used by the vulnerability scan.'
    }

    $inventoryProjectPaths = @()
    $topLevelPackageCount = 0
    $transitivePackageCount = 0
    foreach ($project in @($inventory.projects)) {
        $pathProperty = $project.PSObject.Properties['path']
        if ($null -eq $pathProperty -or $pathProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($pathProperty.Value)) {
            throw 'NuGet package inventory contains a project without a path.'
        }
        if ($inventoryProjectPaths -contains $pathProperty.Value) {
            throw "NuGet package inventory contains duplicate project path: $($pathProperty.Value)"
        }
        $inventoryProjectPaths += $pathProperty.Value

        $frameworksProperty = $project.PSObject.Properties['frameworks']
        if ($null -eq $frameworksProperty -or @($frameworksProperty.Value).Count -eq 0) {
            throw "NuGet package inventory contains no target frameworks for $($pathProperty.Value)."
        }
        foreach ($framework in @($frameworksProperty.Value)) {
            $frameworkNameProperty = $framework.PSObject.Properties['framework']
            if ($null -eq $frameworkNameProperty -or $frameworkNameProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($frameworkNameProperty.Value)) {
                throw "NuGet package inventory contains an unnamed target framework for $($pathProperty.Value)."
            }
            foreach ($packageGroupName in @('topLevelPackages', 'transitivePackages')) {
                $packageGroupProperty = $framework.PSObject.Properties[$packageGroupName]
                if ($null -eq $packageGroupProperty) {
                    throw "NuGet package inventory is missing $packageGroupName for $($pathProperty.Value) $($frameworkNameProperty.Value)."
                }
                foreach ($package in @($packageGroupProperty.Value)) {
                    $idProperty = $package.PSObject.Properties['id']
                    $resolvedVersionProperty = $package.PSObject.Properties['resolvedVersion']
                    if ($null -eq $idProperty -or $idProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($idProperty.Value) -or
                        $null -eq $resolvedVersionProperty -or $resolvedVersionProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($resolvedVersionProperty.Value)) {
                        throw "NuGet package inventory contains an incomplete $packageGroupName entry for $($pathProperty.Value) $($frameworkNameProperty.Value)."
                    }
                    if ($packageGroupName -eq 'topLevelPackages') {
                        $topLevelPackageCount++
                    }
                    else {
                        $transitivePackageCount++
                    }
                }
            }
        }
    }
    if ($topLevelPackageCount -eq 0 -or $transitivePackageCount -eq 0) {
        throw "NuGet package inventory must contain both direct and transitive packages (direct=$topLevelPackageCount, transitive=$transitivePackageCount)."
    }

    $auditProjectPaths = @()
    $findings = @()
    foreach ($project in @($audit.projects)) {
        $pathProperty = $project.PSObject.Properties['path']
        if ($null -eq $pathProperty -or $pathProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($pathProperty.Value)) {
            throw 'NuGet audit contains a project without a path.'
        }
        if ($auditProjectPaths -contains $pathProperty.Value) {
            throw "NuGet audit contains duplicate project path: $($pathProperty.Value)"
        }
        $auditProjectPaths += $pathProperty.Value

        $frameworksProperty = $project.PSObject.Properties['frameworks']
        if ($null -eq $frameworksProperty) {
            continue
        }
        if (@($frameworksProperty.Value).Count -eq 0) {
            throw "NuGet audit contains an empty frameworks collection for $($pathProperty.Value)."
        }
        foreach ($framework in @($frameworksProperty.Value)) {
            $frameworkNameProperty = $framework.PSObject.Properties['framework']
            if ($null -eq $frameworkNameProperty -or $frameworkNameProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($frameworkNameProperty.Value)) {
                throw "NuGet audit contains an unnamed target framework for $($pathProperty.Value)."
            }
            $frameworkPackageGroupCount = 0
            foreach ($packageGroupName in @('topLevelPackages', 'transitivePackages')) {
                $packageGroupProperty = $framework.PSObject.Properties[$packageGroupName]
                if ($null -eq $packageGroupProperty) {
                    continue
                }
                $frameworkPackageGroupCount++
                if (@($packageGroupProperty.Value).Count -eq 0) {
                    throw "NuGet audit contains an empty $packageGroupName collection for $($pathProperty.Value) $($frameworkNameProperty.Value)."
                }
                foreach ($package in @($packageGroupProperty.Value)) {
                    $idProperty = $package.PSObject.Properties['id']
                    $resolvedVersionProperty = $package.PSObject.Properties['resolvedVersion']
                    $vulnerabilitiesProperty = $package.PSObject.Properties['vulnerabilities']
                    if ($null -eq $idProperty -or $idProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($idProperty.Value) -or
                        $null -eq $resolvedVersionProperty -or $resolvedVersionProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($resolvedVersionProperty.Value) -or
                        $null -eq $vulnerabilitiesProperty -or @($vulnerabilitiesProperty.Value).Count -eq 0) {
                        throw "NuGet audit contains an incomplete vulnerable package entry for $($pathProperty.Value) $($frameworkNameProperty.Value)."
                    }
                    foreach ($vulnerability in @($vulnerabilitiesProperty.Value)) {
                        $severityProperty = $vulnerability.PSObject.Properties['severity']
                        $advisoryProperty = $vulnerability.PSObject.Properties['advisoryurl']
                        if ($null -eq $severityProperty -or $severityProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($severityProperty.Value) -or
                            $null -eq $advisoryProperty -or $advisoryProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($advisoryProperty.Value)) {
                            throw "NuGet audit contains incomplete vulnerability details for $($idProperty.Value)."
                        }
                        $findings += [pscustomobject] @{
                            Project = $pathProperty.Value
                            Framework = $frameworkNameProperty.Value
                            Package = $idProperty.Value
                            Version = $resolvedVersionProperty.Value
                            Severity = $severityProperty.Value
                            Advisory = $advisoryProperty.Value
                        }
                    }
                }
            }
            if ($frameworkPackageGroupCount -eq 0) {
                throw "NuGet audit contains no vulnerable package collection for $($pathProperty.Value) $($frameworkNameProperty.Value)."
            }
        }
    }

    $missingAuditProjects = @($inventoryProjectPaths | Where-Object { $auditProjectPaths -notcontains $_ })
    $unexpectedAuditProjects = @($auditProjectPaths | Where-Object { $inventoryProjectPaths -notcontains $_ })
    if ($missingAuditProjects.Count -ne 0 -or $unexpectedAuditProjects.Count -ne 0) {
        throw "NuGet audit and package inventory project sets do not match (missing=$($missingAuditProjects -join ','), unexpected=$($unexpectedAuditProjects -join ','))."
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
