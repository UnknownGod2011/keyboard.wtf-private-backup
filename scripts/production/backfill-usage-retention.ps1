param(
    [string]$ProjectId = "keyboard-wtf-prod",
    [string]$Database = "(default)",
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-prod" -or $Database -ne "(default)") {
    throw "This script is intentionally restricted to the production default database."
}

$gcloud = Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
if (-not (Test-Path -LiteralPath $gcloud)) { throw "Google Cloud CLI was not found." }
$accessToken = (& $gcloud auth print-access-token).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw "An authenticated Google Cloud session is required."
}
$headers = @{
    Authorization = "Bearer $accessToken"
    "x-goog-user-project" = $ProjectId
}
$documentsRoot = "https://firestore.googleapis.com/v1/projects/$ProjectId/databases/$Database/documents"

try {
    $queryBody = [ordered]@{
        structuredQuery = [ordered]@{
            select = [ordered]@{ fields = @(
                [ordered]@{ fieldPath = "accountingStatus" },
                [ordered]@{ fieldPath = "createdAt" },
                [ordered]@{ fieldPath = "detailExpiresAt" }
            ) }
            from = @([ordered]@{ collectionId = "usage"; allDescendants = $true })
        }
    } | ConvertTo-Json -Depth 10
    $rows = Invoke-RestMethod -Method Post -Headers $headers -ContentType "application/json" `
        -Uri "$documentsRoot`:runQuery" -Body $queryBody

    $writes = [Collections.Generic.List[object]]::new()
    $skippedUnsettled = 0
    $alreadyConfigured = 0
    $earliestExpiry = $null
    $latestExpiry = $null
    foreach ($row in $rows) {
        if ($null -eq $row.document) { continue }
        $document = $row.document
        if ($document.fields.accountingStatus.stringValue -ne "settled") {
            $skippedUnsettled++
            continue
        }
        if ($null -ne $document.fields.detailExpiresAt.timestampValue) {
            $alreadyConfigured++
            continue
        }
        $createdAt = [DateTimeOffset][string]$document.fields.createdAt.timestampValue
        # Legacy details did not retain their original cycle end. Monthly cycles
        # are at most 31 days, so 61 days after settlement conservatively keeps
        # roughly 30 days beyond the possible cycle boundary.
        $expiresAt = $createdAt.AddDays(61)
        if ($null -eq $earliestExpiry -or $expiresAt -lt $earliestExpiry) { $earliestExpiry = $expiresAt }
        if ($null -eq $latestExpiry -or $expiresAt -gt $latestExpiry) { $latestExpiry = $expiresAt }
        $writes.Add([ordered]@{
            update = [ordered]@{
                name = $document.name
                fields = [ordered]@{
                    detailExpiresAt = [ordered]@{
                        timestampValue = $expiresAt.UtcDateTime.ToString("O")
                    }
                }
            }
            updateMask = [ordered]@{ fieldPaths = @("detailExpiresAt") }
            currentDocument = [ordered]@{ updateTime = $document.updateTime }
        })
    }

    $updated = 0
    if ($Apply) {
        for ($offset = 0; $offset -lt $writes.Count; $offset += 250) {
            $count = [Math]::Min(250, $writes.Count - $offset)
            $batch = @($writes.GetRange($offset, $count))
            $body = [ordered]@{ writes = $batch } | ConvertTo-Json -Depth 12
            $response = Invoke-RestMethod -Method Post -Headers $headers `
                -ContentType "application/json" -Uri "$documentsRoot`:batchWrite" -Body $body
            $failures = @($response.status | Where-Object { $_.code -and [int]$_.code -ne 0 })
            if ($failures.Count -gt 0) {
                throw "A usage-retention backfill batch had $($failures.Count) failed writes."
            }
            $updated += @($response.writeResults).Count
        }
        if ($updated -ne $writes.Count) {
            throw "Usage-retention backfill wrote $updated of $($writes.Count) expected documents."
        }
    }

    [pscustomobject]@{
        Mode = if ($Apply) { "apply" } else { "dry-run" }
        EligibleSettledDetails = $writes.Count
        Updated = $updated
        AlreadyConfigured = $alreadyConfigured
        SkippedUnsettledOrAmbiguous = $skippedUnsettled
        EarliestExpiry = $earliestExpiry
        LatestExpiry = $latestExpiry
    } | ConvertTo-Json
}
finally {
    $accessToken = $null
    $headers = $null
    $rows = $null
    $writes = $null
}
