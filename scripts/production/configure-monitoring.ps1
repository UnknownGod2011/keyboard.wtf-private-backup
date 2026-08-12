param(
    [string]$ProjectId = "keyboard-wtf-prod",
    [string]$NotificationChannel = ""
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-prod") {
    throw "This wrapper is intentionally restricted to keyboard-wtf-prod."
}

# Staging and production intentionally share the same metric filters and alert
# thresholds. Keep one reviewed implementation, then apply only these fixed
# environment labels so the two configurations cannot drift silently.
$sourcePath = Join-Path $PSScriptRoot "..\staging\configure-monitoring.ps1"
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $sourcePath))
$source = $source.Replace("keyboard-wtf-staging", "keyboard-wtf-prod")
$source = $source.Replace("Paid staging:", "Paid production:")
$source = $source.Replace("staging paid API", "production paid API")
$source = $source.Replace("staging API", "production API")
$source = $source.Replace("staging maintenance", "production maintenance")
$source = $source.Replace("in the staging project", "in the production project")

$script = [ScriptBlock]::Create($source)
& $script `
    -ProjectId $ProjectId `
    -NotificationChannel $NotificationChannel
