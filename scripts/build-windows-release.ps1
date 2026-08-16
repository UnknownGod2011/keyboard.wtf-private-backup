param(
    [string]$Configuration = "Release",
    [switch]$SkipSiteHashUpdate,
    [switch]$SideBySideStaging,
    [switch]$PaidProductionCandidate,
    [switch]$PaidProductionRelease
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\KeyboardWtf.csproj"
if (@($SideBySideStaging, $PaidProductionCandidate, $PaidProductionRelease).Where({ $_ }).Count -gt 1) {
    throw "Choose only one paid staging, paid candidate, or paid production release mode."
}
$isSideBySide = $SideBySideStaging -or $PaidProductionCandidate
$publishName = if ($PaidProductionCandidate) {
    "keyboard-wtf-paid-production-candidate-win-x64"
} elseif ($SideBySideStaging) {
    "keyboard-wtf-paid-staging-win-x64"
} else {
    "keyboard-wtf-win-x64"
}
$publishDir = Join-Path $root "dist\release\$publishName"
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

$publishArguments = @(
    "publish", $project,
    "--configuration", $Configuration,
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "--output", $publishDir,
    "-p:PublishSingleFile=true",
    "-p:PublishTrimmed=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)
if ($PaidProductionCandidate) {
    $publishArguments += "-p:KeyboardWtfPaidProductionCandidate=true"
} elseif ($PaidProductionRelease) {
    $publishArguments += "-p:KeyboardWtfPaidProduction=true"
}
& dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$isccArguments = @()
if ($SideBySideStaging) {
    $isccArguments += @(
        '/DMyAppName=keyboard.wtf Paid Staging',
        '/DMyAppId={{C966E45B-AC9B-4192-AB9C-55D0CC33D9D4}',
        '/DMyDefaultDirName={localappdata}\Programs\keyboard.wtf Paid Staging',
        '/DMyOutputBaseFilename=keyboard-wtf-paid-staging-setup',
        '/DMySourceDir=..\dist\release\keyboard-wtf-paid-staging-win-x64',
        '/DMyRunValueName=keyboard.wtf Paid Staging',
        '/DMyDefaultGroupName=keyboard.wtf Paid Staging',
        '/DMyShortcutName=keyboard.wtf Paid Staging',
        '/DMyUserDataDir=keyboard.wtf-paid-staging',
        '/DMySkipStartup'
    )
}
if ($PaidProductionCandidate) {
    $isccArguments += @(
        '/DMyAppName=keyboard.wtf Paid Production Candidate',
        '/DMyAppId={{C7905F92-81A1-4824-9E31-26A5E532B50F}',
        '/DMyDefaultDirName={localappdata}\Programs\keyboard.wtf Paid Production Candidate',
        '/DMyOutputBaseFilename=keyboard-wtf-paid-production-candidate-setup',
        '/DMySourceDir=..\dist\release\keyboard-wtf-paid-production-candidate-win-x64',
        '/DMyRunValueName=keyboard.wtf Paid Production Candidate',
        '/DMyDefaultGroupName=keyboard.wtf Paid Production Candidate',
        '/DMyShortcutName=keyboard.wtf Paid Production Candidate',
        '/DMyUserDataDir=keyboard.wtf-paid-production-candidate'
    )
}
& $iscc @isccArguments $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$installerName = if ($PaidProductionCandidate) {
    "keyboard-wtf-paid-production-candidate-setup.exe"
} elseif ($SideBySideStaging) {
    "keyboard-wtf-paid-staging-setup.exe"
} else {
    "keyboard-wtf-setup.exe"
}
$installer = Join-Path $installerDir $installerName
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
$sizeMb = [Math]::Round((Get-Item -LiteralPath $installer).Length / 1MB, 1)
# The legacy BYOK website still consumes this inline hash. Paid production
# releases use production/site/config.js plus the signed release manifest and
# must never rewrite the legacy surface as a packaging side effect.
if (-not $SkipSiteHashUpdate -and -not $isSideBySide -and -not $PaidProductionRelease) {
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
