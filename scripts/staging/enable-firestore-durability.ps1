param(
    [string]$ProjectId = "keyboard-wtf-staging",
    [string]$Database = "(default)"
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-staging") {
    throw "This script is intentionally restricted to keyboard-wtf-staging."
}

$gcloudCommand = Get-Command gcloud.cmd -ErrorAction SilentlyContinue
$gcloud = if ($null -ne $gcloudCommand) { $gcloudCommand.Source } else { $null }
if ([string]::IsNullOrWhiteSpace($gcloud)) {
    $gcloud = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
}
if (-not (Test-Path -LiteralPath $gcloud)) {
    throw "Google Cloud CLI was not found."
}

& $gcloud config set project $ProjectId | Out-Null
& $gcloud firestore databases update --database=$Database --enable-pitr --quiet
if ($LASTEXITCODE -ne 0) { throw "Enabling Firestore PITR failed." }

$existing = & $gcloud firestore backups schedules list --database=$Database --format=json | ConvertFrom-Json
if (-not ($existing | Where-Object { $_.recurrence -eq "DAILY" })) {
    & $gcloud firestore backups schedules create --database=$Database --recurrence=daily --retention=7d --quiet
    if ($LASTEXITCODE -ne 0) { throw "Creating the daily Firestore backup schedule failed." }
}
if (-not ($existing | Where-Object { $_.recurrence -eq "WEEKLY" })) {
    & $gcloud firestore backups schedules create --database=$Database --recurrence=weekly --retention=14w --day-of-week=SUN --quiet
    if ($LASTEXITCODE -ne 0) { throw "Creating the weekly Firestore backup schedule failed." }
}

& $gcloud firestore databases describe --database=$Database --format="yaml(name,pointInTimeRecoveryEnablement)"
& $gcloud firestore backups schedules list --database=$Database --format="table(name,recurrence,retention)"
