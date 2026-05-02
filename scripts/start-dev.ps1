[CmdletBinding()]
param(
    [string]$Project,
    [string]$Configuration = "Debug",
    [int]$HttpsPort = 65503,
    [int]$HttpPort = 65504,
    [string]$Environment = "Development",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Project) {
    $Project = Join-Path $scriptRoot "..\youtubed\youtubed.csproj"
}

$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$projectPath = Resolve-Path $Project
$publishRoot = Join-Path $repoRoot "artifacts\dev-server"

if ($NoBuild) {
    $latestPublish = Get-ChildItem -Path $publishRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if (-not $latestPublish) {
        throw "No dev-server publish output exists under '$publishRoot'. Run without -NoBuild first."
    }

    $publishDir = $latestPublish.FullName
}
else {
    $runId = Get-Date -Format "yyyyMMdd-HHmmss"
    $publishDir = Join-Path $publishRoot $runId
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    dotnet publish $projectPath `
        --configuration $Configuration `
        --output $publishDir `
        --nologo `
        /p:UseAppHost=false
}

$appDll = Join-Path $publishDir "youtubed.dll"
if (-not (Test-Path $appDll)) {
    throw "Could not find published app at '$appDll'. Run without -NoBuild first."
}

$env:ASPNETCORE_ENVIRONMENT = $Environment
$env:ASPNETCORE_URLS = "https://localhost:$HttpsPort;http://localhost:$HttpPort"

Write-Host "Starting subscriptiond from $publishDir"
Write-Host "Listening on $env:ASPNETCORE_URLS"
Write-Host "Compiler output remains free because this process runs from artifacts\dev-server."

Push-Location $publishDir
try {
    dotnet $appDll
}
finally {
    Pop-Location
}
