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

$accessToken = (& $gcloud auth print-access-token).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw "An authenticated Google Cloud session is required."
}
$headers = @{ Authorization = "Bearer $accessToken" }

function Set-LogMetric {
    param([string]$Name, [string]$Description, [string]$Filter)
    $metricUri = "https://logging.googleapis.com/v2/projects/$ProjectId/metrics/$Name"
    $exists = $null
    try {
        $exists = Invoke-RestMethod -Method Get -Uri $metricUri -Headers $headers
    }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    }
    $body = [ordered]@{
        name = $Name
        description = $Description
        filter = $Filter
    } | ConvertTo-Json -Depth 4
    if ($null -eq $exists) {
        [void](Invoke-RestMethod -Method Post `
            -Uri "https://logging.googleapis.com/v2/projects/$ProjectId/metrics" `
            -Headers $headers -ContentType "application/json" -Body $body)
    }
    else {
        [void](Invoke-RestMethod -Method Put -Uri $metricUri -Headers $headers `
            -ContentType "application/json" -Body $body)
    }
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
    "$job AND severity>=WARNING AND (textPayload:reconciliation OR textPayload:invariant OR textPayload:Stuck OR textPayload:kill OR (textPayload:provider AND textPayload:cost AND textPayload:drift) OR (textPayload:spend AND textPayload:threshold) OR (textPayload:Global AND textPayload:daily AND textPayload:AI AND textPayload:spend))"
Set-LogMetric "keyboard_wtf_paid_maintenance_failures" `
    "Failed staging maintenance executions." `
    "$job AND severity>=ERROR"
Set-LogMetric "keyboard_wtf_paid_payment_mismatch" `
    "Payment mismatches or provider reconciliation failures." `
    "$job AND severity>=WARNING AND (textPayload:`"Payment reconciliation failed`" OR textPayload:`"Unresolved reconciliation mismatches`")"
Set-LogMetric "keyboard_wtf_paid_usage_backlog" `
    "Usage reconciliation failures or unresolved usage mismatches." `
    "$job AND severity>=WARNING AND (textPayload:`"Usage reconciliation failed`" OR textPayload:`"Unresolved reconciliation mismatches`")"
Set-LogMetric "keyboard_wtf_paid_stuck_reservation" `
    "Expired AI reservations that require reconciliation." `
    "$job AND severity>=WARNING AND textPayload:`"Stuck AI reservation detected`""
Set-LogMetric "keyboard_wtf_paid_ledger_invariant" `
    "Open financial ledger invariant failures." `
    "$job AND severity>=WARNING AND textPayload:`"Open financial invariant issues`""
Set-LogMetric "keyboard_wtf_paid_provider_cost_drift" `
    "Material internal-versus-provider billing drift." `
    "$job AND severity>=WARNING AND (textPayload:`"Provider cost drift detected`" OR textPayload:`"Provider cost drift reconciliation issues`")"
Set-LogMetric "keyboard_wtf_paid_per_user_spend" `
    "Per-user provider spend threshold exceeded." `
    "$job AND severity>=WARNING AND textPayload:`"Per-user spend threshold exceeded`""
Set-LogMetric "keyboard_wtf_paid_global_spend" `
    "Global daily provider spend threshold reached." `
    "$job AND severity>=WARNING AND textPayload:`"Global daily AI spend threshold reached`""
Set-LogMetric "keyboard_wtf_paid_vertex_failures" `
    "Vertex request or Live session failures." `
    "($revision OR $job) AND severity>=WARNING AND (textPayload:`"Vertex Live session failed`" OR textPayload:`"provider_error`" OR textPayload:`"provider failed`")"
Set-LogMetric "keyboard_wtf_paid_stuck_payment" `
    "Payment confirmation remained pending beyond the permitted window." `
    "$job AND severity>=WARNING AND textPayload:`"Stuck payment confirmation detected`""
Set-LogMetric "keyboard_wtf_paid_kill_switch" `
    "A payment or AI kill switch is active." `
    "$job AND severity>=WARNING AND textPayload:`"Operational kill switch active`""

if ([string]::IsNullOrWhiteSpace($NotificationChannel)) {
    Write-Warning "Log metrics are active. No verified notification channel was supplied, so no paging policy was enabled."
    Write-Output "After creating and verifying a Cloud Monitoring notification channel, rerun with -NotificationChannel projects/keyboard-wtf-staging/notificationChannels/CHANNEL_ID."
    exit 0
}

if ($NotificationChannel -notmatch "^projects/$([Regex]::Escape($ProjectId))/notificationChannels/[0-9]+$") {
    throw "The notification channel must be an exact resource in the staging project."
}

$channel = Invoke-RestMethod -Method Get `
    -Uri "https://monitoring.googleapis.com/v3/$NotificationChannel" `
    -Headers $headers
if (-not $channel.enabled -or $channel.type -ne "email") {
    throw "The supplied notification channel is not an enabled email channel."
}
$policiesUri = "https://monitoring.googleapis.com/v3/projects/$ProjectId/alertPolicies"
$existingResponse = Invoke-RestMethod -Method Get -Uri $policiesUri -Headers $headers
$script:ExistingPolicies = @($existingResponse.alertPolicies)

