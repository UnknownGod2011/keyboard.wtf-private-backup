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

& $gcloud firestore databases update `
    --project=$ProjectId `
    --database=$Database `
    --enable-pitr `
    --delete-protection `
    --quiet
if ($LASTEXITCODE -ne 0) { throw "Enabling Firestore PITR and delete protection failed." }

$existingJson = & $gcloud firestore backups schedules list `
    --project=$ProjectId `
    --database=$Database `
    --format=json | Out-String
if ($LASTEXITCODE -ne 0) { throw "Reading Firestore backup schedules failed." }
$parsedExisting = $existingJson | ConvertFrom-Json
$existing = @($parsedExisting | ForEach-Object { $_ })
if (-not ($existing | Where-Object { $_.PSObject.Properties.Name -contains "dailyRecurrence" })) {
    & $gcloud firestore backups schedules create `
        --project=$ProjectId `
        --database=$Database `
        --recurrence=daily `
        --retention=7d `
        --quiet
    if ($LASTEXITCODE -ne 0) { throw "Creating the daily Firestore backup schedule failed." }
}
if (-not ($existing | Where-Object { $_.PSObject.Properties.Name -contains "weeklyRecurrence" })) {
    & $gcloud firestore backups schedules create `
        --project=$ProjectId `
        --database=$Database `
        --recurrence=weekly `
        --retention=14w `
        --day-of-week=SUN `
        --quiet
    if ($LASTEXITCODE -ne 0) { throw "Creating the weekly Firestore backup schedule failed." }
}

$verifiedJson = & $gcloud firestore backups schedules list `
    --project=$ProjectId `
    --database=$Database `
    --format=json | Out-String
if ($LASTEXITCODE -ne 0) { throw "Verifying Firestore backup schedules failed." }
$parsedVerified = $verifiedJson | ConvertFrom-Json
$verified = @($parsedVerified | ForEach-Object { $_ })
$daily = @($verified | Where-Object { $_.PSObject.Properties.Name -contains "dailyRecurrence" })
$weekly = @($verified | Where-Object { $_.PSObject.Properties.Name -contains "weeklyRecurrence" })
if ($daily.Count -ne 1 -or [string]$daily[0].retention -ne "604800s") {
    throw "Firestore must have exactly one daily backup schedule with seven-day retention. Review extra or drifted schedules manually."
}
if ($weekly.Count -ne 1 `
    -or [string]$weekly[0].retention -ne "8467200s" `
    -or [string]$weekly[0].weeklyRecurrence.day -ne "SUNDAY") {
    throw "Firestore must have exactly one Sunday weekly backup schedule with fourteen-week retention. Review extra or drifted schedules manually."
}

& $gcloud firestore databases describe `
    --project=$ProjectId `
    --database=$Database `
    --format="yaml(name,pointInTimeRecoveryEnablement,deleteProtectionState)"
& $gcloud firestore backups schedules list `
    --project=$ProjectId `
    --database=$Database `
    --format="yaml(name,dailyRecurrence,weeklyRecurrence,retention)"
