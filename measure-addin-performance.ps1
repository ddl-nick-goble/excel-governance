# measure-addin-performance.ps1 - Sample Excel process CPU/memory over time for add-in perf baselining
[CmdletBinding()]
param(
    [int]$IntervalSeconds = 1,
    [int]$ExcelPid = 0
)

Write-Host "=== DGT Add-in Performance Sampler ===" -ForegroundColor Cyan
Write-Host ""

function Get-ProcessInstanceNameByPid {
    param([int]$Pid)
    $counter = Get-Counter -Counter "\Process(*)\ID Process" -ErrorAction Stop
    foreach ($sample in $counter.CounterSamples) {
        if ([int]$sample.CookedValue -eq $Pid) {
            return $sample.InstanceName
        }
    }
    return $null
}

$excelProcesses = Get-Process -Name "EXCEL" -ErrorAction SilentlyContinue
if (-not $excelProcesses) {
    Write-Host "Excel is not running. Please start Excel with the add-in loaded, then rerun." -ForegroundColor Yellow
    exit 1
}

if ($ExcelPid -eq 0) {
    if ($excelProcesses.Count -gt 1) {
        Write-Host "Multiple Excel processes found:" -ForegroundColor Yellow
        $excelProcesses | ForEach-Object { Write-Host "  - PID: $($_.Id)  CPU: $([math]::Round($_.CPU,2))  WS(MB): $([math]::Round($_.WorkingSet64/1MB,1))" }
        Write-Host ""
        $ExcelPid = [int](Read-Host "Enter the PID to monitor")
    } else {
        $ExcelPid = $excelProcesses[0].Id
    }
}

$instanceName = Get-ProcessInstanceNameByPid -Pid $ExcelPid
if (-not $instanceName) {
    Write-Host "Could not resolve performance counter instance for PID $ExcelPid." -ForegroundColor Red
    exit 1
}

Write-Host "Monitoring Excel PID $ExcelPid (instance: $instanceName)" -ForegroundColor Green
Write-Host "Interval: $IntervalSeconds sec | Mode: live dashboard (Ctrl+C to stop)" -ForegroundColor Green
Write-Host ""

$counterPaths = @(
    "\Process($instanceName)\% Processor Time",
    "\Process($instanceName)\Working Set - Private",
    "\Process($instanceName)\Working Set",
    "\Process($instanceName)\Private Bytes",
    "\Process($instanceName)\Handle Count",
    "\Process($instanceName)\Thread Count"
)

function Get-Sample {
    $counter = Get-Counter -Counter $counterPaths -SampleInterval $IntervalSeconds -MaxSamples 1
    $group = $counter.CounterSamples
    $ts = $group[0].TimeStamp
    [pscustomobject]@{
        Timestamp = $ts
        CpuPercent = [math]::Round(($group | Where-Object { $_.Path -like "*% Processor Time" }).CookedValue, 2)
        WorkingSetPrivateMB = [math]::Round((($group | Where-Object { $_.Path -like "*Working Set - Private" }).CookedValue) / 1MB, 2)
        WorkingSetMB = [math]::Round((($group | Where-Object { $_.Path -like "*Working Set" -and $_.Path -notlike "*Private*" }).CookedValue) / 1MB, 2)
        PrivateBytesMB = [math]::Round((($group | Where-Object { $_.Path -like "*Private Bytes" }).CookedValue) / 1MB, 2)
        HandleCount = [int](($group | Where-Object { $_.Path -like "*Handle Count" }).CookedValue)
        ThreadCount = [int](($group | Where-Object { $_.Path -like "*Thread Count" }).CookedValue)
    }
}

function Get-Average {
    param(
        [object[]]$Samples,
        [string]$Property
    )
    if (-not $Samples -or $Samples.Count -eq 0) { return 0 }
    [math]::Round(($Samples | Measure-Object -Property $Property -Average).Average, 2)
}

$history = New-Object System.Collections.Generic.List[object]
$maxWindowSeconds = 300
$maxWindowSamples = [math]::Ceiling($maxWindowSeconds / $IntervalSeconds)

while ($true) {
    $sample = Get-Sample
    $history.Add($sample)
    if ($history.Count -gt $maxWindowSamples) {
        $history.RemoveAt(0)
    }

    $cut10 = $sample.Timestamp.AddSeconds(-10)
    $cut60 = $sample.Timestamp.AddSeconds(-60)
    $cut300 = $sample.Timestamp.AddSeconds(-300)

    $samples10 = $history | Where-Object { $_.Timestamp -ge $cut10 }
    $samples60 = $history | Where-Object { $_.Timestamp -ge $cut60 }
    $samples300 = $history | Where-Object { $_.Timestamp -ge $cut300 }

    $cpu10 = Get-Average -Samples $samples10 -Property CpuPercent
    $cpu60 = Get-Average -Samples $samples60 -Property CpuPercent
    $cpu300 = Get-Average -Samples $samples300 -Property CpuPercent

    $ws10 = Get-Average -Samples $samples10 -Property WorkingSetPrivateMB
    $ws60 = Get-Average -Samples $samples60 -Property WorkingSetPrivateMB
    $ws300 = Get-Average -Samples $samples300 -Property WorkingSetPrivateMB

    $priv10 = Get-Average -Samples $samples10 -Property PrivateBytesMB
    $priv60 = Get-Average -Samples $samples60 -Property PrivateBytesMB
    $priv300 = Get-Average -Samples $samples300 -Property PrivateBytesMB

    $ts = $sample.Timestamp.ToString("HH:mm:ss")
    Clear-Host
    Write-Host "=== DGT Add-in Performance Dashboard ===" -ForegroundColor Cyan
    Write-Host "Excel PID $ExcelPid (instance: $instanceName) | Interval: $IntervalSeconds sec | Ctrl+C to stop" -ForegroundColor Green
    Write-Host ""
    Write-Host "Current" -ForegroundColor White
    Write-Host ("Time {0}  CPU {1,6:N2}%  WSPriv {2,8:N2} MB  WS {3,8:N2} MB  Priv {4,8:N2} MB  H {5,6}  T {6,4}" -f `
        $ts, $sample.CpuPercent, $sample.WorkingSetPrivateMB, $sample.WorkingSetMB, $sample.PrivateBytesMB, $sample.HandleCount, $sample.ThreadCount)
    Write-Host ""
    Write-Host "Averages (CPU% | WSPriv MB | Priv MB)" -ForegroundColor White
    Write-Host ("Last 10s : {0,6:N2}% | {1,8:N2} | {2,8:N2}" -f $cpu10, $ws10, $priv10)
    Write-Host ("Last 1m  : {0,6:N2}% | {1,8:N2} | {2,8:N2}" -f $cpu60, $ws60, $priv60)
    Write-Host ("Last 5m  : {0,6:N2}% | {1,8:N2} | {2,8:N2}" -f $cpu300, $ws300, $priv300)
    Write-Host ""
    Write-Host "Note: This captures Excel process usage as a proxy for add-in performance." -ForegroundColor Yellow
}
