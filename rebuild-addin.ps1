# rebuild-addin.ps1 - Safely rebuild the Excel add-in
Write-Host "=== DGT Add-in Rebuild Script ===" -ForegroundColor Cyan
Write-Host ""

# Check if Excel is running
$excelProcesses = Get-Process -Name "EXCEL" -ErrorAction SilentlyContinue
if ($excelProcesses) {
    Write-Host "WARNING: Excel is currently running!" -ForegroundColor Yellow
    Write-Host "Found $($excelProcesses.Count) Excel process(es):" -ForegroundColor Yellow
    $excelProcesses | ForEach-Object { Write-Host "  - PID: $($_.Id)" -ForegroundColor Yellow }
    Write-Host ""

    $response = Read-Host "Would you like to close Excel and continue? (y/n)"
    if ($response -eq 'y' -or $response -eq 'Y') {
        Write-Host "Closing Excel..." -ForegroundColor Yellow
        Stop-Process -Name "EXCEL" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    } else {
        Write-Host "Aborting rebuild. Please close Excel manually and try again." -ForegroundColor Red
        exit 1
    }
}

Write-Host "Cleaning project..." -ForegroundColor Green
dotnet clean
if ($LASTEXITCODE -ne 0) {
    Write-Host "Clean failed!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Building project..." -ForegroundColor Green
dotnet build
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== Build Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Packed add-in locations:" -ForegroundColor Cyan
Write-Host "  32-bit: src\DominoGovernanceTracker\bin\Debug\net472\publish\DominoGovernanceTracker-AddIn-packed.xll" -ForegroundColor White
Write-Host "  64-bit: src\DominoGovernanceTracker\bin\Debug\net472\publish\DominoGovernanceTracker-AddIn64-packed.xll" -ForegroundColor White
Write-Host ""
Write-Host "Use the 64-bit version for testing." -ForegroundColor Yellow
