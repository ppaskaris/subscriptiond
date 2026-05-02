[CmdletBinding()]
param(
    [string]$Project,
    [string]$Configuration = "Release",
    [switch]$AllowUntrustedCertificate
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
if (-not $Project) {
    $Project = Join-Path $repoRoot "youtubed\youtubed.csproj"
}

function Find-PublishSettingsFile {
    $searchRoot = Join-Path $repoRoot ".local\azure"

    $candidateFiles = @(
        Get-ChildItem -Path $searchRoot -Filter "*.PublishSettings" -File -ErrorAction SilentlyContinue
        Get-ChildItem -Path $searchRoot -Filter "*.publishsettings" -File -ErrorAction SilentlyContinue
    )
    $files = @($candidateFiles | Sort-Object FullName -Unique)

    if (-not $files) {
        throw "Place the downloaded Azure .PublishSettings file under '.local\azure'."
    }

    if ($files.Count -gt 1) {
        throw "Multiple publish settings files were found under '.local\azure'. Keep only the one for this app. Found: $($files.FullName -join ', ')"
    }

    return $files[0].FullName
}

function Read-WebDeployPublishSettings {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "Publish settings file was not found at '$Path'."
    }

    [xml]$settings = Get-Content $Path
    $profiles = @($settings.publishData.publishProfile | Where-Object { $_.publishMethod -eq "MSDeploy" })

    if (-not $profiles) {
        throw "No MSDeploy publish profile was found in '$Path'."
    }

    if ($profiles.Count -gt 1) {
        throw "Multiple MSDeploy publish profiles were found in '$Path'. Keep one MSDeploy profile in the local publish settings file."
    }

    return $profiles[0]
}

$publishSettingsPath = Find-PublishSettingsFile
$profile = Read-WebDeployPublishSettings -Path $publishSettingsPath
if (-not $profile.userName -or -not $profile.userPWD) {
    throw "The MSDeploy publish settings profile must include userName and userPWD."
}

$projectPath = Resolve-Path $Project
$msbuildProperties = @(
    "/p:DeployOnBuild=true",
    "/p:WebPublishMethod=MSDeploy",
    "/p:PublishProvider=AzureWebSite",
    "/p:MSDeployPublishMethod=WMSVC",
    "/p:MSDeployServiceURL=$($profile.publishUrl)",
    "/p:DeployIisAppPath=$($profile.msdeploySite)",
    "/p:UserName=$($profile.userName)",
    "/p:Password=$($profile.userPWD)",
    "/p:_DestinationType=AzureWebSite",
    "/p:LaunchSiteAfterPublish=false",
    "/p:UseAppHost=false"
)

if ($AllowUntrustedCertificate) {
    $msbuildProperties += "/p:AllowUntrustedCertificate=true"
}

Write-Host "Deploying '$($profile.msdeploySite)' directly to Azure App Service with Web Deploy."
dotnet build $projectPath --configuration $Configuration --nologo @msbuildProperties
