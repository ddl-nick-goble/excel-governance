#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds and runs DGT add-in in debug mode with Excel
.DESCRIPTION
    This script builds the DGT project, copies the XLL to a convenient location,
    and launches Excel with the add-in loaded for debugging.
.PARAMETER Clean
    Clean build (removes bin/obj folders first)
.PARAMETER Configuration
    Build configuration (Debug or Release)
.EXAMPLE
    .\run-debug.ps1
.EXAMPLE
    .\run-debug.ps1 -Clean
#>

param(
    [switch]$Clean,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

Write-Host "=== DominoGovernanceTracker Build & Debug ===" -ForegroundColor Cyan
Write-Host ""

# Paths
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "src\DominoGovernanceTracker\DominoGovernanceTracker.csproj"
$outputDir = Join-Path $projectRoot "src\DominoGovernanceTracker\bin\$Configuration\net472"
$xllFileName = "DominoGovernanceTracker-AddIn.xll"
$xllPath = Join-Path $outputDir $xllFileName

# Check if project file exists
if (-not (Test-Path $projectFile)) {
    Write-Host "ERROR: Project file not found at $projectFile" -ForegroundColor Red
    exit 1
}

# Clean if requested
if ($Clean) {
    Write-Host "Cleaning build artifacts..." -ForegroundColor Yellow
    $binPath = Join-Path $projectRoot "src\DominoGovernanceTracker\bin"
    $objPath = Join-Path $projectRoot "src\DominoGovernanceTracker\obj"

    if (Test-Path $binPath) { Remove-Item $binPath -Recurse -Force }
    if (Test-Path $objPath) { Remove-Item $objPath -Recurse -Force }

    Write-Host "Clean complete" -ForegroundColor Green
    Write-Host ""
}

# Build the project
Write-Host "Building DGT add-in ($Configuration)..." -ForegroundColor Yellow
Write-Host "Project: $projectFile" -ForegroundColor Gray

try {
    # Use MSBuild directly (dotnet build may not work for .NET Framework on all machines)
    $msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
        -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe `
        -prerelease | Select-Object -First 1

    if (-not $msbuildPath) {
        Write-Host "WARNING: MSBuild not found via vswhere, trying dotnet build..." -ForegroundColor Yellow
        $buildCmd = "dotnet"
        $buildArgs = @("build", $projectFile, "-c", $Configuration, "-v", "minimal")
    } else {
        Write-Host "Using MSBuild: $msbuildPath" -ForegroundColor Gray
        $buildCmd = $msbuildPath
        $buildArgs = @($projectFile, "/p:Configuration=$Configuration", "/v:minimal", "/nologo")
    }

    & $buildCmd $buildArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }

    Write-Host "Build successful!" -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host "ERROR: Build failed - $_" -ForegroundColor Red
    exit 1
}

# Check if XLL was created
if (-not (Test-Path $xllPath)) {
    Write-Host "ERROR: XLL file not found at $xllPath" -ForegroundColor Red
    Write-Host "Build may have succeeded but ExcelDNA packaging failed" -ForegroundColor Yellow
    exit 1
}

Write-Host "XLL created: $xllPath" -ForegroundColor Green
$xllSize = (Get-Item $xllPath).Length / 1KB
Write-Host "Size: $([math]::Round($xllSize, 2)) KB" -ForegroundColor Gray
Write-Host ""

# Find Excel
Write-Host "Locating Excel..." -ForegroundColor Yellow
$excelPaths = @(
    "${env:ProgramFiles}\Microsoft Office\root\Office16\EXCEL.EXE",
    "${env:ProgramFiles(x86)}\Microsoft Office\root\Office16\EXCEL.EXE",
    "${env:ProgramFiles}\Microsoft Office\Office16\EXCEL.EXE",
    "${env:ProgramFiles(x86)}\Microsoft Office\Office16\EXCEL.EXE"
)

$excelPath = $excelPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $excelPath) {
    Write-Host "ERROR: Excel not found in standard locations" -ForegroundColor Red
    Write-Host "Please start Excel manually and load the XLL from:" -ForegroundColor Yellow
    Write-Host "  $xllPath" -ForegroundColor White
    exit 1
}

Write-Host "Excel found: $excelPath" -ForegroundColor Green
Write-Host ""

# Launch Excel with XLL
Write-Host "Launching Excel with DGT add-in..." -ForegroundColor Cyan
Write-Host "To debug in Visual Studio Code:" -ForegroundColor Yellow
Write-Host "  1. Set breakpoints in your code" -ForegroundColor Gray
Write-Host "  2. In VSCode, press F5 or use Debug > Attach to Process" -ForegroundColor Gray
Write-Host "  3. Select the EXCEL.EXE process" -ForegroundColor Gray
Write-Host ""

try {
    # Get log and buffer paths
    $logDir = Join-Path $env:LOCALAPPDATA "DominoGovernanceTracker\logs"
    $bufferPath = Join-Path $env:LOCALAPPDATA "DominoGovernanceTracker\buffer.jsonl"
    $todayLog = Join-Path $logDir "dgt-$(Get-Date -Format 'yyyyMMdd').log"

    # Create log directory if it doesn't exist
    if (-not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    # Start log monitor in a separate window
    $monitorScript = @"
`$Host.UI.RawUI.WindowTitle = 'DGT Live Logs'
Write-Host '=== DominoGovernanceTracker Live Log Monitor ===' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Monitoring: $todayLog' -ForegroundColor Yellow
Write-Host 'Press Ctrl+C to stop monitoring' -ForegroundColor Gray
Write-Host ''
Write-Host '--- LOG OUTPUT ---' -ForegroundColor Green
Write-Host ''

# Show last 20 lines if file exists
if (Test-Path '$todayLog') {
    Get-Content '$todayLog' -Tail 20 | ForEach-Object {
        if (`$_ -match 'ERR|Error|Exception') {
            Write-Host `$_ -ForegroundColor Red
        } elseif (`$_ -match 'WRN|Warning') {
            Write-Host `$_ -ForegroundColor Yellow
        } elseif (`$_ -match 'INF|Information') {
            Write-Host `$_ -ForegroundColor White
        } else {
            Write-Host `$_ -ForegroundColor Gray
        }
    }
    Write-Host ''
    Write-Host '--- LIVE UPDATES ---' -ForegroundColor Green
}

# Tail the log file (live updates)
try {
    Get-Content '$todayLog' -Wait -Tail 0 | ForEach-Object {
        if (`$_ -match 'ERR|Error|Exception') {
            Write-Host `$_ -ForegroundColor Red
        } elseif (`$_ -match 'WRN|Warning') {
            Write-Host `$_ -ForegroundColor Yellow
        } elseif (`$_ -match 'INF|Information') {
            Write-Host `$_ -ForegroundColor White
        } else {
            Write-Host `$_ -ForegroundColor Gray
        }
    }
} catch {
    Write-Host 'Log file not created yet. Waiting...' -ForegroundColor Yellow
    Start-Sleep -Seconds 1
}
"@

    # Launch log monitor window
    Start-Process powershell -ArgumentList "-NoExit", "-Command", $monitorScript

    # Small delay to let monitor window open
    Start-Sleep -Milliseconds 500

    # Start Excel with the XLL loaded as an add-in (not as a file to open)
    # The /x parameter tells Excel to load the XLL as an add-in
    Start-Process -FilePath $excelPath -ArgumentList "/x `"$xllPath`""

    Write-Host "Excel launched successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Live log monitor opened in separate window!" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "What to check:" -ForegroundColor Yellow
    Write-Host "  1. Log monitor window - watch for initialization messages" -ForegroundColor Gray
    Write-Host "  2. Excel ribbon - look for 'DGT' tab (after View tab)" -ForegroundColor Gray
    Write-Host "  3. DGT tab should show 'Status: Active' and event counter" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Log file location:" -ForegroundColor Yellow
    Write-Host "  $todayLog" -ForegroundColor White
    Write-Host ""
    Write-Host "Buffer file location:" -ForegroundColor Yellow
    Write-Host "  $bufferPath" -ForegroundColor White
    Write-Host ""
    Write-Host "If you don't see the DGT tab:" -ForegroundColor Red
    Write-Host "  - Check the log monitor for errors" -ForegroundColor Gray
    Write-Host "  - Look for 'DGT Add-in Starting' message" -ForegroundColor Gray
    Write-Host "  - Check if add-in is enabled: File > Options > Add-ins" -ForegroundColor Gray
    Write-Host ""
} catch {
    Write-Host "ERROR: Failed to launch Excel - $_" -ForegroundColor Red
    exit 1
}

Write-Host "=== Debug session started ===" -ForegroundColor Green
