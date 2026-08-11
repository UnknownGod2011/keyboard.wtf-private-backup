param(
    [string]$ProjectId = "keyboard-wtf-prod",
    [string]$RazorpayPlanId = "",
    [switch]$EnableAi,
    [switch]$EnableCheckout,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-prod") {
    throw "This script is intentionally restricted to the dedicated keyboard-wtf-prod project."
}
if ($Apply -and $RazorpayPlanId -notmatch '^plan_[A-Za-z0-9]+$') {
    throw "A production Razorpay plan ID is required before applying the catalog."
}
if ($EnableCheckout -and -not $Apply) {
    throw "Checkout cannot be enabled by a dry run."
}
if ($EnableAi -and -not $Apply) {
    throw "Paid AI cannot be enabled by a dry run."
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
    if ($Value -is [System.Byte] -or $Value -is [System.Int16] -or $Value -is [System.Int32] -or $Value -is [System.Int64]) {
        return [ordered]@{ integerValue = ([long]$Value).ToString([Globalization.CultureInfo]::InvariantCulture) }
    }
    if ($Value -is [DateTimeOffset]) {
        return [ordered]@{ timestampValue = $Value.UtcDateTime.ToString("o") }
    }
    return [ordered]@{ stringValue = [string]$Value }
}

$catalog = [ordered]@{
    version = "launch-monthly-inr999-v1"
    updatedAt = [DateTimeOffset]::UtcNow
    killSwitch = -not $EnableAi
    killSwitchMessage = if ($EnableAi) { "" } else { "Paid AI is not open yet." }
    paymentsKillSwitch = -not $EnableCheckout
    paymentsKillSwitchMessage = if ($EnableCheckout) { "" } else { "Payments are not open yet." }
    pricing = [ordered]@{
        version = "vertex-standard-global-fx100-2026-08-11"
        model = "gemini-3.5-flash-lite"
        inputUsdMicrosPerMillionTokens = 300000L
        outputUsdMicrosPerMillionTokens = 2500000L
        cachedInputUsdMicrosPerMillionTokens = 30000L
        reasoningUsdMicrosPerMillionTokens = 2500000L
        inputAudioUsdMicrosPerMillionTokens = 300000L
        usdToInrPaise = 10000L
        creditMicrosPerInrPaise = 10000L
        liveInputTextUsdMicrosPerMillionTokens = 500000L
        liveInputAudioUsdMicrosPerMillionTokens = 3000000L
        liveInputImageUsdMicrosPerMillionTokens = 3000000L
        liveOutputTextUsdMicrosPerMillionTokens = 2000000L
        liveOutputAudioUsdMicrosPerMillionTokens = 12000000L
    }
    products = @(
        [ordered]@{
            sku = "monthly_standard_inr_999_v1"
            kind = "subscription"
            displayName = "Jarvis Monthly"
            amountMinor = 99900L
            currency = "INR"
            creditMicros = 350000000L
            razorpayPlanId = $RazorpayPlanId
            enabled = $true
        }
    )
}

$fields = [ordered]@{}
foreach ($entry in $catalog.GetEnumerator()) {
    $fields[[string]$entry.Key] = ConvertTo-FirestoreValue $entry.Value
}
$payload = [ordered]@{ fields = $fields } | ConvertTo-Json -Depth 20

if (-not $Apply) {
    Write-Output "Validated launch catalog: INR 999 monthly, 350 credits, one subscription SKU, paid AI and checkout disabled until explicit apply/enable."
    return
}

$gcloud = (Get-Command gcloud.cmd -ErrorAction SilentlyContinue).Source
if (-not $gcloud) {
    $gcloud = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
}
if (-not (Test-Path -LiteralPath $gcloud)) { throw "Google Cloud CLI was not found." }
$accessToken = & $gcloud auth print-access-token
if ([string]::IsNullOrWhiteSpace($accessToken)) { throw "Could not obtain a Google Cloud access token." }
try {
    $headers = @{ Authorization = "Bearer $accessToken"; "x-goog-user-project" = $ProjectId }
    $uri = "https://firestore.googleapis.com/v1/projects/$ProjectId/databases/(default)/documents/config/catalog"
    $saved = Invoke-RestMethod -Method Patch -Uri $uri -Headers $headers -ContentType "application/json" -Body $payload
    $savedProducts = @($saved.fields.products.arrayValue.values).Count
    if ($saved.fields.version.stringValue -ne "launch-monthly-inr999-v1" -or $savedProducts -ne 1) {
        throw "The saved production catalog failed verification."
    }
    Write-Output "Applied and verified the production launch catalog with paid AI enabled=$EnableAi and checkout enabled=$EnableCheckout."
}
finally {
    Remove-Variable accessToken -ErrorAction SilentlyContinue
}
