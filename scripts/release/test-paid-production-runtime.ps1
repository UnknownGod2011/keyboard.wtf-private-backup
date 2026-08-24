param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
$executablePath = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Paid-production executable was not found: $executablePath"
}

$localAppData = [IO.Path]::GetFullPath(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
$validationData = Join-Path $localAppData (
    "keyboard.wtf-release-validation-" + [Guid]::NewGuid().ToString("N"))
$process = $null

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executablePath
    $startInfo.UseShellExecute = $false
    $startInfo.Environment["KEYBOARD_WTF_APPDATA_DIR"] = $validationData
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Paid-production executable did not start."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $settings = $null
    $settingsPort = 0
    while ($null -eq $settings -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        foreach ($port in 8090..8092) {
            try {
                $settings = Invoke-RestMethod `
                    -Uri "http://localhost:$port/api/settings" `
                    -TimeoutSec 2
                $settingsPort = $port
                break
            }
            catch {
                # The service is still starting or this port belongs to no app.
            }
        }
    }

    if ($null -eq $settings) {
        throw "The clean paid-production Settings service did not start within $TimeoutSeconds seconds."
    }
    if ($settings.paidAccount.enabled -ne $true) {
        throw "Release flavor check failed: paid Account & Billing is disabled."
    }
    if ($settings.paidAccount.signedIn -eq $true) {
        throw "Release flavor check failed: isolated clean data unexpectedly restored a user session."
    }
    if ($settings.paidAccount.emergencyByokEnabled -eq $true) {
        throw "Release flavor check failed: emergency BYOK is visible by default."
    }

    [ordered]@{
        executable = $executablePath
        settingsPort = $settingsPort
        paidAccountEnabled = $true
        signedIn = $false
        emergencyByokEnabled = $false
        cleanDataCreated = Test-Path -LiteralPath $validationData
    } | ConvertTo-Json
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(5000)
    }
    if (Test-Path -LiteralPath $validationData) {
        $resolved = [IO.Path]::GetFullPath($validationData)
        $insideLocalAppData = $resolved.StartsWith(
            $localAppData,
            [StringComparison]::OrdinalIgnoreCase)
        $expectedDirectoryName = [IO.Path]::GetFileName($resolved).StartsWith(
            "keyboard.wtf-release-validation-",
            [StringComparison]::Ordinal)
        if (-not $insideLocalAppData -or -not $expectedDirectoryName) {
            throw "Refusing to clean an unexpected validation directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
