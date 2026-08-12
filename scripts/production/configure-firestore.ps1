param(
    [string]$ProjectId = "keyboard-wtf-prod",
    [string]$Database = "(default)"
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-prod" -or $Database -ne "(default)") {
    throw "This script is intentionally restricted to the production default database."
}

$gcloud = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
if (-not (Test-Path -LiteralPath $gcloud)) { throw "Google Cloud CLI was not found." }
$rulesPath = Join-Path $PSScriptRoot "..\..\staging\backend\firestore.rules"
if (-not (Test-Path -LiteralPath $rulesPath)) { throw "Firestore rules source was not found." }
$accessToken = (& $gcloud auth print-access-token).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw "An authenticated Google Cloud session is required."
}
$headers = @{
    Authorization = "Bearer $accessToken"
    "x-goog-user-project" = $ProjectId
}

function Deploy-DenyAllRules {
    $rules = Get-Content -LiteralPath $rulesPath -Raw
    if ($rules -notmatch 'allow read, write: if false') {
        throw "Production Firestore rules must deny all client reads and writes."
    }
    $body = [pscustomobject]@{
        source = [pscustomobject]@{
            # Windows PowerShell otherwise expands a nested string as an
            # object wrapper when serializing this API shape.
            files = @([pscustomobject]@{ name = "firestore.rules"; content = [string]$rules })
        }
    } | ConvertTo-Json -Depth 8
    try {
        $ruleset = Invoke-RestMethod -Method Post -Headers $headers `
            -ContentType "application/json" `
            -Uri "https://firebaserules.googleapis.com/v1/projects/$ProjectId/rulesets" `
            -Body $body
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { throw }
        $stream = $response.GetResponseStream()
        $reader = [IO.StreamReader]::new($stream)
        try { $detail = $reader.ReadToEnd() }
        finally { $reader.Dispose(); $stream.Dispose() }
        throw "Ruleset creation failed: $detail"
    }
    if ([string]::IsNullOrWhiteSpace($ruleset.name)) { throw "Ruleset creation failed." }

    $releaseName = "projects/$ProjectId/releases/cloud.firestore"
    $releaseBody = [pscustomobject]@{
        release = [pscustomobject]@{ name = $releaseName; rulesetName = $ruleset.name }
        updateMask = "rulesetName"
    } | ConvertTo-Json -Depth 5
    try {
        $release = Invoke-RestMethod -Method Patch -Headers $headers `
            -ContentType "application/json" `
            -Uri "https://firebaserules.googleapis.com/v1/$releaseName" -Body $releaseBody
    }
    catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
        $createBody = [pscustomobject]@{ name = $releaseName; rulesetName = $ruleset.name } |
            ConvertTo-Json -Depth 4
        $release = Invoke-RestMethod -Method Post -Headers $headers `
            -ContentType "application/json" `
            -Uri "https://firebaserules.googleapis.com/v1/projects/$ProjectId/releases" `
            -Body $createBody
    }
    if ($release.rulesetName -ne $ruleset.name) { throw "Rules release verification failed." }
}

