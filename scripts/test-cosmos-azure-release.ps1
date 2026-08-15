[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$AccountName,

    [Parameter(Mandatory = $true)]
    [string]$DatabaseName,

    [Parameter(Mandatory = $true)]
    [string]$AppServiceName,

    [string]$AppServiceResourceGroup = $ResourceGroup,

    [string]$EvidencePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-AzureCliJson {
    param([string[]]$Arguments)

    $result = @(& az @Arguments --subscription $SubscriptionId --only-show-errors --output json)
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI could not read the requested resource. No raw response was retained."
    }

    try {
        # Windows PowerShell sends native stdout to the pipeline one line at a time. Joining the
        # lines before parsing keeps multi-line objects and empty or single-item arrays consistent
        # with PowerShell 7 without retaining or echoing the control-plane response.
        $parsed = ConvertFrom-Json -InputObject ($result -join [Environment]::NewLine)
    }
    catch {
        throw "Azure CLI returned an unreadable response. No raw response was retained."
    }

    if ($parsed -is [System.Array]) {
        foreach ($item in $parsed) {
            Write-Output $item
        }
        return
    }

    return $parsed
}

function Assert-Equal {
    param(
        [object]$Actual,
        [object]$Expected,
        [string]$Description
    )

    if ($Actual -ne $Expected) {
        throw "$Description is '$Actual'; expected '$Expected'."
    }
}

function Get-SortedPaths {
    param([object[]]$Paths)

    return @($Paths | ForEach-Object { $_.path } | Sort-Object)
}

function Get-OptionalProperty {
    param(
        [object]$Object,
        [string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Assert-Paths {
    param(
        [object[]]$Actual,
        [string[]]$Expected,
        [string]$Description
    )

    $actualPaths = @(Get-SortedPaths -Paths $Actual)
    $expectedPaths = @($Expected | Sort-Object)
    if (($actualPaths -join "|") -ne ($expectedPaths -join "|")) {
        throw "$Description does not match the checked-in Cosmos policy."
    }
}

function Resolve-EvidenceOutputPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $resolvedPath -PathType Container) {
        throw "EvidencePath must name a file, not a directory."
    }

    $parentPath = [System.IO.Path]::GetDirectoryName($resolvedPath)
    if ([string]::IsNullOrWhiteSpace($parentPath)) {
        throw "EvidencePath must have a resolvable parent directory."
    }

    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) {
        New-Item -ItemType Directory -Path $parentPath -Force | Out-Null
    }

    return $resolvedPath
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI is required. Install it and sign in before running this read-only check."
}

$account = Invoke-AzureCliJson -Arguments @(
    "cosmosdb", "show",
    "--resource-group", $ResourceGroup,
    "--name", $AccountName
)
Assert-Equal -Actual $account.enableFreeTier -Expected $true `
    -Description "Cosmos lifetime free tier"
Assert-Equal -Actual @($account.locations).Count -Expected 1 `
    -Description "Cosmos region count"
if (@($account.capabilities | Where-Object { $_.name -eq "EnableServerless" }).Count -ne 0) {
    throw "The Cosmos account is serverless; the supported release shape is provisioned throughput."
}

$databaseThroughput = Invoke-AzureCliJson -Arguments @(
    "cosmosdb", "sql", "database", "throughput", "show",
    "--resource-group", $ResourceGroup,
    "--account-name", $AccountName,
    "--name", $DatabaseName
)
Assert-Equal -Actual $databaseThroughput.resource.throughput -Expected 1000 `
    -Description "Database manual throughput"
if ($null -ne $databaseThroughput.resource.autoscaleSettings) {
    throw "The Cosmos database uses autoscale; exactly 1,000 RU/s manual throughput is required."
}

$containers = @(Invoke-AzureCliJson -Arguments @(
    "cosmosdb", "sql", "container", "list",
    "--resource-group", $ResourceGroup,
    "--account-name", $AccountName,
    "--database-name", $DatabaseName
))
$expectedNames = @("channels", "lists", "shareLinks")
$actualNames = @($containers | ForEach-Object { $_.name } | Sort-Object)
Assert-Equal -Actual ($actualNames -join "|") -Expected ($expectedNames -join "|") `
    -Description "Cosmos container set"
