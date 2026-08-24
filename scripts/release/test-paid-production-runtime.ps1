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
$nativeHostRegistry = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\wtf.keyboard.browser"
$startupRegistry = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$priorNativeHostKeyExists = Test-Path -LiteralPath $nativeHostRegistry
$priorNativeHostManifest = if ($priorNativeHostKeyExists) {
    (Get-Item -LiteralPath $nativeHostRegistry).GetValue("")
} else {
    $null
}
$priorNativeHostExists = -not [string]::IsNullOrWhiteSpace($priorNativeHostManifest)
$priorStartupCommand = (Get-Item -LiteralPath $startupRegistry).GetValue("keyboard.wtf")

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
        foreach ($port in 8090..8094) {
            try {
                $candidateSettings = Invoke-RestMethod `
                    -Uri "http://localhost:$port/api/settings" `
                    -TimeoutSec 2
                $reportedExecutable = [IO.Path]::GetFullPath(
                    [string]$candidateSettings.startupExecutablePath)
                if (-not [string]::Equals(
                    $reportedExecutable,
                    $executablePath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
                $settings = $candidateSettings
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
    if ($settings.browserAutomation.nativeHostRegistered -ne $true) {
        throw "Release setup check failed: the Native Messaging host is not registered."
    }

    [ordered]@{
        executable = $executablePath
        settingsPort = $settingsPort
        paidAccountEnabled = $true
        signedIn = $false
        emergencyByokEnabled = $false
        nativeHostRegistered = $true
        cleanDataCreated = Test-Path -LiteralPath $validationData
    } | ConvertTo-Json
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(5000)
    }
    Get-CimInstance Win32_Process -Filter "Name = 'keyboard.wtf.exe'" |
        Where-Object {
            $_.ExecutablePath -and
            [string]::Equals(
                [IO.Path]::GetFullPath($_.ExecutablePath),
                $executablePath,
                [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

    if ($priorNativeHostExists) {
        New-Item -ItemType Directory -Path $nativeHostRegistry -Force | Out-Null
        (Get-Item -LiteralPath $nativeHostRegistry).SetValue("", $priorNativeHostManifest)
    }
    elseif (Test-Path -LiteralPath $nativeHostRegistry) {
        Remove-Item -LiteralPath $nativeHostRegistry -Force
    }
    if ($null -ne $priorStartupCommand) {
        Set-ItemProperty -LiteralPath $startupRegistry -Name "keyboard.wtf" -Value $priorStartupCommand
    }
    else {
        Remove-ItemProperty -LiteralPath $startupRegistry -Name "keyboard.wtf" -ErrorAction SilentlyContinue
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
