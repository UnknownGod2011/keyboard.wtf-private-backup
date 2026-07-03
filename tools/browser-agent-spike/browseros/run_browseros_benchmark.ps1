param(
    [string]$ServerUrl = "",
    [string]$OutputPath = "tools/browser-agent-spike/browseros/browseros-results.json"
)

$ErrorActionPreference = "Stop"
$results = @()

function Add-Result($task, $status, $durationMs, $note) {
    $script:results += [pscustomobject]@{
        backend = "BrowserOS external"
        task = $task
        status = $status
        durationMs = $durationMs
        note = $note
    }
}

function Run-Task($name, $url, $readOnlyInstruction) {
    $started = [Diagnostics.Stopwatch]::StartNew()
    try {
        $serverArgs = @()
        if ($ServerUrl) { $serverArgs = @("--server", $ServerUrl) }
        $opened = npx browseros-cli @serverArgs open --json $url | ConvertFrom-Json
        $page = $opened.page
        $text = npx browseros-cli @serverArgs -p $page read --format text --timeout 90s
        $snippet = ($text -join "`n")
        if ($snippet.Length -gt 500) { $snippet = $snippet.Substring(0, 500) }
        Add-Result $name "partial" $started.ElapsedMilliseconds "Opened and read page text. Agent reasoning/ranking still needs an LLM wrapper. Instruction: $readOnlyInstruction Snippet: $snippet"
        npx browseros-cli @serverArgs -p $page close | Out-Null
    }
    catch {
        Add-Result $name "failed" $started.ElapsedMilliseconds $_.Exception.Message
    }
}

try {
    $serverArgs = @()
    if ($ServerUrl) { $serverArgs = @("--server", $ServerUrl) }
    $health = npx browseros-cli @serverArgs health --json 2>&1
    $healthText = ($health | ForEach-Object { $_.ToString() }) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $healthText -match "not configured|not installed|Error:") {
        throw $healthText
    }
}
catch {
    Add-Result "health" "failed" 0 "BrowserOS is not installed/running/configured. Open BrowserOS Settings > BrowserOS MCP and provide the server URL."
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Get-Item -LiteralPath $OutputPath
    exit 0
}

Run-Task "example-read" "https://example.com/" "Read the page title."
Run-Task "devpost-read" "https://devpost.com/hackathons" "Read visible hackathon listings without joining/submitting."

$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Get-Item -LiteralPath $OutputPath
