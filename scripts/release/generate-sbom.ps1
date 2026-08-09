param(
    [Parameter(Mandatory = $true)]
    [string]$BuildDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solution = Join-Path $root "KeyboardWtf.sln"
$outputDirectory = Join-Path $root "dist\sbom"
$output = Join-Path $outputDirectory "keyboard-wtf-$Version.cdx.json"

if (-not (Test-Path -LiteralPath $solution)) {
    throw "The keyboard.wtf solution was not found."
}
if (-not (Test-Path -LiteralPath $BuildDirectory)) {
    throw "The packaged build directory was not found."
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "The release version is invalid."
}

$dirty = @(git -C $root status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) {
    throw "Could not verify the release source state."
}
if ($dirty.Count -ne 0) {
    throw "SBOM generation requires a clean committed worktree."
}

$packageJson = dotnet list $solution package --include-transitive --format json
if ($LASTEXITCODE -ne 0) {
    throw "Could not enumerate NuGet dependencies for the SBOM."
}
$packageInventory = $packageJson | ConvertFrom-Json
$components = [ordered]@{}
foreach ($project in @($packageInventory.projects)) {
    foreach ($framework in @($project.frameworks)) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($null -eq $package -or [string]::IsNullOrWhiteSpace([string]$package.id)) {
                continue
            }
            $resolvedVersion = [string]$package.resolvedVersion
            $key = "$($package.id.ToLowerInvariant())@$resolvedVersion"
            if ($components.Contains($key)) {
                continue
            }
            $escapedId = [Uri]::EscapeDataString([string]$package.id)
            $escapedVersion = [Uri]::EscapeDataString($resolvedVersion)
            $components[$key] = [ordered]@{
                type = "library"
                group = "nuget"
                name = [string]$package.id
                version = $resolvedVersion
                purl = "pkg:nuget/$escapedId@$escapedVersion"
            }
        }
    }
}

$resolvedBuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
$packagedFiles = @(Get-ChildItem -LiteralPath $resolvedBuildDirectory -File -Recurse)
if ($packagedFiles.Count -eq 0) {
    throw "The packaged build directory is empty."
}
$fileComponents = foreach ($file in $packagedFiles) {
    $relativePath = $file.FullName.Substring($resolvedBuildDirectory.Length).TrimStart('\', '/')
    $sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{
        type = "file"
        name = $relativePath.Replace('\', '/')
        hashes = @([ordered]@{ alg = "SHA-256"; content = $sha256 })
        properties = @([ordered]@{ name = "keyboard.wtf:file-size"; value = [string]$file.Length })
    }
}

$commit = (git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw "Could not determine the exact source commit for the SBOM."
}

$document = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    serialNumber = "urn:uuid:$([Guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = [DateTimeOffset]::UtcNow.ToString("O")
        tools = [ordered]@{
            components = @([ordered]@{
                type = "application"
                name = "keyboard.wtf SBOM generator"
                version = "1"
            })
        }
        component = [ordered]@{
            type = "application"
            name = "keyboard.wtf"
            version = $Version
            properties = @(
                [ordered]@{ name = "keyboard.wtf:source-commit"; value = $commit },
                [ordered]@{ name = "keyboard.wtf:target"; value = "win-x64" },
                [ordered]@{ name = "keyboard.wtf:packaged-file-count"; value = [string]$packagedFiles.Count }
            )
        }
    }
    components = @(
        @($components.Values)
        @($fileComponents)
    ) | Sort-Object type, name, version
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $output -Encoding utf8
$verified = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
if ($verified.bomFormat -ne "CycloneDX" `
    -or @($verified.components).Count -eq 0 `
    -or @($verified.components | Where-Object { $_.type -eq "file" }).Count -ne $packagedFiles.Count) {
    throw "The generated SBOM failed validation."
}

Write-Output "Generated CycloneDX SBOM with $(@($verified.components).Count) components."
Write-Output $output
