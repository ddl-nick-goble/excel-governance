#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Checks if DGT add-in is properly installed and loaded
.DESCRIPTION
    Diagnostic script to verify DGT add-in status
.EXAMPLE
    .\check-addin.ps1
#>

Write-Host "=== DGT Add-in Diagnostic Check ===" -ForegroundColor Cyan
Write-Host ""

# Check if Excel is running
$excelProcess = Get-Process -Name EXCEL -ErrorAction SilentlyContinue
if ($excelProcess) {
    Write-Host "[OK] Excel is running (PID: $($excelProcess.Id))" -ForegroundColor Green
} else {
    Write-Host "[--] Excel is not running" -ForegroundColor Yellow
}
Write-Host ""

# Check build output
$xllPath = ".\src\DominoGovernanceTracker\bin\Debug\net472\DominoGovernanceTracker-AddIn.xll"
if (Test-Path $xllPath) {
    $xllInfo = Get-Item $xllPath
    Write-Host "[OK] XLL file exists" -ForegroundColor Green
    Write-Host "    Path: $($xllInfo.FullName)" -ForegroundColor Gray
    Write-Host "    Size: $('{0:N0}' -f ($xllInfo.Length / 1KB)) KB" -ForegroundColor Gray
    Write-Host "    Modified: $($xllInfo.LastWriteTime)" -ForegroundColor Gray
} else {
    Write-Host "[FAIL] XLL file not found at: $xllPath" -ForegroundColor Red
    Write-Host "    Run .\run-debug.ps1 to build" -ForegroundColor Yellow
}
Write-Host ""

# Check logs
$logDir = Join-Path $env:LOCALAPPDATA "DominoGovernanceTracker\logs"
$todayLog = Join-Path $logDir "dgt-$(Get-Date -Format 'yyyyMMdd').log"

if (Test-Path $todayLog) {
    $logInfo = Get-Item $todayLog
    Write-Host "[OK] Log file exists" -ForegroundColor Green
    Write-Host "    Path: $todayLog" -ForegroundColor Gray
    Write-Host "    Size: $('{0:N0}' -f ($logInfo.Length / 1KB)) KB" -ForegroundColor Gray
    Write-Host "    Last modified: $($logInfo.LastWriteTime)" -ForegroundColor Gray
    Write-Host ""

    # Check for key log messages
    $logContent = Get-Content $todayLog -Raw

    if ($logContent -match "DGT Add-in Starting") {
        Write-Host "    [OK] Add-in initialization started" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Add-in initialization NOT found in logs" -ForegroundColor Red
    }

    if ($logContent -match "Tracking System Initialized Successfully") {
        Write-Host "    [OK] Tracking system initialized" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Tracking system NOT initialized" -ForegroundColor Yellow
    }

    if ($logContent -match "Event tracking started") {
        Write-Host "    [OK] Event tracking is active" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Event tracking NOT started" -ForegroundColor Yellow
    }

    if ($logContent -match "Ribbon loaded") {
        Write-Host "    [OK] Ribbon UI loaded" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Ribbon UI NOT loaded" -ForegroundColor Yellow
    }

    # Check for errors
    $errors = ($logContent -split "`n" | Where-Object { $_ -match "ERR|Error|Exception" }).Count
    if ($errors -gt 0) {
        Write-Host ""
        Write-Host "    [WARN] Found $errors error(s) in log:" -ForegroundColor Red
        Get-Content $todayLog | Where-Object { $_ -match "ERR|Error|Exception" } | Select-Object -First 5 | ForEach-Object {
            Write-Host "        $_" -ForegroundColor Red
        }
    }
} else {
    Write-Host "[FAIL] Log file not found" -ForegroundColor Red
    Write-Host "    Expected: $todayLog" -ForegroundColor Gray
    Write-Host "    This means the add-in has not started yet" -ForegroundColor Yellow
}
Write-Host ""

# Check buffer file
$bufferPath = Join-Path $env:LOCALAPPDATA "DominoGovernanceTracker\buffer.jsonl"
if (Test-Path $bufferPath) {
    $bufferInfo = Get-Item $bufferPath
    Write-Host "[WARN] Buffer file exists (API may be unreachable)" -ForegroundColor Yellow
    Write-Host "    Path: $bufferPath" -ForegroundColor Gray
    Write-Host "    Size: $('{0:N0}' -f ($bufferInfo.Length / 1KB)) KB" -ForegroundColor Gray
    Write-Host "    Events buffered: $((Get-Content $bufferPath | Measure-Object -Line).Lines)" -ForegroundColor Gray
} else {
    Write-Host "[OK] No buffer file (API is reachable or no events yet)" -ForegroundColor Green
}
Write-Host ""

# Check config
$configPath = ".\src\DominoGovernanceTracker\bin\Debug\net472\config.json"
if (Test-Path $configPath) {
    Write-Host "[OK] Config file exists" -ForegroundColor Green
    $config = Get-Content $configPath | ConvertFrom-Json
    Write-Host "    API Endpoint: $($config.apiEndpoint)" -ForegroundColor Gray
    Write-Host "    Tracking Enabled: $($config.trackingEnabled)" -ForegroundColor Gray
    Write-Host "    Include Formulas: $($config.includeFormulas)" -ForegroundColor Gray
} else {
    Write-Host "[FAIL] Config file not found at: $configPath" -ForegroundColor Red
}
Write-Host ""

Write-Host "=== Recommendations ===" -ForegroundColor Cyan
if (-not (Test-Path $todayLog)) {
    Write-Host "The add-in is not loading. Try:" -ForegroundColor Yellow
    Write-Host "  1. Close Excel completely" -ForegroundColor Gray
    Write-Host "  2. Run: .\run-debug.ps1" -ForegroundColor Gray
    Write-Host "  3. Watch the log monitor window for errors" -ForegroundColor Gray
} elseif (-not $excelProcess) {
    Write-Host "Start Excel and check for the DGT tab in the ribbon" -ForegroundColor Yellow
} else {
    Write-Host "Check Excel: File > Options > Add-ins" -ForegroundColor Yellow
    Write-Host "  - Ensure DominoGovernanceTracker-AddIn is checked" -ForegroundColor Gray
    Write-Host "  - If not listed, click Browse and add the .xll file" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Run .\view-logs.ps1 to see live logs" -ForegroundColor Yellow
}
Write-Host ""