function Ensure-CompositeIndex {
    param(
        [string]$CollectionGroup,
        [string]$QueryScope,
        [string[]]$Fields)
    $existingJson = & $gcloud firestore indexes composite list --project=$ProjectId `
        --database=$Database --format=json | Out-String
    $existing = @($existingJson | ConvertFrom-Json | ForEach-Object { $_ })
    $wanted = @($Fields | ForEach-Object { ($_ -split ':', 2)[0] + ':ASCENDING' })
    $match = $existing | Where-Object {
        $_.name -match "/collectionGroups/$([Regex]::Escape($CollectionGroup))/indexes/" `
            -and $_.queryScope -eq $QueryScope.ToUpperInvariant().Replace('-', '_') `
            -and (@($_.fields | Where-Object { $_.fieldPath -ne "__name__" } |
                    ForEach-Object { $_.fieldPath + ':' + $_.order }) -join ',') -eq ($wanted -join ',')
    } | Select-Object -First 1
    if ($null -ne $match) { return }
    $arguments = @(
        "firestore", "indexes", "composite", "create",
        "--project=$ProjectId", "--database=$Database",
        "--collection-group=$CollectionGroup", "--query-scope=$QueryScope", "--async", "--quiet"
    )
    foreach ($field in $Fields) {
        $parts = $field -split ':', 2
        $arguments += "--field-config=field-path=$($parts[0]),order=$($parts[1])"
    }
    & $gcloud @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Creating the $CollectionGroup composite index failed." }
}

function Ensure-CollectionGroupField {
    param([string]$CollectionGroup, [string]$Field)
    $escapedDatabase = [Uri]::EscapeDataString($Database)
    $uri = "https://firestore.googleapis.com/v1/projects/$ProjectId/databases/$escapedDatabase/collectionGroups/$CollectionGroup/fields/$Field`?updateMask=indexConfig"
    $body = [ordered]@{
        indexConfig = [ordered]@{
            indexes = @(
                [ordered]@{ queryScope = "COLLECTION"; fields = @(
                    [ordered]@{ fieldPath = $Field; order = "ASCENDING" }) },
                [ordered]@{ queryScope = "COLLECTION"; fields = @(
                    [ordered]@{ fieldPath = $Field; order = "DESCENDING" }) },
                [ordered]@{ queryScope = "COLLECTION_GROUP"; fields = @(
                    [ordered]@{ fieldPath = $Field; order = "ASCENDING" }) },
                [ordered]@{ queryScope = "COLLECTION_GROUP"; fields = @(
                    [ordered]@{ fieldPath = $Field; order = "DESCENDING" }) }
            )
        }
    } | ConvertTo-Json -Depth 7
    [void](Invoke-RestMethod -Method Patch -Uri $uri -Headers $headers `
        -ContentType "application/json" -Body $body)
}

try {
    Deploy-DenyAllRules
    & $gcloud firestore databases update --project=$ProjectId --database=$Database `
        --enable-pitr --delete-protection --quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Enabling production Firestore durability failed." }

    $scheduleJson = & $gcloud firestore backups schedules list --project=$ProjectId `
        --database=$Database --format=json | Out-String
    $schedules = @($scheduleJson | ConvertFrom-Json | ForEach-Object { $_ })
    if (-not ($schedules | Where-Object { $_.PSObject.Properties.Name -contains "dailyRecurrence" })) {
        & $gcloud firestore backups schedules create --project=$ProjectId --database=$Database `
            --recurrence=daily --retention=7d --quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Creating the daily backup schedule failed." }
    }
    if (-not ($schedules | Where-Object { $_.PSObject.Properties.Name -contains "weeklyRecurrence" })) {
        & $gcloud firestore backups schedules create --project=$ProjectId --database=$Database `
            --recurrence=weekly --retention=14w --day-of-week=SUN --quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Creating the weekly backup schedule failed." }
    }

    Ensure-CompositeIndex "desktopSessions" "collection" @("userId:ascending", "status:ascending")
    Ensure-CompositeIndex "desktopSessions" "collection" @("userId:ascending", "deviceId:ascending", "status:ascending")
    Ensure-CompositeIndex "purchases" "collection-group" @("status:ascending", "updatedAt:ascending")
    Ensure-CompositeIndex "reservations" "collection-group" @("status:ascending", "expiresAt:ascending")
    foreach ($field in @("provider", "purchaseId", "providerObjectId", "providerPaymentId")) {
        Ensure-CollectionGroupField "purchases" $field
    }
    Ensure-CollectionGroupField "reservations" "status"

    $verifiedScheduleJson = & $gcloud firestore backups schedules list --project=$ProjectId `
        --database=$Database --format=json | Out-String
    $verifiedSchedules = @($verifiedScheduleJson | ConvertFrom-Json | ForEach-Object { $_ })
    $databaseStatusJson = & $gcloud firestore databases describe --project=$ProjectId `
        --database=$Database --format=json | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Reading production Firestore status failed." }
    $databaseStatus = $databaseStatusJson | ConvertFrom-Json
    $indexNames = @(& $gcloud firestore indexes composite list --project=$ProjectId `
        --database=$Database --format="value(name)")
    if ($LASTEXITCODE -ne 0) { throw "Reading production composite indexes failed." }
    [pscustomobject]@{
        ClientRules = "deny-all"
        DeleteProtection = $databaseStatus.deleteProtectionState
        PointInTimeRecovery = $databaseStatus.pointInTimeRecoveryEnablement
        BackupSchedules = $verifiedSchedules.Count
        CompositeIndexes = $indexNames.Count
    } | ConvertTo-Json
}
finally {
    $accessToken = $null
    $headers = $null
}
