param(
    [string]$ProjectId = "keyboard-wtf-staging",
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-staging") {
    throw "This script is intentionally restricted to keyboard-wtf-staging."
}

$gcloudCommand = Get-Command gcloud.cmd -ErrorAction SilentlyContinue
$gcloud = if ($null -ne $gcloudCommand) { $gcloudCommand.Source } else {
    Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
}
if (-not (Test-Path -LiteralPath $gcloud)) {
    throw "Google Cloud CLI was not found."
}

function New-CandidateFields {
    param(
        [string]$Prefix,
        [string]$Model,
        [string]$Location,
        [string]$PricingVersion,
        [long]$InputRate,
        [long]$OutputRate,
        [long]$CachedInputRate,
        [long]$ReasoningRate,
        [long]$InputAudioRate,
        [long]$LiveInputText = 500000,
        [long]$LiveInputAudio = 3000000,
        [long]$LiveInputImage = 3000000,
        [long]$LiveOutputText = 2000000,
        [long]$LiveOutputAudio = 12000000
    )
    return ,([ordered]@{
        ($Prefix + "Model") = $Model
        ($Prefix + "Location") = $Location
        ($Prefix + "PricingVersion") = $PricingVersion
        ($Prefix + "InputUsdMicrosPerMillionTokens") = $InputRate
        ($Prefix + "OutputUsdMicrosPerMillionTokens") = $OutputRate
        ($Prefix + "CachedInputUsdMicrosPerMillionTokens") = $CachedInputRate
        ($Prefix + "ReasoningUsdMicrosPerMillionTokens") = $ReasoningRate
        ($Prefix + "InputAudioUsdMicrosPerMillionTokens") = $InputAudioRate
        ($Prefix + "UsdToInrPaise") = 10000L
        ($Prefix + "CreditMicrosPerInrPaise") = 10000L
        ($Prefix + "LiveInputTextUsdMicrosPerMillionTokens") = $LiveInputText
        ($Prefix + "LiveInputAudioUsdMicrosPerMillionTokens") = $LiveInputAudio
        ($Prefix + "LiveInputImageUsdMicrosPerMillionTokens") = $LiveInputImage
        ($Prefix + "LiveOutputTextUsdMicrosPerMillionTokens") = $LiveOutputText
        ($Prefix + "LiveOutputAudioUsdMicrosPerMillionTokens") = $LiveOutputAudio
    })
}

function New-Route {
    param(
        [string]$Lifecycle,
        [int]$MaxInput,
        [int]$MaxOutput,
        [int]$Timeout,
        [int]$Thinking,
        [System.Collections.IDictionary]$Primary,
        [System.Collections.IDictionary]$Fallback = $null,
        [System.Collections.IDictionary]$LivePolicy = $null
    )
    $route = [ordered]@{
        version = "routes-2026-08-10"
        provider = "vertex"
        enabled = $true
        killSwitch = $false
        maxInputCharacters = [long]$MaxInput
        maxOutputTokens = [long]$MaxOutput
        timeoutSeconds = [long]$Timeout
        thinkingBudget = [long]$Thinking
        canaryBasisPoints = 0L
        minimumDesktopVersion = "1.3.0"
        lifecycleNote = $Lifecycle
    }
    foreach ($entry in $Primary.GetEnumerator()) { $route[$entry.Key] = $entry.Value }
    if ($null -ne $Fallback) {
        foreach ($entry in $Fallback.GetEnumerator()) { $route[$entry.Key] = $entry.Value }
    }
    if ($null -ne $LivePolicy) {
        foreach ($entry in $LivePolicy.GetEnumerator()) { $route[$entry.Key] = $entry.Value }
    }
    return ,$route
}

function ConvertTo-FirestoreValue([object]$Value) {
    if ($Value -is [System.Collections.IDictionary]) {
        $fields = [ordered]@{}
        foreach ($entry in $Value.GetEnumerator()) {
            $fields[[string]$entry.Key] = ConvertTo-FirestoreValue $entry.Value
        }
        return [ordered]@{ mapValue = [ordered]@{ fields = $fields } }
    }
    if ($Value -is [bool]) { return [ordered]@{ booleanValue = $Value } }
    if ($Value -is [System.Byte] -or $Value -is [System.Int16] -or $Value -is [System.Int32] -or $Value -is [System.Int64]) {
        return [ordered]@{ integerValue = ([long]$Value).ToString([Globalization.CultureInfo]::InvariantCulture) }
    }
    if ($Value -is [DateTimeOffset]) {
        return [ordered]@{ timestampValue = $Value.UtcDateTime.ToString("o") }
    }
    return [ordered]@{ stringValue = [string]$Value }
}

$flashLitePrimary = New-CandidateFields -Prefix "primary" -Model "gemini-3.5-flash-lite" -Location "global" `
    -PricingVersion "vertex-gemini-3.5-flash-lite-standard-global-fx100-2026-08-10" -InputRate 300000 -OutputRate 2500000 -CachedInputRate 30000 -ReasoningRate 2500000 -InputAudioRate 300000
$flashLiteFallback = New-CandidateFields -Prefix "fallback" -Model "gemini-3.1-flash-lite" -Location "global" `
    -PricingVersion "vertex-gemini-3.1-flash-lite-standard-global-fx100-2026-08-10" -InputRate 250000 -OutputRate 1500000 -CachedInputRate 25000 -ReasoningRate 1500000 -InputAudioRate 500000
