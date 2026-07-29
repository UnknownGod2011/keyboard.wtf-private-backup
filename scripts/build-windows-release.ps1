param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\KeyboardWtf.csproj"
$publishDir = Join-Path $root "dist\release\keyboard-wtf-win-x64"
$installerScript = Join-Path $root "installer\keyboard-wtf.iss"
$installerDir = Join-Path $root "dist\installer"
$siteIndex = Join-Path $root "site\index.html"
$iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"

if (-not (Test-Path -LiteralPath $iscc)) {
    throw "Inno Setup 6 was not found at $iscc"
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

& $iscc $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$installer = Join-Path $installerDir "keyboard-wtf-setup.exe"
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
$sizeMb = [Math]::Round((Get-Item -LiteralPath $installer).Length / 1MB, 1)
$siteHtml = Get-Content -LiteralPath $siteIndex -Raw
$updatedSiteHtml = $siteHtml -replace 'SHA256 [A-Fa-f0-9]{64}', "SHA256 $($hash.Hash)"
if ($updatedSiteHtml -eq $siteHtml -and $siteHtml -notmatch "SHA256 $($hash.Hash)") {
    throw "Could not update the website installer hash"
}
[System.IO.File]::WriteAllText(
    $siteIndex,
    $updatedSiteHtml,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Built: $installer"
Write-Host "Size: $sizeMb MB"
Write-Host "SHA256: $($hash.Hash)"