function Set-ThresholdPolicy {
    param(
        [string]$Name,
        [string]$Metric,
        [string]$ResourceType,
        [string]$Documentation)
    $existing = $script:ExistingPolicies | Where-Object {
        $_.displayName -eq $Name
    } | Select-Object -First 1
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
    if ($null -ne $existing) {
        $policy["name"] = $existing.name
    }
    $body = $policy | ConvertTo-Json -Depth 10
    if ($null -eq $existing) {
        $created = Invoke-RestMethod -Method Post -Uri $policiesUri -Headers $headers `
            -ContentType "application/json" -Body $body
        $script:ExistingPolicies += $created
    }
    else {
        $mask = [Uri]::EscapeDataString(
            "display_name,documentation,conditions,combiner,enabled,notification_channels")
        [void](Invoke-RestMethod -Method Patch `
            -Uri "https://monitoring.googleapis.com/v3/$($existing.name)?updateMask=$mask" `
            -Headers $headers -ContentType "application/json" -Body $body)
    }
}

function Set-LatencyPolicy {
    $name = "Paid staging: high API latency"
    $existing = $script:ExistingPolicies | Where-Object {
        $_.displayName -eq $name
    } | Select-Object -First 1
    $policy = [ordered]@{
        displayName = $name
        combiner = "OR"
        enabled = $true
        notificationChannels = @($NotificationChannel)
        documentation = [ordered]@{
            content = "The paid API p95 container request latency exceeded five seconds for five minutes."
            mimeType = "text/markdown"
        }
        conditions = @([ordered]@{
            displayName = $name
            conditionThreshold = [ordered]@{
                filter = 'metric.type="run.googleapis.com/request_latencies" AND resource.type="cloud_run_revision" AND resource.label."service_name"="keyboard-wtf-paid-api"'
                comparison = "COMPARISON_GT"
                thresholdValue = 5000
                duration = "300s"
                aggregations = @([ordered]@{
                    alignmentPeriod = "300s"
                    perSeriesAligner = "ALIGN_PERCENTILE_95"
                    crossSeriesReducer = "REDUCE_MAX"
                    groupByFields = @('resource.label."service_name"')
                })
                trigger = [ordered]@{ count = 1 }
            }
        })
    }
    if ($null -ne $existing) { $policy["name"] = $existing.name }
    $body = $policy | ConvertTo-Json -Depth 12
    if ($null -eq $existing) {
        $created = Invoke-RestMethod -Method Post -Uri $policiesUri -Headers $headers `
            -ContentType "application/json" -Body $body
        $script:ExistingPolicies += $created
    }
    else {
        $mask = [Uri]::EscapeDataString(
            "display_name,documentation,conditions,combiner,enabled,notification_channels")
        [void](Invoke-RestMethod -Method Patch `
            -Uri "https://monitoring.googleapis.com/v3/$($existing.name)?updateMask=$mask" `
            -Headers $headers -ContentType "application/json" -Body $body)
    }
}

Set-ThresholdPolicy "Paid staging: webhook signature rejected" "keyboard_wtf_paid_webhook_signature_rejected" "cloud_run_revision" "Investigate rejected signatures. Do not replay or credit events manually without provider evidence."
Set-ThresholdPolicy "Paid staging: API failure" "keyboard_wtf_paid_api_failures" "cloud_run_revision" "Inspect the safe request correlation logs; do not expose prompts, tokens, or payment data."
Set-ThresholdPolicy "Paid staging: HTTP 5xx" "keyboard_wtf_paid_http_5xx" "cloud_run_revision" "Inspect correlated safe API logs and the latest Cloud Run revision."
Set-ThresholdPolicy "Paid staging: repeated auth rejection" "keyboard_wtf_paid_auth_rejections" "cloud_run_revision" "Review aggregate authentication rejections without logging tokens or user content."
Set-ThresholdPolicy "Paid staging: financial warning" "keyboard_wtf_paid_financial_warnings" "cloud_run_job" "Run the maintenance job and resolve quarantined records with provider evidence."
Set-ThresholdPolicy "Paid staging: maintenance job failure" "keyboard_wtf_paid_maintenance_failures" "cloud_run_job" "Inspect the failed job execution before retrying."
Set-ThresholdPolicy "Paid staging: payment mismatch" "keyboard_wtf_paid_payment_mismatch" "cloud_run_job" "Resolve payment state only from authoritative Razorpay evidence."
Set-ThresholdPolicy "Paid staging: usage reconciliation backlog" "keyboard_wtf_paid_usage_backlog" "cloud_run_job" "Inspect quarantined reservations; never silently release ambiguous provider work."
Set-ThresholdPolicy "Paid staging: stuck AI reservation" "keyboard_wtf_paid_stuck_reservation" "cloud_run_job" "Run bounded usage reconciliation and inspect unresolved provider dispatches."
Set-ThresholdPolicy "Paid staging: ledger invariant failure" "keyboard_wtf_paid_ledger_invariant" "cloud_run_job" "Stop rollout until the immutable ledger and materialized balance reconcile."
Set-ThresholdPolicy "Paid staging: provider cost drift" "keyboard_wtf_paid_provider_cost_drift" "cloud_run_job" "Compare the closed window against the billing export before changing pricing."
Set-ThresholdPolicy "Paid staging: per-user runaway spend" "keyboard_wtf_paid_per_user_spend" "cloud_run_job" "Inspect aggregate account spend without exposing prompts or identity in alerts."
Set-ThresholdPolicy "Paid staging: global provider spend" "keyboard_wtf_paid_global_spend" "cloud_run_job" "Use the server kill switch if provider exposure is unsafe."
Set-ThresholdPolicy "Paid staging: Vertex error spike" "keyboard_wtf_paid_vertex_failures" "cloud_run_revision" "Inspect provider availability and retry rates before increasing traffic."
Set-ThresholdPolicy "Paid staging: stuck payment confirmation" "keyboard_wtf_paid_stuck_payment" "cloud_run_job" "Reconcile the provider object; never trust a browser callback."
Set-ThresholdPolicy "Paid staging: kill switch active" "keyboard_wtf_paid_kill_switch" "cloud_run_job" "Confirm the activation is intentional and keep checkout or AI fail-closed."
Set-LatencyPolicy

$accessToken = $null
