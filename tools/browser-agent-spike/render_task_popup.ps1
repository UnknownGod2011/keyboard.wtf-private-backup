param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$assemblyPath = Join-Path $RepoRoot 'bin\Release\bin\keyboard.wtf.dll'
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)

$managerType = $assembly.GetType('KeyboardWtf.Services.JarvisTaskManager', $true)
$settingsType = $assembly.GetType('KeyboardWtf.Services.SettingsService', $true)
$formType = $assembly.GetType('KeyboardWtf.TaskPanelForm', $true)
$stepStatusType = $assembly.GetType('KeyboardWtf.Models.JarvisTaskStepStatus', $true)
$taskStatusType = $assembly.GetType('KeyboardWtf.Models.JarvisTaskStatus', $true)

$manager = [Activator]::CreateInstance($managerType)
$settingsPath = Join-Path $RepoRoot 'output\task-popup\preview-settings.json'
$settings = [Activator]::CreateInstance($settingsType, @($settingsPath))
$form = [Activator]::CreateInstance($formType, @($manager, $settings))

function Invoke-Manager($name, [object[]]$args) {
    $method = $managerType.GetMethods() | Where-Object {
        $_.Name -eq $name -and $_.GetParameters().Count -eq $args.Count
    } | Select-Object -First 1
    if (-not $method) {
        throw "Method not found: $name/$($args.Count)"
    }
    $method.Invoke($manager, $args)
}

function New-DemoTask($title, $command) {
    $steps = [string[]]@(
        'Capture requested context',
        'Open controlled browser',
        'Inspect the page',
        'Perform safe browser actions',
        'Verify the result'
    )
    Invoke-Manager 'Create' @($title, $command, $steps, [string[]]@('Controlled browser access'), $null)
}

function Set-Step($task, [int]$index, $statusName, $detail) {
    $status = [Enum]::Parse($stepStatusType, $statusName)
    Invoke-Manager 'SetStepByIndex' @($task.TaskId, $index, $status, $detail) | Out-Null
}

function Capture-Form($name) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.Application]::DoEvents()
    $bitmap = New-Object System.Drawing.Bitmap $form.Width, $form.Height
    $form.DrawToBitmap($bitmap, [System.Drawing.Rectangle]::new(0, 0, $form.Width, $form.Height))
    $outDir = Join-Path $RepoRoot 'output\task-popup'
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $path = Join-Path $outDir $name
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Get-Item $path
}

try {
    $running = New-DemoTask 'Running task demo' 'Open the local mock form and keep the task running for overlay verification.'
    Set-Step $running 0 'Completed' 'No private context was requested.'
    Set-Step $running 1 'Completed' 'keyboard.wtf Browser Agent Mock Form'
    Set-Step $running 2 'Completed' 'keyboard.wtf Browser Agent Mock Form · 5 interactive elements'
    Set-Step $running 3 'Running' 'Waiting for the next safe browser action.'
    $managerType.GetMethod('Start').Invoke($manager, @($running.TaskId)) | Out-Null
    $form.Show()
    Capture-Form 'popup-running-final.png' | Out-Null
    $form.Hide()
    $managerType.GetMethod('Cancel').Invoke($manager, @($running.TaskId, 'Task cancelled. No changes were made.')) | Out-Null

    $clarify = New-DemoTask 'Clarification state demo' 'Open the local mock form and demonstrate an ambiguous project choice.'
    Set-Step $clarify 0 'Completed' 'No private context was requested.'
    Set-Step $clarify 1 'Completed' 'keyboard.wtf Browser Agent Mock Form'
    Set-Step $clarify 2 'Completed' 'keyboard.wtf Browser Agent Mock Form · 5 interactive elements'
    $optionList = [string[]]@('Choose project')
    $managerType.GetMethod('WaitForClarification').Invoke(
        $manager,
        @($clarify.TaskId, $clarify.Steps[3].Id, '2 page elements matched Choose project; clarification is required.', $optionList)
    ) | Out-Null
    $form.Show()
    Capture-Form 'popup-clarification-final.png' | Out-Null
    $form.Hide()
    $managerType.GetMethod('Cancel').Invoke($manager, @($clarify.TaskId, 'Task cancelled. No changes were made.')) | Out-Null

    $completed = New-DemoTask 'Safe mock form demo' 'Fill the local mock form with keyboard.wtf and preview it without submitting.'
    Set-Step $completed 0 'Completed' 'No private context was requested.'
    Set-Step $completed 1 'Completed' 'keyboard.wtf Browser Agent Mock Form'
    Set-Step $completed 2 'Completed' 'keyboard.wtf Browser Agent Mock Form · 5 interactive elements'
    Set-Step $completed 3 'Completed' 'Clicked Preview safely.'
    $managerType.GetMethod('Complete').Invoke($manager, @($completed.TaskId, 'I filled the form and stopped for review.')) | Out-Null
    $form.Show()
    Capture-Form 'popup-completed-final.png' | Out-Null
}
finally {
    $form.Dispose()
    $manager.Dispose()
}
