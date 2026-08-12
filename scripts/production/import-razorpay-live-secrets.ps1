param(
    [string]$ProjectId = "keyboard-wtf-prod",
    [string]$CredentialCsv = "",
    [switch]$Rotate
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-prod") {
    throw "This script is intentionally restricted to keyboard-wtf-prod."
}

$gcloud = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
if (-not (Test-Path -LiteralPath $gcloud)) {
    throw "Google Cloud CLI was not found."
}
$accessToken = (& $gcloud auth print-access-token).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw "An authenticated Google Cloud session is required."
}
$secretHeaders = @{ Authorization = "Bearer $accessToken" }

if ([string]::IsNullOrWhiteSpace($CredentialCsv)) {
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE "Downloads") `
        -File -Filter "*.csv" -ErrorAction SilentlyContinue | Where-Object {
            try {
                $row = Import-Csv -LiteralPath $_.FullName | Select-Object -First 1
                $names = @($row.PSObject.Properties.Name)
                $names -contains "key_id" -and $names -contains "key_secret"
            }
            catch { $false }
        })
    if ($candidates.Count -ne 1) {
        throw "Exactly one Razorpay credential CSV with key_id and key_secret columns is required in Downloads."
    }
    $CredentialCsv = $candidates[0].FullName
}

$resolvedCsv = (Resolve-Path -LiteralPath $CredentialCsv).Path
$downloads = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE "Downloads"))
if (-not $resolvedCsv.StartsWith($downloads + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The credential CSV must remain in the current user's Downloads directory."
}

$credential = Import-Csv -LiteralPath $resolvedCsv | Select-Object -First 1
$keyId = [string]$credential.key_id
$keySecret = [string]$credential.key_secret
if ($keyId -notmatch '^rzp_live_[A-Za-z0-9]+$') {
    throw "The credential CSV does not contain a Razorpay Live key ID."
}
if ([string]::IsNullOrWhiteSpace($keySecret) -or $keySecret.Length -lt 16) {
    throw "The credential CSV does not contain a usable Razorpay Live key secret."
}

function Invoke-GCloudSecretInput {
    param([string]$SecretName, [string]$Value)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
    $body = @{ payload = @{ data = $encoded } } | ConvertTo-Json -Depth 4
    [void](Invoke-RestMethod -Method Post `
        -Uri "https://secretmanager.googleapis.com/v1/projects/$ProjectId/secrets/$SecretName`:addVersion" `
        -Headers $secretHeaders -ContentType "application/json" -Body $body)
    $encoded = $null
    $body = $null
}

$secretNames = @(
    "razorpay-live-key-id",
    "razorpay-live-key-secret",
    "razorpay-live-webhook-secret",
    "razorpay-live-plan-id"
)
$existing = @(& $gcloud secrets list --project=$ProjectId --format="value(name)")
foreach ($secret in $secretNames) {
    if ($existing -notcontains $secret) {
        & $gcloud secrets create $secret --project=$ProjectId `
            --replication-policy=automatic --quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not create $secret." }
    }
}

function Has-EnabledVersion([string]$SecretName) {
    @(& $gcloud secrets versions list $SecretName --project=$ProjectId `
        --filter="state=ENABLED" --format="value(name)").Count -gt 0
}

if ($Rotate -or -not (Has-EnabledVersion "razorpay-live-key-id")) {
    Invoke-GCloudSecretInput "razorpay-live-key-id" $keyId
}
if ($Rotate -or -not (Has-EnabledVersion "razorpay-live-key-secret")) {
    Invoke-GCloudSecretInput "razorpay-live-key-secret" $keySecret
}
if ($Rotate -or -not (Has-EnabledVersion "razorpay-live-webhook-secret")) {
    $bytes = [byte[]]::new(48)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    $webhookSecret = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    Invoke-GCloudSecretInput "razorpay-live-webhook-secret" $webhookSecret
    $webhookSecret = $null
    [Array]::Clear($bytes, 0, $bytes.Length)
}

foreach ($secret in @(
        "razorpay-live-key-id",
        "razorpay-live-key-secret",
        "razorpay-live-webhook-secret")) {
    $versions = @(& $gcloud secrets versions list $secret --project=$ProjectId `
        --format=json | ConvertFrom-Json)
    $latest = $versions | Where-Object { $_.state -eq "ENABLED" } |
        Sort-Object { [long]($_.name -split '/')[-1] } -Descending |
        Select-Object -First 1
    if ($null -eq $latest) { throw "$secret has no enabled version after import." }
    foreach ($version in $versions | Where-Object {
            $_.state -eq "ENABLED" -and $_.name -ne $latest.name
        }) {
        $versionId = ($version.name -split '/')[-1]
        & $gcloud secrets versions disable $versionId --secret=$secret `
            --project=$ProjectId --quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not disable an obsolete $secret version." }
    }
}

$runtime = "serviceAccount:keyboard-wtf-paid-runtime@$ProjectId.iam.gserviceaccount.com"
foreach ($secret in $secretNames) {
    & $gcloud secrets add-iam-policy-binding $secret --project=$ProjectId `
        --member=$runtime --role=roles/secretmanager.secretAccessor `
        --condition=None --quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant runtime access to $secret." }
}

$states = foreach ($secret in $secretNames) {
    [pscustomobject]@{
        Secret = $secret
        EnabledVersions = @(& $gcloud secrets versions list $secret --project=$ProjectId `
            --filter="state=ENABLED" --format="value(name)").Count
    }
}

$keyId = $null
$keySecret = $null
$credential = $null
$accessToken = $null
$secretHeaders = $null
Write-Output ($states | ConvertTo-Json)
