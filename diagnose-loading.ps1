#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Diagnoses why DGT add-in is not loading in Excel
.DESCRIPTION
    Comprehensive diagnostic to identify add-in loading issues
#>

Write-Host "=== DGT Add-in Loading Diagnostic ===" -ForegroundColor Cyan
Write-Host ""

$xllPath = ".\src\DominoGovernanceTracker\bin\Debug\net472\DominoGovernanceTracker-AddIn.xll"
$xll64Path = ".\src\DominoGovernanceTracker\bin\Debug\net472\DominoGovernanceTracker-AddIn64.xll"

# 1. Check if XLL exists
Write-Host "1. Checking XLL Files..." -ForegroundColor Yellow
if (Test-Path $xllPath) {
    $xllInfo = Get-Item $xllPath
    Write-Host "   [OK] 32-bit XLL exists" -ForegroundColor Green
    Write-Host "       Size: $($xllInfo.Length) bytes" -ForegroundColor Gray
    Write-Host "       Modified: $($xllInfo.LastWriteTime)" -ForegroundColor Gray
} else {
    Write-Host "   [FAIL] 32-bit XLL not found!" -ForegroundColor Red
}

if (Test-Path $xll64Path) {
    $xll64Info = Get-Item $xll64Path
    Write-Host "   [OK] 64-bit XLL exists" -ForegroundColor Green
    Write-Host "       Size: $($xll64Info.Length) bytes" -ForegroundColor Gray
} else {
    Write-Host "   [WARN] 64-bit XLL not found" -ForegroundColor Yellow
}
Write-Host ""

# 2. Check Excel version
Write-Host "2. Checking Excel..." -ForegroundColor Yellow
$excelPath = "${env:ProgramFiles}\Microsoft Office\root\Office16\EXCEL.EXE"
if (Test-Path $excelPath) {
    $excelVersion = (Get-Item $excelPath).VersionInfo
    Write-Host "   [OK] Excel found" -ForegroundColor Green
    Write-Host "       Path: $excelPath" -ForegroundColor Gray
    Write-Host "       Version: $($excelVersion.FileVersion)" -ForegroundColor Gray

    # Check if 64-bit
    $is64bit = [System.Environment]::Is64BitOperatingSystem
    Write-Host "       OS is 64-bit: $is64bit" -ForegroundColor Gray
} else {
    Write-Host "   [FAIL] Excel not found at standard location" -ForegroundColor Red
}
Write-Host ""

# 3. Check if Excel is running
Write-Host "3. Checking Excel Process..." -ForegroundColor Yellow
$excelProcess = Get-Process -Name EXCEL -ErrorAction SilentlyContinue
if ($excelProcess) {
    Write-Host "   [OK] Excel is running" -ForegroundColor Green
    Write-Host "       PID: $($excelProcess.Id)" -ForegroundColor Gray
    Write-Host "       Path: $($excelProcess.Path)" -ForegroundColor Gray

    # Check loaded modules for our XLL
    $modules = $excelProcess.Modules | Where-Object { $_.FileName -like "*DominoGovernanceTracker*" }
    if ($modules) {
        Write-Host "       [OK] DGT XLL is loaded!" -ForegroundColor Green
        $modules | ForEach-Object {
            Write-Host "           $($_.FileName)" -ForegroundColor White
        }
    } else {
        Write-Host "       [FAIL] DGT XLL is NOT loaded in Excel!" -ForegroundColor Red
        Write-Host "       This is the problem - Excel is not loading the add-in" -ForegroundColor Yellow
    }
} else {
    Write-Host "   [--] Excel is not running" -ForegroundColor Yellow
}
Write-Host ""

# 4. Check Excel add-ins registry
Write-Host "4. Checking Excel Add-ins Registry..." -ForegroundColor Yellow
$openKeys = @(
    "HKCU:\Software\Microsoft\Office\16.0\Excel\Options",
    "HKCU:\Software\Microsoft\Office\Excel\Addins"
)

foreach ($keyPath in $openKeys) {
    if (Test-Path $keyPath) {
        Write-Host "   Checking: $keyPath" -ForegroundColor Gray
        $key = Get-Item $keyPath
        $openValues = $key.GetValueNames() | Where-Object { $_ -like "OPEN*" }

        if ($openValues) {
            $found = $false
            foreach ($valueName in $openValues) {
                $value = $key.GetValue($valueName)
                if ($value -like "*DominoGovernanceTracker*") {
                    Write-Host "       [OK] Found in registry: $valueName = $value" -ForegroundColor Green
                    $found = $true
                }
            }
            if (-not $found) {
                Write-Host "       [WARN] DGT not found in OPEN keys" -ForegroundColor Yellow
            }
        } else {
            Write-Host "       [--] No OPEN keys found" -ForegroundColor Gray
        }
    }
}
Write-Host ""

