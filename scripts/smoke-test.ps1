param(
    [int]$ObservationSeconds = 12,
    [string]$ExecutablePath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $projectRoot 'src\QuickDrop.App\bin\Release\net8.0-windows\QuickDrop.exe'
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$process = Start-Process -FilePath $resolvedExecutable -WorkingDirectory (Split-Path -Parent $resolvedExecutable) -WindowStyle Hidden -PassThru
Start-Sleep -Seconds $ObservationSeconds
$process.Refresh()

if ($process.HasExited) {
    throw "QuickDrop exited during the startup observation window with code $($process.ExitCode)."
}

$responding = $process.Responding
Stop-Process -Id $process.Id
$process.WaitForExit()
if (-not $responding) {
    throw 'QuickDrop remained open but did not respond during the startup observation window.'
}

Write-Host "QuickDrop startup smoke test passed after $ObservationSeconds seconds."
