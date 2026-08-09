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
Set-LogMetric "keyboard_wtf_paid_http_5xx" `
    "Staging paid API requests that returned a server error." `
    "$revision AND httpRequest.status>=500"
Set-LogMetric "keyboard_wtf_paid_auth_rejections" `
    "Rejected authentication or authorization requests to the staging paid API." `
    "$revision AND textPayload:API AND textPayload:request AND textPayload:rejected AND (textPayload:401 OR textPayload:403)"
Set-LogMetric "keyboard_wtf_paid_financial_warnings" `
    "Financial health warnings emitted only by the staging maintenance job." `
    "$job AND severity>=WARNING AND (textPayload:reconciliation OR textPayload:invariant OR textPayload:Stuck OR textPayload:kill OR (textPayload:spend AND textPayload:threshold) OR (textPayload:Global AND textPayload:daily AND textPayload:AI AND textPayload:spend))"
Set-LogMetric "keyboard_wtf_paid_maintenance_failures" `
    "Failed staging maintenance executions." `
    "$job AND severity>=ERROR"

if ([string]::IsNullOrWhiteSpace($NotificationChannel)) {
    Write-Warning "Log metrics are active. No verified notification channel was supplied, so no paging policy was enabled."
    Write-Output "After creating and verifying a Cloud Monitoring notification channel, rerun with -NotificationChannel projects/keyboard-wtf-staging/notificationChannels/CHANNEL_ID."
    exit 0
}

function Set-ThresholdPolicy {
    param(
        [string]$Name,
        [string]$Metric,
        [string]$ResourceType,
        [string]$Documentation)
    $existing = (& $gcloud monitoring policies list `
        --project $ProjectId `
        --format="value(name)" `
        --filter="displayName=$Name").Trim()
    $policy = [ordered]@{
        displayName = $Name
        combiner = "OR"
        enabled = $true
        notificationChannels = @($NotificationChannel)
        documentation = [ordered]@{
            content = $Documentation
            mimeType = "text/markdown"
        }
        conditions = @([ordered]@{
            displayName = $Name
            conditionThreshold = [ordered]@{
                filter = "metric.type=`"logging.googleapis.com/user/$Metric`" AND resource.type=`"$ResourceType`""
                comparison = "COMPARISON_GT"
                thresholdValue = 0
                duration = "0s"
                trigger = [ordered]@{ count = 1 }
            }
        })
    }
    if (-not [string]::IsNullOrWhiteSpace($existing)) {
        $policy["name"] = $existing
    }
    $policyFile = Join-Path ([System.IO.Path]::GetTempPath()) `
        ("keyboard-wtf-monitoring-" + [Guid]::NewGuid().ToString("N") + ".json")
    try {
        $policy | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $policyFile -Encoding utf8
        if ([string]::IsNullOrWhiteSpace($existing)) {
            & $gcloud monitoring policies create `
                --project $ProjectId `
                --policy-from-file=$policyFile `
                --quiet
        }
        else {
            & $gcloud monitoring policies update $existing `
                --project $ProjectId `
                --policy-from-file=$policyFile `
                --quiet
        }
        if ($LASTEXITCODE -ne 0) { throw "Could not configure alert policy $Name." }
    }
    finally {
        Remove-Item -LiteralPath $policyFile -Force -ErrorAction SilentlyContinue
    }
}

Set-ThresholdPolicy "Paid staging: webhook signature rejected" "keyboard_wtf_paid_webhook_signature_rejected" "cloud_run_revision" "Investigate rejected signatures. Do not replay or credit events manually without provider evidence."
Set-ThresholdPolicy "Paid staging: API failure" "keyboard_wtf_paid_api_failures" "cloud_run_revision" "Inspect the safe request correlation logs; do not expose prompts, tokens, or payment data."
Set-ThresholdPolicy "Paid staging: HTTP 5xx" "keyboard_wtf_paid_http_5xx" "cloud_run_revision" "Inspect correlated safe API logs and the latest Cloud Run revision."
Set-ThresholdPolicy "Paid staging: repeated auth rejection" "keyboard_wtf_paid_auth_rejections" "cloud_run_revision" "Review aggregate authentication rejections without logging tokens or user content."
Set-ThresholdPolicy "Paid staging: financial warning" "keyboard_wtf_paid_financial_warnings" "cloud_run_job" "Run the maintenance job and resolve quarantined records with provider evidence."
Set-ThresholdPolicy "Paid staging: maintenance job failure" "keyboard_wtf_paid_maintenance_failures" "cloud_run_job" "Inspect the failed job execution before retrying."