$containers = @($containers | Sort-Object name)

$expectedPolicies = @{
    lists = @{
        DefaultTtl = -1
        Included = @("/*")
        Excluded = @('/"_etag"/?', "/token/?")
    }
    channels = @{
        DefaultTtl = $null
        Included = @("/*")
        Excluded = @('/"_etag"/?', "/videos/*")
    }
    shareLinks = @{
        DefaultTtl = -1
        Included = @("/createdAt/?", "/expiresAfter/?", "/listId/?", "/usedAt/?")
        Excluded = @('/"_etag"/?', "/*")
    }
}
$containerEvidence = @()

foreach ($container in $containers) {
    $policy = $expectedPolicies[$container.name]
    $indexingPolicy = $container.resource.indexingPolicy
    $defaultTtl = Get-OptionalProperty -Object $container.resource -Name "defaultTtl"
    $compositeIndexes = @(Get-OptionalProperty -Object $indexingPolicy `
        -Name "compositeIndexes" | Where-Object { $null -ne $_ })
    $spatialIndexes = @(Get-OptionalProperty -Object $indexingPolicy `
        -Name "spatialIndexes" | Where-Object { $null -ne $_ })
    $vectorIndexes = @(Get-OptionalProperty -Object $indexingPolicy `
        -Name "vectorIndexes" | Where-Object { $null -ne $_ })
    $fullTextIndexes = @(Get-OptionalProperty -Object $indexingPolicy `
        -Name "fullTextIndexes" | Where-Object { $null -ne $_ })
    $vectorEmbeddingPolicy = Get-OptionalProperty `
        -Object $container.resource -Name "vectorEmbeddingPolicy"
    $fullTextPolicy = Get-OptionalProperty -Object $container.resource -Name "fullTextPolicy"
    Assert-Equal -Actual ($container.resource.partitionKey.paths -join "|") -Expected "/id" `
        -Description "Partition key for container '$($container.name)'"
    Assert-Equal -Actual $defaultTtl -Expected $policy.DefaultTtl `
        -Description "TTL for container '$($container.name)'"
    Assert-Equal -Actual $indexingPolicy.automatic -Expected $true `
        -Description "Automatic indexing for container '$($container.name)'"
    Assert-Equal -Actual $indexingPolicy.indexingMode -Expected "consistent" `
        -Description "Indexing mode for container '$($container.name)'"
    Assert-Paths -Actual $indexingPolicy.includedPaths `
        -Expected $policy.Included -Description "Included paths for container '$($container.name)'"
    Assert-Paths -Actual $indexingPolicy.excludedPaths `
        -Expected $policy.Excluded -Description "Excluded paths for container '$($container.name)'"
    Assert-Equal -Actual $compositeIndexes.Count -Expected 0 `
        -Description "Composite indexes for container '$($container.name)'"
    Assert-Equal -Actual $spatialIndexes.Count -Expected 0 `
        -Description "Spatial indexes for container '$($container.name)'"
    Assert-Equal -Actual $vectorIndexes.Count -Expected 0 `
        -Description "Vector indexes for container '$($container.name)'"
    Assert-Equal -Actual $fullTextIndexes.Count -Expected 0 `
        -Description "Full-text indexes for container '$($container.name)'"
    Assert-Equal -Actual $vectorEmbeddingPolicy -Expected $null `
        -Description "Vector embedding policy for container '$($container.name)'"
    Assert-Equal -Actual $fullTextPolicy -Expected $null `
        -Description "Full-text policy for container '$($container.name)'"

    $containerEvidence += [ordered]@{
        Name = $container.name
        PartitionKey = $container.resource.partitionKey.paths
        DefaultTtl = $defaultTtl
        AutomaticIndexing = $indexingPolicy.automatic
        IndexingMode = $indexingPolicy.indexingMode
        IncludedPaths = @(Get-SortedPaths -Paths $indexingPolicy.includedPaths)
        ExcludedPaths = @(Get-SortedPaths -Paths $indexingPolicy.excludedPaths)
        CompositeIndexCount = $compositeIndexes.Count
        SpatialIndexCount = $spatialIndexes.Count
        VectorIndexCount = $vectorIndexes.Count
        FullTextIndexCount = $fullTextIndexes.Count
        HasVectorEmbeddingPolicy = ($null -ne $vectorEmbeddingPolicy)
        HasFullTextPolicy = ($null -ne $fullTextPolicy)
    }
}

$webApp = Invoke-AzureCliJson -Arguments @(
    "webapp", "show",
    "--resource-group", $AppServiceResourceGroup,
    "--name", $AppServiceName
)
$plan = Invoke-AzureCliJson -Arguments @(
    "appservice", "plan", "show",
    "--ids", $webApp.serverFarmId
)
$planCapacity = Get-OptionalProperty -Object $plan.sku -Name "capacity"
$maximumNumberOfWorkers = Get-OptionalProperty `
    -Object $plan.properties -Name "maximumNumberOfWorkers"
Assert-Equal -Actual $webApp.siteConfig.numberOfWorkers -Expected 1 `
    -Description "App Service instance count"
Assert-Equal -Actual $plan.properties.perSiteScaling -Expected $false `
    -Description "App Service per-site scaling"
Assert-Equal -Actual $plan.properties.elasticScaleEnabled -Expected $false `
    -Description "App Service elastic scale"

# Azure reports capacity=0 for an F1 plan even though the plan has one fixed worker. Accept that
# sentinel only for the Free/F1 shape when the plan's own maximum confirms that scale-out is
# impossible. Other SKUs report configured plan capacity directly and must report exactly one.
if ($planCapacity -eq 0) {
    Assert-Equal -Actual $plan.sku.name -Expected "F1" `
        -Description "Zero-capacity App Service plan SKU"
    Assert-Equal -Actual $plan.sku.tier -Expected "Free" `
        -Description "Zero-capacity App Service plan tier"
    Assert-Equal -Actual $maximumNumberOfWorkers -Expected 1 `
        -Description "Free App Service plan maximum worker count"
}
else {
    Assert-Equal -Actual $planCapacity -Expected 1 `
        -Description "App Service plan configured capacity"
}

$autoscaleSettings = @(Invoke-AzureCliJson -Arguments @(
    "resource", "list",
    "--resource-type", "Microsoft.Insights/autoscaleSettings"
))
if (@($autoscaleSettings | Where-Object {
            $_.properties.enabled -and
            $_.properties.targetResourceUri -eq $webApp.serverFarmId
        }).Count -ne 0) {
    throw "An enabled autoscale setting targets the App Service plan; scale-out must remain disabled."
}

$evidence = [ordered]@{
    CheckedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    FreeTierEnabled = $true
    RegionCount = 1
    Region = $account.locations[0].locationName
    BackupMode = $account.backupPolicy.type
    DatabaseManualThroughput = 1000
    Containers = $containerEvidence
    AppServicePlanSku = $plan.sku.name
    AppServicePlanReportedCapacity = $planCapacity
    AppServiceConfiguredWorkers = $webApp.siteConfig.numberOfWorkers
    AppServicePlanMaximumWorkers = $maximumNumberOfWorkers
    AppServicePerSiteScalingEnabled = $plan.properties.perSiteScaling
    AppServiceElasticScaleEnabled = $plan.properties.elasticScaleEnabled
    AppServiceAutoscaleEnabled = $false
}

$evidenceJson = $evidence | ConvertTo-Json -Depth 10
if ($EvidencePath) {
    $resolvedEvidencePath = Resolve-EvidenceOutputPath -Path $EvidencePath
    $evidenceJson | Set-Content -LiteralPath $resolvedEvidencePath -Encoding utf8
    Write-Host "Azure release-shape evidence was written without credentials or raw diagnostics."
}
else {
    $evidenceJson
}
