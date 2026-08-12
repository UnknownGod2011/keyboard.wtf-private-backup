param([string]$ProjectId = "keyboard-wtf-prod")

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-prod") {
    throw "This script is intentionally restricted to keyboard-wtf-prod."
}
$gcloud = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
if (-not (Test-Path -LiteralPath $gcloud)) { throw "Google Cloud CLI was not found." }
$accessToken = (& $gcloud auth print-access-token).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw "An authenticated Google Cloud session is required."
}
$secretHeaders = @{ Authorization = "Bearer $accessToken" }

function Read-Secret([string]$Name) {
    $response = Invoke-RestMethod `
        -Uri "https://secretmanager.googleapis.com/v1/projects/$ProjectId/secrets/$Name/versions/latest`:access" `
        -Headers $secretHeaders
    $value = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($response.payload.data))
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Secret Manager has no enabled value for $Name."
    }
    return [string]$value
}

function Write-Secret([string]$Name, [string]$Value) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
    $body = @{ payload = @{ data = $encoded } } | ConvertTo-Json -Depth 4
    [void](Invoke-RestMethod -Method Post `
        -Uri "https://secretmanager.googleapis.com/v1/projects/$ProjectId/secrets/$Name`:addVersion" `
        -Headers $secretHeaders -ContentType "application/json" -Body $body)
    $encoded = $null
    $body = $null
}

$keyId = Read-Secret "razorpay-live-key-id"
$keySecret = Read-Secret "razorpay-live-key-secret"
if ($keyId -notmatch '^rzp_live_[A-Za-z0-9]+$') { throw "The configured key is not a Live key." }

$basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${keyId}:${keySecret}"))
$headers = @{ Authorization = "Basic $basic" }
$created = $false
try {
    $plans = Invoke-RestMethod -Uri "https://api.razorpay.com/v1/plans?count=100" -Headers $headers
    $matches = @($plans.items | Where-Object {
        $_.period -eq "monthly" -and [int]$_.interval -eq 1 `
            -and [long]$_.item.amount -eq 99900 `
            -and $_.item.currency -eq "INR" `
            -and $_.item.name -eq "keyboard.wtf Monthly"
    })
    if ($matches.Count -gt 1) {
        throw "Multiple identical production plans exist; refusing to choose one implicitly."
    }
    if ($matches.Count -eq 1) {
        $plan = $matches[0]
    }
    else {
        $payload = @{
            period = "monthly"
            interval = 1
            item = @{
                name = "keyboard.wtf Monthly"
                amount = 99900
                currency = "INR"
                description = "Monthly keyboard.wtf provider-backed allowance"
            }
            notes = @{ sku = "monthly_standard_inr_999_v1" }
        } | ConvertTo-Json -Depth 8
        $plan = Invoke-RestMethod -Method Post -Uri "https://api.razorpay.com/v1/plans" `
            -Headers $headers -ContentType "application/json" -Body $payload
        $created = $true
    }
    if ($plan.id -notmatch '^plan_[A-Za-z0-9]+$' `
        -or $plan.period -ne "monthly" `
        -or [int]$plan.interval -ne 1 `
        -or [long]$plan.item.amount -ne 99900 `
        -or $plan.item.currency -ne "INR") {
        throw "Razorpay returned a plan that does not match the production contract."
    }

    $enabledPlanVersions = @(& $gcloud secrets versions list razorpay-live-plan-id `
        --project=$ProjectId --filter="state=ENABLED" --format="value(name)")
    if ($enabledPlanVersions.Count -eq 0) {
        Write-Secret "razorpay-live-plan-id" ([string]$plan.id)
    }
    else {
        $stored = Read-Secret "razorpay-live-plan-id"
        if ($stored.Trim() -ne [string]$plan.id) {
            throw "The production plan differs from the plan already stored in Secret Manager."
        }
        $stored = $null
    }

    [pscustomobject]@{
        LiveApiAuthenticated = $true
        PlanCreated = $created
        Period = $plan.period
        Interval = [int]$plan.interval
        AmountMinor = [long]$plan.item.amount
        Currency = $plan.item.currency
        PlanSecretEnabled = $true
        ChargedMoney = $false
    } | ConvertTo-Json
}
finally {
    $keyId = $null
    $keySecret = $null
    $basic = $null
    $headers = $null
    $accessToken = $null
    $secretHeaders = $null
    $payload = $null
    $plan = $null
}
