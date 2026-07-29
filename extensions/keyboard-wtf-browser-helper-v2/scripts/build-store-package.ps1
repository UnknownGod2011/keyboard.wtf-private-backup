param(
    [string]$ExtensionRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$extensionRootFull = [IO.Path]::GetFullPath($ExtensionRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $extensionRootFull "..\.."))
    $outputRootFull = Join-Path $repositoryRoot "dist\chrome-web-store"
}
else {
    $outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $extensionRootFull "..\.."))
if (-not $outputRootFull.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Store output directory must remain inside the repository."
}

$manifestPath = Join-Path $extensionRootFull "manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Extension manifest has no version."
}

$staging = Join-Path $outputRootFull "staging-$version"
if (Test-Path -LiteralPath $staging) {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    if (-not $resolvedStaging.StartsWith($outputRootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a staging directory outside the Store output directory."
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

foreach ($file in @(
    "manifest.json",
    "service-worker.js",
    "content-script.js",
    "popup.html",
    "popup.css",
    "popup.js"
)) {
    Copy-Item -LiteralPath (Join-Path $extensionRootFull $file) -Destination (Join-Path $staging $file)
}
Copy-Item -LiteralPath (Join-Path $extensionRootFull "icons") -Destination (Join-Path $staging "icons") -Recurse

$storeManifestPath = Join-Path $staging "manifest.json"
$storeManifest = Get-Content -LiteralPath $storeManifestPath -Raw | ConvertFrom-Json
$storeManifest.PSObject.Properties.Remove("key")
$storeManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $storeManifestPath -Encoding UTF8

$zipPath = Join-Path $outputRootFull "keyboard-wtf-chrome-helper-$version.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath -CompressionLevel Optimal

$validatedManifest = Get-Content -LiteralPath $storeManifestPath -Raw | ConvertFrom-Json
if ($validatedManifest.PSObject.Properties.Name -contains "key") {
    throw "The Chrome Web Store package still contains the development key."
}
foreach ($size in @(16, 32, 48, 128)) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging "icons\icon-$size.png"))) {
        throw "Missing required icon-$size.png."
    }
}

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
Write-Host "Chrome Web Store draft package: $zipPath"
Write-Host "Version: $version"
Write-Host "SHA256: $($hash.Hash)"
