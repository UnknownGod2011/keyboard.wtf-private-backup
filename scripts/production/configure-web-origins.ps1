param(
    [string]$ProjectId = "keyboard-wtf-prod",
    [string]$Region = "asia-south1",
    [string]$ServiceName = "keyboard-wtf-paid-api",
    [string]$MaintenanceJobName = "keyboard-wtf-paid-maintenance"
)

$ErrorActionPreference = "Stop"
$gcloud = Get-Command gcloud -ErrorAction SilentlyContinue
if (-not $gcloud) {
    $bundled = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
    if (-not (Test-Path -LiteralPath $bundled)) {
        throw "Google Cloud CLI was not found."
    }
    $gcloud = $bundled
}

$canonical = "https://keyboardwtf.com"
$legacy = "https://keyboard-wtf.vercel.app"
$updates = @(
    "PaidSystem__WebBaseUrl=$canonical",
    "PaidSystem__AllowedOrigins__0=$canonical",
    "PaidSystem__AllowedOrigins__1=$legacy"
) -join ","

& $gcloud run services update $ServiceName `
    --project $ProjectId `
    --region $Region `
    --update-env-vars $updates `
    --quiet
if ($LASTEXITCODE -ne 0) {
    throw "Cloud Run website-origin update failed."
}

& $gcloud run jobs update $MaintenanceJobName `
    --project $ProjectId `
    --region $Region `
    --update-env-vars $updates `
    --quiet
if ($LASTEXITCODE -ne 0) {
    throw "Cloud Run maintenance website-origin update failed."
}

Write-Host "Canonical website configured: $canonical"
Write-Host "Compatibility origin retained: $legacy"
