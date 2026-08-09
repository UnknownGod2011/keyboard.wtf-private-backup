param(
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = "Stop"
Push-Location $RepositoryRoot
try {
    $sourceFiles = @(git ls-files -co --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw "Could not enumerate source files." }
    $forbiddenTrackedExtensions = $sourceFiles | Where-Object {
        $_ -match '(?i)(\.p12|\.pfx|\.key|\.jks|\.keystore)$' -or
        $_ -match '(?i)(^|/)service-account[^/]*\.json$' -or
        $_ -match '(?i)(^|/)\.env($|\.)'
    }

    $patterns = @(
        '-----BEGIN ([A-Z0-9]+ )?PRIVATE KEY-----',
        'xai-[0-9A-Za-z_-]{20,}',
        'gsk_[0-9A-Za-z_-]{20,}',
        'sk-[0-9A-Za-z_-]{20,}',
        '"private_key"\s*:',
        '(api[_-]?key|client[_-]?secret|webhook[_-]?secret)\s*[:=]\s*["''][0-9A-Za-z+/_=-]{20,}'
    )
    $matchedFiles = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
    $textExtensions = @('.cs', '.js', '.html', '.json', '.yml', '.yaml', '.ps1', '.md', '.csproj', '.sln', '.iss', '.toml', '.py', '.txt', '.props', '.targets')
    foreach ($file in $sourceFiles) {
        if ($file -eq 'scripts/security/scan-secrets.ps1' -or
            $textExtensions -notcontains [IO.Path]::GetExtension($file).ToLowerInvariant()) { continue }
        $fullPath = Join-Path $RepositoryRoot $file
        if (-not (Test-Path -LiteralPath $fullPath) -or (Get-Item -LiteralPath $fullPath).Length -gt 5MB) { continue }
        $content = [IO.File]::ReadAllText($fullPath)
        foreach ($pattern in $patterns) {
            if ($file -eq 'staging/site/config.js' -and $pattern.StartsWith('(api', [StringComparison]::Ordinal)) {
                continue
            }
            if ([Text.RegularExpressions.Regex]::IsMatch($content, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                [void]$matchedFiles.Add($file)
                break
            }
        }
        if ($file -ne 'staging/site/config.js' -and
            [Text.RegularExpressions.Regex]::IsMatch($content, 'AIza[0-9A-Za-z_-]{30,}')) {
            [void]$matchedFiles.Add($file)
        }
    }

    $problems = @($forbiddenTrackedExtensions) + @($matchedFiles)
    if ($problems.Count -gt 0) {
        Write-Error ("Potential secret material is tracked in: " + (($problems | Sort-Object -Unique) -join ', '))
        exit 1
    }
    Write-Host "Tracked-source secret scan passed."
}
finally {
    Pop-Location
}
