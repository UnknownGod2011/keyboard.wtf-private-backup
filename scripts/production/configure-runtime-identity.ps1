param(
    [string]$ProjectId = "keyboard-wtf-prod",
    [string]$ServiceAccountName = "keyboard-wtf-paid-runtime"
)

$ErrorActionPreference = "Stop"

$gcloudCommand = Get-Command gcloud.cmd -ErrorAction SilentlyContinue
$gcloudPath = if ($gcloudCommand) { $gcloudCommand.Source } else { $null }
if (-not $gcloudPath) {
    $localSdk = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
    if (Test-Path -LiteralPath $localSdk) {
        $gcloudPath = $localSdk
    }
}
if (-not $gcloudPath) {
    throw "Google Cloud CLI was not found."
}

$member = "serviceAccount:$ServiceAccountName@$ProjectId.iam.gserviceaccount.com"

# VerifyIdTokenAsync(checkRevoked: true) performs a read-only Firebase user
# lookup after cryptographic token validation. Keep that security check enabled
# and grant only the viewer role required for the revocation lookup.
& $gcloudPath projects add-iam-policy-binding $ProjectId `
    --member=$member `
    --role=roles/firebaseauth.viewer `
    --condition=None `
    --quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not grant Firebase Authentication Viewer to the runtime identity."
}

Write-Host "Production runtime Firebase revocation-check access is configured."
