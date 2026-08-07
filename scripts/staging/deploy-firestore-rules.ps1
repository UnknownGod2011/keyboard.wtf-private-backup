param(
    [string]$ProjectId = "keyboard-wtf-staging",
    [string]$RulesPath = "../../staging/backend/firestore.rules"
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-staging") {
    throw "This script is intentionally restricted to keyboard-wtf-staging."
}

$resolvedRulesPath = Join-Path $PSScriptRoot $RulesPath
if (-not (Test-Path -LiteralPath $resolvedRulesPath)) {
    throw "Firestore rules source was not found."
}

$gcloudCommand = Get-Command gcloud.cmd -ErrorAction SilentlyContinue
$gcloud = if ($null -ne $gcloudCommand) { $gcloudCommand.Source } else {
    Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
}
if (-not (Test-Path -LiteralPath $gcloud)) {
    throw "Google Cloud CLI was not found."
}

# Firebase Rules API requires an explicit quota project for a user access token.
# Keep both the source and the target project fixed to staging.
$accessToken = & $gcloud auth print-access-token
if ([string]::IsNullOrWhiteSpace($accessToken)) {
    throw "Could not obtain a Google Cloud access token."
}

$headers = @{
    Authorization = "Bearer $accessToken"
    "x-goog-user-project" = $ProjectId
}
$rules = Get-Content -Raw -LiteralPath $resolvedRulesPath
$rulesetPayload = [pscustomobject]@{
    source = [pscustomobject]@{
        # Use PSCustomObject rather than nested hashtables: Windows PowerShell
        # serializes a hashtable key named `content` as an object wrapper.
        files = @([pscustomobject]@{
            name = "firestore.rules"
            content = [string]$rules
        })
    }
} | ConvertTo-Json -Depth 8

try {
    $ruleset = Invoke-RestMethod `
        -Method Post `
        -Headers $headers `
        -ContentType "application/json" `
        -Uri "https://firebaserules.googleapis.com/v1/projects/$ProjectId/rulesets" `
        -Body $rulesetPayload
    if ([string]::IsNullOrWhiteSpace($ruleset.name)) {
        throw "Firebase Rules API did not return a ruleset name."
    }

    $releaseName = "projects/$ProjectId/releases/cloud.firestore"
    $releasePayload = [pscustomobject]@{
        release = [pscustomobject]@{
            name = $releaseName
            rulesetName = $ruleset.name
        }
        updateMask = "rulesetName"
    } | ConvertTo-Json -Depth 5
    try {
        # The default Firestore release normally already exists.  PATCH is the
        # documented update path and avoids replacing a named release blindly.
        $release = Invoke-RestMethod `
            -Method Patch `
            -Headers $headers `
            -ContentType "application/json" `
            -Uri "https://firebaserules.googleapis.com/v1/$releaseName" `
            -Body $releasePayload
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response -or [int]$response.StatusCode -ne 404) { throw }
        $createPayload = [pscustomobject]@{
            name = $releaseName
            rulesetName = $ruleset.name
        } | ConvertTo-Json -Depth 4
        $release = Invoke-RestMethod `
            -Method Post `
            -Headers $headers `
            -ContentType "application/json" `
            -Uri "https://firebaserules.googleapis.com/v1/projects/$ProjectId/releases" `
            -Body $createPayload
    }
    if ($release.rulesetName -ne $ruleset.name) {
        throw "Firestore release did not reference the newly created ruleset."
    }

    Write-Output "Deployed staging Firestore ruleset $($ruleset.name)."
}
finally {
    Remove-Variable accessToken -ErrorAction SilentlyContinue
}
