param(
    [string]$ProjectId = "keyboard-wtf-staging",
    [string]$NotificationChannel = ""
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

function Set-LogMetric {
    param([string]$Name, [string]$Description, [string]$Filter)
    $exists = $false
    try {
        & $gcloud logging metrics describe $Name --project $ProjectId 2>$null | Out-Null
        $exists = $LASTEXITCODE -eq 0
    }
    catch {
        # A missing metric is the normal first-run condition.
        $exists = $false
    }
    if ($exists) {
        & $gcloud logging metrics update $Name --project $ProjectId --description $Description --log-filter=$Filter --quiet
    }
    else {
        & $gcloud logging metrics create $Name --project $ProjectId --description $Description --log-filter=$Filter --quiet
    }
    if ($LASTEXITCODE -ne 0) { throw "Could not configure log metric $Name." }
}

$revision = 'resource.type="cloud_run_revision" AND resource.labels.service_name="keyboard-wtf-paid-api"'
$job = 'resource.type="cloud_run_job" AND resource.labels.job_name="keyboard-wtf-paid-maintenance"'
Set-LogMetric "keyboard_wtf_paid_webhook_signature_rejected" `
    "Razorpay webhook signatures rejected by the staging API." `
    "$revision AND textPayload:Razorpay AND textPayload:webhook AND textPayload:signature AND textPayload:rejected"
Set-LogMetric "keyboard_wtf_paid_api_failures" `
    "Unhandled staging paid API failures, without prompt or payment data." `
    "$revision AND severity>=ERROR AND textPayload:Unhandled AND textPayload:API AND textPayload:failure"
Set-LogMetric "keyboard_wtf_paid_financial_warnings" `
    "Financial health warnings emitted only by the staging maintenance job." `
    "$job AND severity>=WARNING AND (textPayload:reconciliation OR textPayload:invariant OR textPayload:Stuck OR textPayload:kill)"
Set-LogMetric "keyboard_wtf_paid_maintenance_failures" `
    "Failed staging maintenance executions." `
    "$job AND severity>=ERROR"

if ([string]::IsNullOrWhiteSpace($NotificationChannel)) {
    Write-Warning "Log metrics are active. No verified notification channel was supplied, so no paging policy was enabled."
    Write-Output "After creating and verifying a Cloud Monitoring notification channel, rerun with -NotificationChannel projects/keyboard-wtf-staging/notificationChannels/CHANNEL_ID."
    exit 0
}

function Set-ThresholdPolicy {
    param([string]$Name, [string]$Metric, [string]$Documentation)
    $existing = & $gcloud monitoring policies list --project $ProjectId --format="value(name)" --filter="displayName=$Name"
    if (-not [string]::IsNullOrWhiteSpace($existing)) { return }
    & $gcloud monitoring policies create `
        --project $ProjectId `
        --display-name $Name `
        --condition-display-name $Name `
        --condition-filter "metric.type=\"logging.googleapis.com/user/$Metric\" AND resource.type=\"global\"" `
        --if "> 0" `
        --duration "0s" `
        --trigger-count 1 `
        --notification-channels $NotificationChannel `
        --documentation $Documentation `
        --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not create alert policy $Name." }
}

Set-ThresholdPolicy "Paid staging: webhook signature rejected" "keyboard_wtf_paid_webhook_signature_rejected" "Investigate rejected signatures. Do not replay or credit events manually without provider evidence."
Set-ThresholdPolicy "Paid staging: API failure" "keyboard_wtf_paid_api_failures" "Inspect the safe request correlation logs; do not expose prompts, tokens, or payment data."
Set-ThresholdPolicy "Paid staging: financial warning" "keyboard_wtf_paid_financial_warnings" "Run the maintenance job and resolve quarantined records with provider evidence."
Set-ThresholdPolicy "Paid staging: maintenance job failure" "keyboard_wtf_paid_maintenance_failures" "Inspect the failed job execution before retrying."
