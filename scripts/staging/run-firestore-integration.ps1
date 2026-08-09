param(
    [string]$ProjectId = "keyboard-wtf-staging"
)

$ErrorActionPreference = "Stop"
if ($ProjectId -ne "keyboard-wtf-staging") {
    throw "Real Firestore integration tests are restricted to keyboard-wtf-staging."
}
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root "staging\backend\KeyboardWtf.Staging.Api.Tests\KeyboardWtf.Staging.Api.Tests.csproj"
$gcloudCommand = Get-Command gcloud.cmd -ErrorAction SilentlyContinue
$gcloud = if ($null -ne $gcloudCommand) { $gcloudCommand.Source } else {
    Join-Path $env:LOCALAPPDATA "Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
}
if (-not (Test-Path -LiteralPath $gcloud)) {
    throw "Google Cloud CLI was not found."
}

$accessToken = (& $gcloud auth print-access-token).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accessToken)) {
    throw "An authenticated Google Cloud staging session is required."
}

$previous = $env:KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN
try {
    $env:KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN = $accessToken
    dotnet test $project `
        --configuration Release `
        --no-restore `
        --filter "FullyQualifiedName~Firestore_"
    if ($LASTEXITCODE -ne 0) {
        throw "The real Firestore integration gate failed."
    }
}
finally {
    if ($null -eq $previous) {
        Remove-Item Env:\KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN -ErrorAction SilentlyContinue
    }
    else {
        $env:KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN = $previous
    }
    $accessToken = $null
}