$flashPrimary = New-CandidateFields -Prefix "primary" -Model "gemini-3.5-flash" -Location "global" `
    -PricingVersion "vertex-gemini-3.5-flash-standard-global-fx100-2026-08-10" -InputRate 1500000 -OutputRate 9000000 -CachedInputRate 150000 -ReasoningRate 9000000 -InputAudioRate 1500000
$flashFallback = New-CandidateFields -Prefix "fallback" -Model "gemini-3.5-flash-lite" -Location "global" `
    -PricingVersion "vertex-gemini-3.5-flash-lite-standard-global-fx100-2026-08-10" -InputRate 300000 -OutputRate 2500000 -CachedInputRate 30000 -ReasoningRate 2500000 -InputAudioRate 300000
$browserFallback = New-CandidateFields -Prefix "fallback" -Model "gemini-3.5-flash" -Location "global" `
    -PricingVersion "vertex-gemini-3.5-flash-standard-global-fx100-2026-08-10" -InputRate 1500000 -OutputRate 9000000 -CachedInputRate 150000 -ReasoningRate 9000000 -InputAudioRate 1500000
$livePrimary = New-CandidateFields -Prefix "primary" -Model "gemini-live-2.5-flash-native-audio" -Location "us-central1" `
    -PricingVersion "vertex-gemini-live-2.5-flash-native-audio-standard-fx100-2026-08-10" -InputRate 500000 -OutputRate 2000000 -CachedInputRate 500000 -ReasoningRate 2000000 -InputAudioRate 3000000

$routes = [ordered]@{
    fast_text = New-Route -Lifecycle "Gemini 3.5 Flash-Lite GA; retirement 2027-07-21 or later." -MaxInput 200000 -MaxOutput 2048 -Timeout 60 -Thinking 0 -Primary $flashLitePrimary -Fallback $flashLiteFallback
    selected_text = New-Route -Lifecycle "Gemini 3.5 Flash-Lite GA; retirement 2027-07-21 or later." -MaxInput 200000 -MaxOutput 2048 -Timeout 60 -Thinking 0 -Primary $flashLitePrimary -Fallback $flashLiteFallback
    agent_planner = New-Route -Lifecycle "Gemini 3.5 Flash GA; retirement 2027-05-19 or later." -MaxInput 200000 -MaxOutput 4096 -Timeout 90 -Thinking 1024 -Primary $flashPrimary -Fallback $flashFallback
    browser_planner = New-Route -Lifecycle "Fast GA planner with a stronger compatible GA fallback." -MaxInput 200000 -MaxOutput 2048 -Timeout 75 -Thinking 512 -Primary $flashLitePrimary -Fallback $browserFallback
    screen_reasoning = New-Route -Lifecycle "Gemini 3.5 Flash GA multimodal reasoning route." -MaxInput 200000 -MaxOutput 2048 -Timeout 90 -Thinking 1024 -Primary $flashPrimary -Fallback $flashFallback
    live_audio = New-Route -Lifecycle "Gemini Live 2.5 Flash Native Audio GA; retirement 2026-12-13." -MaxInput 20000 -MaxOutput 1 -Timeout 100 -Thinking 0 -Primary $livePrimary -LivePolicy ([ordered]@{
        maxSessionSeconds = 110L
        reservationWindowCreditMicros = 1000000L
        maxSessionCostCreditMicros = 5000000L
    })
}

$document = [ordered]@{
    version = "routes-2026-08-10"
    updatedAt = [DateTimeOffset]::UtcNow
    routes = $routes
}
$fields = [ordered]@{}
foreach ($entry in $document.GetEnumerator()) {
    $fields[[string]$entry.Key] = ConvertTo-FirestoreValue $entry.Value
}
$payload = [ordered]@{ fields = $fields } | ConvertTo-Json -Depth 20

if (-not $Apply) {
    Write-Output "Validated staging model-routing payload: $($routes.Count) routes, Standard PayGo text location global, Live location us-central1. Re-run with -Apply to write it."
    return
}

$accessToken = & $gcloud auth print-access-token
if ([string]::IsNullOrWhiteSpace($accessToken)) {
    throw "Could not obtain a Google Cloud access token."
}
$headers = @{
    Authorization = "Bearer $accessToken"
    "x-goog-user-project" = $ProjectId
}
$uri = "https://firestore.googleapis.com/v1/projects/$ProjectId/databases/(default)/documents/config/modelRouting"
try {
    $saved = Invoke-RestMethod -Method Patch -Uri $uri -Headers $headers `
        -ContentType "application/json" -Body $payload
    $savedVersion = $saved.fields.version.stringValue
    $savedRoutes = @($saved.fields.routes.mapValue.fields.PSObject.Properties).Count
    if ($savedVersion -ne "routes-2026-08-10" -or $savedRoutes -ne $routes.Count) {
        throw "The saved model-routing document failed verification."
    }
    Write-Output "Applied and verified staging model routing $savedVersion with $savedRoutes routes."
}
finally {
    Remove-Variable accessToken -ErrorAction SilentlyContinue
}
