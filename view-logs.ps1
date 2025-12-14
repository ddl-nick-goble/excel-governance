#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Live log viewer for DGT add-in
.DESCRIPTION
    Displays DGT logs in real-time with color-coded output
.EXAMPLE
    .\view-logs.ps1
#>

$ErrorActionPreference = "Stop"

# Get log path
$logDir = Join-Path $env:LOCALAPPDATA "DominoGovernanceTracker\logs"
$todayLog = Join-Path $logDir "dgt-$(Get-Date -Format 'yyyyMMdd').log"
$bufferPath = Join-Path $env:LOCALAPPDATA "DominoGovernanceTracker\buffer.jsonl"

Clear-Host
Write-Host "=== DominoGovernanceTracker Live Log Viewer ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Log file: $todayLog" -ForegroundColor Yellow
Write-Host "Buffer:   $bufferPath" -ForegroundColor Yellow
Write-Host ""
Write-Host "Press Ctrl+C to stop" -ForegroundColor Gray
Write-Host ""

# Check if log file exists
if (-not (Test-Path $todayLog)) {
    Write-Host "WARNING: Log file doesn't exist yet!" -ForegroundColor Red
    Write-Host "Either DGT hasn't started, or logs are in a different file." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Available log files:" -ForegroundColor Cyan
    if (Test-Path $logDir) {
        Get-ChildItem $logDir -Filter "dgt-*.log" | ForEach-Object {
            Write-Host "  $($_.Name) - $('{0:N0}' -f ($_.Length / 1KB)) KB" -ForegroundColor White
        }
    } else {
        Write-Host "  No logs directory found at: $logDir" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "Waiting for log file to be created..." -ForegroundColor Yellow

    # Wait for file to be created (max 30 seconds)
    $timeout = 30
    $elapsed = 0
    while (-not (Test-Path $todayLog) -and $elapsed -lt $timeout) {
        Start-Sleep -Seconds 1
        $elapsed++
        Write-Host "." -NoNewline -ForegroundColor Gray
    }
    Write-Host ""

    if (-not (Test-Path $todayLog)) {
        Write-Host ""
        Write-Host "ERROR: Log file not created after $timeout seconds" -ForegroundColor Red
        Write-Host "DGT add-in may not be loading correctly." -ForegroundColor Yellow
        exit 1
    }
}

Write-Host "--- LAST 30 LINES ---" -ForegroundColor Green
Write-Host ""

# Show last 30 lines with color coding if file exists
if (Test-Path $todayLog) {
    Get-Content $todayLog -Tail 30 | ForEach-Object {
    if ($_ -match 'ERR|Error|Exception|FATAL') {
        Write-Host $_ -ForegroundColor Red
    } elseif ($_ -match 'WRN|Warning') {
        Write-Host $_ -ForegroundColor Yellow
    } elseif ($_ -match 'INF|Information|Starting|Started|Initialized|Success') {
        Write-Host $_ -ForegroundColor Green
    } elseif ($_ -match 'DBG|Debug') {
        Write-Host $_ -ForegroundColor DarkGray
    } else {
        Write-Host $_ -ForegroundColor White
    }
    }
} else {
    Write-Host "Log file not created yet - add-in has not started" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "--- LIVE UPDATES (streaming) ---" -ForegroundColor Cyan
Write-Host ""

# Stream new log entries
try {
    Get-Content $todayLog -Wait -Tail 0 | ForEach-Object {
        $timestamp = Get-Date -Format "HH:mm:ss"

        if ($_ -match 'ERR|Error|Exception|FATAL') {
            Write-Host "[$timestamp] " -NoNewline -ForegroundColor DarkGray
            Write-Host $_ -ForegroundColor Red
        } elseif ($_ -match 'WRN|Warning') {
            Write-Host "[$timestamp] " -NoNewline -ForegroundColor DarkGray
            Write-Host $_ -ForegroundColor Yellow
        } elseif ($_ -match 'INF|Information|Starting|Started|Initialized|Success') {
            Write-Host "[$timestamp] " -NoNewline -ForegroundColor DarkGray
            Write-Host $_ -ForegroundColor Green
        } elseif ($_ -match 'DBG|Debug') {
            Write-Host "[$timestamp] " -NoNewline -ForegroundColor DarkGray
            Write-Host $_ -ForegroundColor DarkGray
        } else {
            Write-Host "[$timestamp] " -NoNewline -ForegroundColor DarkGray
            Write-Host $_ -ForegroundColor White
        }

        # Show buffer file size every 10 log lines
        if ((Get-Random -Maximum 10) -eq 0 -and (Test-Path $bufferPath)) {
            $bufferSize = (Get-Item $bufferPath).Length
            if ($bufferSize -gt 0) {
                Write-Host "    [Buffer: $('{0:N0}' -f ($bufferSize / 1KB)) KB]" -ForegroundColor Magenta
            }
        }
    }
} catch {
    Write-Host ""
    Write-Host "ERROR: Failed to tail log file - $_" -ForegroundColor Red
    exit 1
}
