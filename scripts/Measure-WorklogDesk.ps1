param(
    [string]$Exe = "",
    [string]$Output = "",
    [switch]$SkipIdle
)
$ErrorActionPreference = "Stop"
$root = Join-Path $PSScriptRoot ".."
if (-not $Exe) { $Exe = Join-Path $root "src/WorklogDesk.Windows/bin/Release/net10.0-windows/WorklogDesk.Windows.exe" }
if (-not $Output) { $Output = Join-Path $root "artifacts/measurement" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$directory = New-Item -ItemType Directory -Path $Output -Force
$run = New-Item -ItemType Directory -Path (Join-Path $directory.FullName ([guid]::NewGuid().ToString("N")))
function Start-Worklog([string[]]$Inputs) {
    $parameters = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    $parameters.UseShellExecute = $false
    $parameters.CreateNoWindow = $true
    $parameters.WindowStyle = "Hidden"
    foreach ($inputArgument in $Inputs) { $parameters.ArgumentList.Add($inputArgument) }
    return [System.Diagnostics.Process]::Start($parameters)
}
function Run-Worklog([string[]]$Inputs, [string]$ExpectedFile) {
    $process = Start-Worklog $Inputs
    try {
        if (-not $process.WaitForExit(60000)) { throw "The app benchmark did not finish." }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $ExpectedFile)) {
            throw "The app benchmark did not write the expected result."
        }
    } finally {
        if (-not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

$benchmark = Join-Path $run.FullName "performance.json"
$view = Join-Path $run.FullName "daily-review.png"
Run-Worklog @("--benchmark", $benchmark) $benchmark
Run-Worklog @("--ui-benchmark", (Join-Path $run.FullName "demo"), $view) $view
$results = Get-Content -LiteralPath $benchmark -Raw | ConvertFrom-Json -AsHashtable
$results["Cpu"] = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$results["WindowsBuild"] = (Get-CimInstance Win32_OperatingSystem).BuildNumber
$results["ViewPng"] = $view
$results["ReviewMeasurement"] = "Actual ReviewWindow reload and native drawing, 30 samples, 16 issues and 8 entered worklogs"

if (-not $SkipIdle) {
    $idle = Start-Worklog @("--idle-benchmark", (Join-Path $run.FullName "idle"))
    $results["IdleMeasurement"] = "After opening and closing the synthetic review; real Win32 queries against a benchmark window and isolated idle checkpoint writes"
    try {
        Start-Sleep -Seconds 60
        $idle.Refresh()
        if ($idle.HasExited) { throw "The idle process exited early." }
        $beforeCpu = $idle.TotalProcessorTime.TotalSeconds
        $window = [System.Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds 60
        $idle.Refresh()
        $results["IdlePercentOfOneCore"] = [Math]::Round(
            100 * ($idle.TotalProcessorTime.TotalSeconds - $beforeCpu) / $window.Elapsed.TotalSeconds, 4)
        $results["IdleWorkingSetMiB"] = [Math]::Round($idle.WorkingSet64 / 1MB, 2)
        $results["IdlePrivateCommitMiB"] = [Math]::Round($idle.PrivateMemorySize64 / 1MB, 2)
        $counter = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($idle.Id)"
        if ($counter) { $results["IdlePrivateWorkingSetMiB"] = [Math]::Round($counter.WorkingSetPrivate / 1MB, 2) }
    } finally {
        if (-not $idle.HasExited) { $idle.Kill() }
        $idle.Dispose()
    }
}
$results | ConvertTo-Json | Set-Content -LiteralPath $benchmark
$results | ConvertTo-Json
Write-Output $benchmark
