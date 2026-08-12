param(
    [string]$ProjectId = "keyboard-wtf-prod",
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-prod") {
    throw "This script is intentionally restricted to keyboard-wtf-prod."
}

function ConvertTo-FirestoreValue([object]$Value) {
    if ($Value -is [System.Collections.IDictionary]) {
        $fields = [ordered]@{}
        foreach ($entry in $Value.GetEnumerator()) {
            $fields[[string]$entry.Key] = ConvertTo-FirestoreValue $entry.Value
        }
        return [ordered]@{ mapValue = [ordered]@{ fields = $fields } }
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return [ordered]@{ arrayValue = [ordered]@{
            values = @($Value | ForEach-Object { ConvertTo-FirestoreValue $_ })
        } }
    }
    if ($Value -is [bool]) { return [ordered]@{ booleanValue = $Value } }
    if ($Value -is [System.Byte] -or $Value -is [System.Int16] `
        -or $Value -is [System.Int32] -or $Value -is [System.Int64]) {
        return [ordered]@{ integerValue = ([long]$Value).ToString(
            [Globalization.CultureInfo]::InvariantCulture) }
    }
    return [ordered]@{ stringValue = [string]$Value }
}

function Pricing(
    [string]$Prefix,
    [string]$Version,
    [long]$InputRate,
    [long]$OutputRate,
    [long]$CachedRate,
    [long]$ReasoningRate,
    [long]$AudioInputRate) {
    [ordered]@{
        ($Prefix + "PricingVersion") = $Version
        ($Prefix + "InputUsdMicrosPerMillionTokens") = $InputRate
        ($Prefix + "OutputUsdMicrosPerMillionTokens") = $OutputRate
        ($Prefix + "CachedInputUsdMicrosPerMillionTokens") = $CachedRate
        ($Prefix + "ReasoningUsdMicrosPerMillionTokens") = $ReasoningRate
        ($Prefix + "InputAudioUsdMicrosPerMillionTokens") = $AudioInputRate
        ($Prefix + "UsdToInrPaise") = 10000L
        ($Prefix + "CreditMicrosPerInrPaise") = 10000L
    }
}

function Route(
    [string]$Name,
    [string]$PrimaryModel,
    [string]$PrimaryLocation,
    [System.Collections.IDictionary]$PrimaryPricing,
    [string]$FallbackModel,
    [string]$FallbackLocation,
    [System.Collections.IDictionary]$FallbackPricing,
    [int]$MaxOutput,
    [int]$Timeout,
    [int]$Thinking,
    [string]$Lifecycle) {
    $route = [ordered]@{
        version = "routes-production-2026-08-11"
        provider = "vertex"
        enabled = $true
        killSwitch = $false
        maxInputCharacters = 200000
        maxOutputTokens = $MaxOutput
        timeoutSeconds = $Timeout
        thinkingBudget = $Thinking
        canaryBasisPoints = 0
        minimumDesktopVersion = "1.4.0"
        lifecycleNote = $Lifecycle
        primaryModel = $PrimaryModel
        primaryLocation = $PrimaryLocation
    }
    foreach ($entry in $PrimaryPricing.GetEnumerator()) { $route[$entry.Key] = $entry.Value }
    if (-not [string]::IsNullOrWhiteSpace($FallbackModel)) {
        $route.fallbackModel = $FallbackModel
        $route.fallbackLocation = $FallbackLocation
        foreach ($entry in $FallbackPricing.GetEnumerator()) { $route[$entry.Key] = $entry.Value }
    }
    return $route
}

$flashLite = Pricing "primary" `
    "vertex-gemini-3.5-flash-lite-standard-global-fx100-2026-08-11" `
    300000L 2500000L 30000L 2500000L 300000L
$olderLiteFallback = Pricing "fallback" `
    "vertex-gemini-3.1-flash-lite-standard-global-fx100-2026-08-11" `
    250000L 1500000L 25000L 1500000L 500000L
$flash = Pricing "primary" `
    "vertex-gemini-3.5-flash-standard-global-fx100-2026-08-11" `
    1500000L 9000000L 150000L 9000000L 1500000L
$flashLiteFallback = Pricing "fallback" `
    "vertex-gemini-3.5-flash-lite-standard-global-fx100-2026-08-11" `
    300000L 2500000L 30000L 2500000L 300000L
$flashFallback = Pricing "fallback" `
    "vertex-gemini-3.5-flash-standard-global-fx100-2026-08-11" `
    1500000L 9000000L 150000L 9000000L 1500000L

$routes = [ordered]@{
    fast_text = Route "fast_text" "gemini-3.5-flash-lite" "global" $flashLite `
        "gemini-3.1-flash-lite" "global" $olderLiteFallback 2048 60 0 `
        "GA fast text route; server-owned fallback and pricing."
    selected_text = Route "selected_text" "gemini-3.5-flash-lite" "global" $flashLite `
        "gemini-3.1-flash-lite" "global" $olderLiteFallback 2048 60 0 `
        "GA selected-text route; server-owned fallback and pricing."
    agent_planner = Route "agent_planner" "gemini-3.5-flash" "global" $flash `
        "gemini-3.5-flash-lite" "global" $flashLiteFallback 4096 90 1024 `
        "GA reasoning route for complex desktop planning."
    browser_planner = Route "browser_planner" "gemini-3.5-flash-lite" "global" $flashLite `
        "gemini-3.5-flash" "global" $flashFallback 2048 75 512 `
        "Fast GA browser planner with a stronger GA fallback."
    screen_reasoning = Route "screen_reasoning" "gemini-3.5-flash" "global" $flash `
        "gemini-3.5-flash-lite" "global" $flashLiteFallback 2048 90 1024 `
        "GA multimodal screen and image reasoning route."
}

$live = Route "live_audio" "gemini-live-2.5-flash-native-audio" "us-central1" `
    (Pricing "primary" `
        "vertex-gemini-live-2.5-flash-native-audio-standard-fx100-2026-08-11" `
        500000L 2000000L 500000L 2000000L 3000000L) `
    "" "" ([ordered]@{}) 1 100 0 `
    "GA bounded Live audio route with cumulative high-water accounting."
$live.maxInputCharacters = 20000
$live.maxSessionSeconds = 600
$live.reservationWindowCreditMicros = 1000000L
$live.maxSessionCostCreditMicros = 5000000L
$live.primaryLiveInputTextUsdMicrosPerMillionTokens = 500000L
$live.primaryLiveInputAudioUsdMicrosPerMillionTokens = 3000000L
$live.primaryLiveInputImageUsdMicrosPerMillionTokens = 3000000L
$live.primaryLiveOutputTextUsdMicrosPerMillionTokens = 2000000L
$live.primaryLiveOutputAudioUsdMicrosPerMillionTokens = 12000000L
$routes.live_audio = $live

$document = [ordered]@{
    version = "routes-production-2026-08-11"
    routes = $routes
}
$fields = [ordered]@{}
foreach ($entry in $document.GetEnumerator()) {
    $fields[[string]$entry.Key] = ConvertTo-FirestoreValue $entry.Value
}
$payload = [ordered]@{ fields = $fields } | ConvertTo-Json -Depth 30

if (-not $Apply) {
    Write-Output "Validated six complete production model routes; no cloud state changed."
    return
}

$gcloud = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
$accessToken = (& $gcloud auth print-access-token).Trim()
if ([string]::IsNullOrWhiteSpace($accessToken)) { throw "Could not obtain a Google Cloud access token." }
try {
    $headers = @{ Authorization = "Bearer $accessToken"; "x-goog-user-project" = $ProjectId }
    $uri = "https://firestore.googleapis.com/v1/projects/$ProjectId/databases/(default)/documents/config/modelRouting"
    $saved = Invoke-RestMethod -Method Patch -Uri $uri -Headers $headers `
        -ContentType "application/json" -Body $payload
    $savedRoutes = @($saved.fields.routes.mapValue.fields.PSObject.Properties.Name)
    $missing = @($routes.Keys | Where-Object { $savedRoutes -notcontains $_ })
    if ($saved.fields.version.stringValue -ne "routes-production-2026-08-11" `
        -or $missing.Count -gt 0) {
        throw "The saved production model routing failed verification."
    }
    Write-Output "Applied and verified six explicit production model routes."
}
finally { $accessToken = $null; $headers = $null }
