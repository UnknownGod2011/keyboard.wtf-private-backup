param(
    [string]$Configuration = "Release",
    [switch]$SkipSiteHashUpdate
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\KeyboardWtf.csproj"
$publishDir = Join-Path $root "dist\release\keyboard-wtf-win-x64"
$installerScript = Join-Path $root "installer\keyboard-wtf.iss"
$installerDir = Join-Path $root "dist\installer"
$siteIndex = Join-Path $root "site\index.html"
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$iscc = $isccCandidates | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 was not found in a supported installation path."
}

$dirty = @(git -C $root status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) {
    throw "Could not verify the release source state."
}
if ($dirty.Count -ne 0) {
    throw "Release packaging requires a clean committed worktree."
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

dotnet restore $project `
    --runtime win-x64 `
    --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "Locked win-x64 restore failed with exit code $LASTEXITCODE"
}

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
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
if (-not $SkipSiteHashUpdate) {
    $siteHtml = Get-Content -LiteralPath $siteIndex -Raw
    $updatedSiteHtml = $siteHtml -replace 'SHA256 [A-Fa-f0-9]{64}', "SHA256 $($hash.Hash)"
    if ($updatedSiteHtml -eq $siteHtml -and $siteHtml -notmatch "SHA256 $($hash.Hash)") {
        throw "Could not update the website installer hash"
    }
    [System.IO.File]::WriteAllText(
        $siteIndex,
        $updatedSiteHtml,
        [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Built: $installer"
Write-Host "Size: $sizeMb MB"
Write-Host "SHA256: $($hash.Hash)"