# 5. Check Excel Trust Center settings
Write-Host "5. Checking Trust Settings..." -ForegroundColor Yellow
$trustKey = "HKCU:\Software\Microsoft\Office\16.0\Excel\Security"
if (Test-Path $trustKey) {
    $trust = Get-ItemProperty $trustKey -ErrorAction SilentlyContinue
    if ($trust.VBAWarnings) {
        Write-Host "   VBA Macro Security: $($trust.VBAWarnings)" -ForegroundColor Gray
        if ($trust.VBAWarnings -eq 1) {
            Write-Host "       [OK] Enable all macros (not recommended)" -ForegroundColor Green
        } elseif ($trust.VBAWarnings -eq 2) {
            Write-Host "       [WARN] Disable macros with notification" -ForegroundColor Yellow
        } elseif ($trust.VBAWarnings -eq 3) {
            Write-Host "       [WARN] Disable macros except digitally signed" -ForegroundColor Yellow
        } elseif ($trust.VBAWarnings -eq 4) {
            Write-Host "       [FAIL] Disable all macros (blocks add-ins!)" -ForegroundColor Red
        }
    }
}
Write-Host ""

# 6. Check dependencies
Write-Host "6. Checking .NET Framework..." -ForegroundColor Yellow
$dotnetVersion = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if ($dotnetVersion) {
    $release = $dotnetVersion.Release
    Write-Host "   [OK] .NET Framework 4.x installed" -ForegroundColor Green
    Write-Host "       Release: $release" -ForegroundColor Gray
    if ($release -ge 461808) {
        Write-Host "       [OK] Version 4.7.2+ detected (compatible)" -ForegroundColor Green
    } else {
        Write-Host "       [WARN] May need .NET Framework 4.7.2 or higher" -ForegroundColor Yellow
    }
} else {
    Write-Host "   [FAIL] .NET Framework 4.x not found!" -ForegroundColor Red
}
Write-Host ""

# 7. Test if XLL can be loaded
Write-Host "7. Testing XLL File..." -ForegroundColor Yellow
if (Test-Path $xllPath) {
    try {
        # Check if it's a valid PE file
        $bytes = [System.IO.File]::ReadAllBytes($xllPath)
        if ($bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A) {
            Write-Host "   [OK] Valid PE (executable) file" -ForegroundColor Green
        } else {
            Write-Host "   [FAIL] Not a valid executable!" -ForegroundColor Red
        }

        # Check file size
        if ($bytes.Length -gt 1MB) {
            Write-Host "   [OK] File size looks reasonable ($([math]::Round($bytes.Length / 1MB, 2)) MB)" -ForegroundColor Green
        } else {
            Write-Host "   [WARN] File is very small ($([math]::Round($bytes.Length / 1KB, 2)) KB)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "   [FAIL] Cannot read XLL file: $_" -ForegroundColor Red
    }
}
Write-Host ""

# 8. Final recommendations
Write-Host "=== DIAGNOSIS ===" -ForegroundColor Cyan
Write-Host ""

if (-not $excelProcess) {
    Write-Host "Excel is not running. Start Excel and try loading the add-in:" -ForegroundColor Yellow
    Write-Host "  1. Open Excel" -ForegroundColor Gray
    Write-Host "  2. File > Options > Add-ins" -ForegroundColor Gray
    Write-Host "  3. Manage: Excel Add-ins > Go" -ForegroundColor Gray
    Write-Host "  4. Browse to: $xllPath" -ForegroundColor Gray
    Write-Host "  5. Check the box next to DominoGovernanceTracker-AddIn" -ForegroundColor Gray
} elseif (-not ($excelProcess.Modules | Where-Object { $_.FileName -like "*DominoGovernanceTracker*" })) {
    Write-Host "[CRITICAL] The add-in is NOT loaded in Excel!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Possible causes:" -ForegroundColor Yellow
    Write-Host "  1. Add-in not registered (manually add via File > Options > Add-ins)" -ForegroundColor Gray
    Write-Host "  2. Excel Trust Center blocking it (check macro security settings)" -ForegroundColor Gray
    Write-Host "  3. Missing dependencies (check .NET Framework)" -ForegroundColor Gray
    Write-Host "  4. XLL file is corrupted (rebuild with: .\run-debug.ps1 -Clean)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Close Excel completely" -ForegroundColor White
    Write-Host "  2. In Excel: File > Options > Trust Center > Trust Center Settings" -ForegroundColor White
    Write-Host "  3. Macro Settings > Enable all macros (for testing)" -ForegroundColor White
    Write-Host "  4. Trusted Locations > Add the folder: $(Split-Path $xllPath)" -ForegroundColor White
    Write-Host "  5. Restart Excel and manually load the add-in" -ForegroundColor White
} else {
    Write-Host "[OK] Add-in appears to be loaded!" -ForegroundColor Green
    Write-Host "If ribbon still doesn't show, there may be an error during initialization." -ForegroundColor Yellow
    Write-Host "Check logs at: $env:LOCALAPPDATA\DominoGovernanceTracker\logs\" -ForegroundColor Gray
}
Write-Host ""
